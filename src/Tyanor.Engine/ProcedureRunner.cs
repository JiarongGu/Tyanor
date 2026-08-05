namespace Tyanor.Engine;

/// <summary>Bounded retry policy for transient provider errors.</summary>
/// <param name="Attempts">Total tries, including the first.</param>
/// <param name="BaseDelay">First backoff; doubles each attempt.</param>
public sealed record RetryPolicy(int Attempts = 5, TimeSpan? BaseDelay = null)
{
    /// <summary>The delay before attempt <paramref name="attempt"/> (1-based).</summary>
    public TimeSpan DelayFor(int attempt) =>
        TimeSpan.FromMilliseconds((BaseDelay ?? TimeSpan.FromMilliseconds(500)).TotalMilliseconds * Math.Pow(2, attempt - 1));
}

/// <summary>
/// Runs a <see cref="Procedure"/> against an <see cref="IDeploymentTarget"/> — the whole of Tyanor's
/// execution model.
///
/// <para><b>There is no separate resume.</b> Applying a procedure and resuming an interrupted one are the
/// same call: each unit is reconciled against what the provider currently reports, so a unit that already
/// finished is skipped, one still converging is attached to, and one that broke is remade. That is why a
/// crash, a closed laptop, or a second operator running the same command all converge rather than
/// conflict — and why there is no state file to drift, lock or repair.</para>
///
/// <para><b>A stop is classified, not merely reported.</b> An expired credential and a malformed template
/// both end a run, but only one of them means the work so far was wasted; conflating them is how a tool
/// teaches people to distrust it. See <see cref="FailureClass"/>.</para>
/// </summary>
public sealed class ProcedureRunner(
    IDeploymentTarget target, IRunHistory history, IStateStore? state = null, RetryPolicy? retry = null)
{
    private readonly RetryPolicy _retry = retry ?? new RetryPolicy();

    /// <summary>
    /// Work out what <see cref="ApplyAsync"/> would do, WITHOUT doing any of it — a read-only pass that
    /// asks the provider for each unit's phase and runs the same decision the apply will run.
    ///
    /// <para>Because the plan is derived from the provider rather than from a stored model, it cannot be
    /// stale in the way a state-file plan can. It is still a forecast: see <see cref="Plan"/> for the two
    /// things it honestly cannot know.</para>
    /// </summary>
    /// <param name="procedure">The units, in apply order.</param>
    /// <param name="request">What would be deployed, and where.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<Plan> PlanAsync(Procedure procedure, DeploymentRequest request, CancellationToken ct = default)
    {
        var recorded = state is null ? null : await state.GetAsync(procedure.Name, request.Prefix, ct);
        var steps = new List<PlannedStep>();
        var drift = new List<Drift>();

        foreach (var unit in procedure.Forward())
        {
            ct.ThrowIfCancellationRequested();
            var phase = await WithRetryAsync(() => target.Driver.PhaseAsync(unit, request, ct), ct);
            steps.Add(new PlannedStep(unit, phase, Reconcile.Decide(phase)));

            // REFRESH: re-read what actually exists and compare it to what state records. This is what
            // makes the add/change/destroy counts real rather than a guess from configuration — and it is
            // why a state that has gone stale repairs itself instead of needing to be edited by hand.
            if (recorded is not null)
            {
                var actual = await WithRetryAsync(() => target.Driver.RefreshAsync(unit, request, ct), ct);
                drift.AddRange(StateDiff.ForUnit(unit.Name, recorded.For(unit.Name), actual));
            }
        }

        // Both halves of "is anything already happening here?" — the provider, and the record of intent.
        // With a shared history the second one spans machines, which is what makes running Tyanor from a
        // laptop and a pipeline against the same deployment a visible situation rather than a silent race.
        var active = await history.LiveAsync(procedure.Name, request.Prefix, ct);
        return new Plan(procedure.Name, request.Prefix, steps, active) { Drift = drift };
    }

    /// <summary>
    /// Re-sync state from the real deployment: ask the provider what exists for every unit and rewrite
    /// state to match, WITHOUT changing any infrastructure.
    ///
    /// <para>This is the repair for a mirror that has gone stale — state adopted from a deployment created
    /// before Tyanor, or one someone changed by hand. It is deliberately separate from
    /// <see cref="ApplyAsync"/> so that "make my records true" is never bundled with "change my
    /// infrastructure".</para>
    /// </summary>
    /// <param name="procedure">The units to refresh.</param>
    /// <param name="request">Which deployment.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The state as rewritten.</returns>
    /// <exception cref="InvalidOperationException">No state store was configured.</exception>
    public async Task<DeploymentState> RefreshAsync(Procedure procedure, DeploymentRequest request, CancellationToken ct = default)
    {
        if (state is null)
            throw new InvalidOperationException("Refresh needs a state store; this runner was built without one.");

        var current = await state.GetAsync(procedure.Name, request.Prefix, ct);
        foreach (var unit in procedure.Forward())
        {
            ct.ThrowIfCancellationRequested();
            var actual = await WithRetryAsync(() => target.Driver.RefreshAsync(unit, request, ct), ct);
            current = current.With(unit.Name, actual);
        }
        await state.SaveAsync(current, ct);
        return current;
    }

    /// <summary>
    /// Converge <paramref name="request"/> on the target, in unit order. Re-entrant: calling this again
    /// after a pause continues the same run.
    /// </summary>
    /// <param name="procedure">The units, in apply order.</param>
    /// <param name="request">What to deploy, and where.</param>
    /// <param name="report">Receives live progress lines.</param>
    /// <param name="runId">Pass the id of a paused run to continue it; omit to start a new one.</param>
    /// <param name="ct">Cancelling leaves the run LIVE — the provider may still be converging.</param>
    public async Task<OperationOutcome> ApplyAsync(
        Procedure procedure, DeploymentRequest request, Action<ProgressReport>? report = null,
        string? runId = null, CancellationToken ct = default)
        => await RunAsync(procedure, procedure.Forward().ToList(), RunKind.Apply, request, report ?? Ignore, runId, ct);

    /// <summary>
    /// Remove everything the procedure created, in REVERSE unit order so that whatever imports from a unit
    /// is gone before the unit itself.
    /// </summary>
    /// <param name="procedure">The units; removed in reverse of apply order.</param>
    /// <param name="request">Which deployment to remove.</param>
    /// <param name="report">Receives live progress lines.</param>
    /// <param name="runId">Pass the id of a paused teardown to continue it.</param>
    /// <param name="ct">Cancelling leaves the run LIVE.</param>
    public async Task<OperationOutcome> RemoveAsync(
        Procedure procedure, DeploymentRequest request, Action<ProgressReport>? report = null,
        string? runId = null, CancellationToken ct = default)
        => await RunAsync(procedure, procedure.Reverse().ToList(), RunKind.Remove, request, report ?? Ignore, runId, ct);

    /// <summary>Progress is optional — a script or a test may not want it. Nothing else changes.</summary>
    private static void Ignore(ProgressReport _) { }

    private async Task<OperationOutcome> RunAsync(
        Procedure procedure, IReadOnlyList<ProcedureUnit> units, RunKind kind,
        DeploymentRequest request, Action<ProgressReport> report, string? runId, CancellationToken ct)
    {
        // One id for the whole attempt INCLUDING its resumes — a resume continues a run rather than
        // starting one, or the history would show five failures where one interrupted job happened.
        //
        // With no id given, ADOPT a live run for this procedure + prefix if one exists. That is "resume is
        // a re-run" taken to its conclusion: the caller should not have to know whether they are starting
        // or continuing. With a shared history the live run may belong to another machine, and adopting it
        // is still right — the alternative is two competing records of one deployment, which is precisely
        // the out-of-sync state the plan exists to reveal.
        var id = runId
            ?? (await history.LiveAsync(procedure.Name, request.Prefix, ct))?.Id
            ?? Guid.NewGuid().ToString("N");
        var started = DateTimeOffset.UtcNow;
        var record = new RunRecord(id, procedure.Name, request.Prefix, kind, RunStatus.Running, started);
        await history.UpsertAsync(record, ct);

        var done = 0;
        try
        {
            foreach (var unit in units)
            {
                ct.ThrowIfCancellationRequested();
                if (kind == RunKind.Apply) await ConvergeAsync(unit, request, report, ct);
                else await RetireAsync(unit, request, report, ct);

                // Keep state current as we go, not once at the end: a run that pauses halfway has still
                // created things, and state that only lands on success would omit exactly the resources a
                // resumed or abandoned run most needs to know about.
                await RecordStateAsync(procedure, request, unit, removed: kind == RunKind.Remove, ct);

                done += unit.Weight;
                report(new ProgressReport(unit.Name, $"{unit.Label}: done.",
                    Percent(done, procedure.TotalWeight), ProgressStatus.Success));
            }

            await history.UpsertAsync(record with { Status = RunStatus.Succeeded, FinishedAt = DateTimeOffset.UtcNow }, ct);
            return OperationOutcome.Success();
        }
        catch (OperationCanceledException)
        {
            // Cancellation leaves the run LIVE on purpose. Whatever the provider had started is still
            // converging out there; marking this failed would hide work that is genuinely in flight.
            await history.UpsertAsync(record with { Status = RunStatus.Paused, Reason = PauseReason.External }, ct);
            throw;
        }
        catch (Exception ex)
        {
            var outcome = OperationOutcome.From(target.Classifier.Classify(ex) ?? FailureClass.Hard, ex.Message);
            report(new ProgressReport(procedure.Name, Explain(outcome), -1, ProgressStatus.Error));
            await history.UpsertAsync(record with
            {
                Status = outcome.Resumable ? RunStatus.Paused : RunStatus.Failed,
                FinishedAt = outcome.Resumable ? null : DateTimeOffset.UtcNow,
                Reason = outcome.Reason,
                Error = ex.Message,
            }, ct);
            return outcome;
        }
    }

    /// <summary>
    /// Bring ONE unit to the desired state — read the phase, decide, act. The decision is
    /// <see cref="Reconcile.Decide"/> and nothing else; this method only carries it out.
    /// </summary>
    private async Task ConvergeAsync(ProcedureUnit unit, DeploymentRequest request, Action<ProgressReport> report, CancellationToken ct)
    {
        var phase = await WithRetryAsync(() => target.Driver.PhaseAsync(unit, request, ct), ct);
        var action = Reconcile.Decide(phase);

        switch (action)
        {
            case ReconcileAction.Attach:
                // Someone else's operation is already converging this unit. Watch it; issue nothing.
                report(new ProgressReport(unit.Name, $"{unit.Label}: already in progress — resuming.", -1));
                await target.Driver.AwaitSettledAsync(unit, request, report, ct);
                return;

            case ReconcileAction.SettleThenRecreate:
                report(new ProgressReport(unit.Name, $"{unit.Label}: rolling back — waiting for it to settle.", -1));
                // The wait is EXPECTED to end in failure: that is what unwinding settles into. Swallow it
                // here and let the recreate below be the real attempt.
                try { await target.Driver.AwaitSettledAsync(unit, request, report, ct); }
                catch (OperationCanceledException) { throw; }
                catch { /* settled into a failed state, which is exactly what we were waiting for */ }
                goto case ReconcileAction.Recreate;

            case ReconcileAction.Recreate:
                report(new ProgressReport(unit.Name, $"{unit.Label}: cannot be updated in place — remaking it.", -1));
                await WithRetryAsync(() => target.Driver.RemoveAsync(unit, request, ct), ct);
                await CreateAndWaitAsync(unit, request, report, ct);
                return;

            case ReconcileAction.Update:
                report(new ProgressReport(unit.Name, $"{unit.Label}: updating…", -1));
                if (!await WithRetryAsync(() => target.Driver.UpdateAsync(unit, request, ct), ct))
                {
                    // "Nothing to change" is success, and on a resume it is the ordinary answer.
                    report(new ProgressReport(unit.Name, $"{unit.Label}: already up to date.", -1, ProgressStatus.Success));
                    return;
                }
                await target.Driver.AwaitSettledAsync(unit, request, report, ct);
                return;

            default:
                report(new ProgressReport(unit.Name, $"{unit.Label}: creating…", -1));
                await CreateAndWaitAsync(unit, request, report, ct);
                return;
        }
    }

    /// <summary>
    /// Record what this unit now holds, straight from the provider. Nothing is inferred from what we
    /// intended — state records what IS, which is the only version of it that stays true.
    /// </summary>
    private async Task RecordStateAsync(
        Procedure procedure, DeploymentRequest request, ProcedureUnit unit, bool removed, CancellationToken ct)
    {
        if (state is null) return;
        var current = await state.GetAsync(procedure.Name, request.Prefix, ct);
        var resources = removed
            ? []                                            // torn down: the unit owns nothing now
            : await WithRetryAsync(() => target.Driver.RefreshAsync(unit, request, ct), ct);
        await state.SaveAsync(current.With(unit.Name, resources), ct);
    }

    private async Task CreateAndWaitAsync(ProcedureUnit unit, DeploymentRequest request, Action<ProgressReport> report, CancellationToken ct)
    {
        await WithRetryAsync(() => target.Driver.CreateAsync(unit, request, ct), ct);
        await target.Driver.AwaitSettledAsync(unit, request, report, ct);
    }

    /// <summary>Remove one unit, tolerating one that is already gone — teardown must be re-runnable.</summary>
    private async Task RetireAsync(ProcedureUnit unit, DeploymentRequest request, Action<ProgressReport> report, CancellationToken ct)
    {
        var phase = await WithRetryAsync(() => target.Driver.PhaseAsync(unit, request, ct), ct);
        if (phase == UnitPhase.Missing)
        {
            report(new ProgressReport(unit.Name, $"{unit.Label}: already removed.", -1, ProgressStatus.Success));
            return;
        }
        report(new ProgressReport(unit.Name, $"{unit.Label}: removing…", -1));
        await WithRetryAsync(() => target.Driver.RemoveAsync(unit, request, ct), ct);
    }

    /// <summary>
    /// Retry TRANSIENT failures only. Credential and hard failures rethrow immediately: retrying an
    /// expired token just delays the moment someone can fix it, and retrying a malformed request is a lie
    /// told five times.
    /// </summary>
    private async Task<T> WithRetryAsync<T>(Func<Task<T>> op, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { return await op(); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (attempt < _retry.Attempts && target.Classifier.Classify(ex) == FailureClass.Transient)
            {
                await Task.Delay(_retry.DelayFor(attempt), ct);
            }
        }
    }

    private async Task WithRetryAsync(Func<Task> op, CancellationToken ct)
        => await WithRetryAsync(async () => { await op(); return true; }, ct);

    private static int Percent(int done, int total) => total <= 0 ? -1 : (int)Math.Round(100.0 * done / total);

    /// <summary>What to tell the operator. A pause says the work is kept, because it is.</summary>
    private static string Explain(OperationOutcome outcome) => outcome.Reason?.Value switch
    {
        "credentials" => "The provider rejected your credentials — they may have expired. Re-enter them and resume; the progress so far is kept.",
        "transient" => "A temporary provider error interrupted the run. You can resume — the progress so far is kept.",
        "external" => "The run is waiting on something outside the provider. You can resume once it is ready.",
        _ => "The run failed: " + (outcome.Error ?? "unknown error"),
    };
}
