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
    /// <param name="kind">
    /// Which direction to plan. <see cref="RunKind.Destroy"/> plans a TEARDOWN — the units in reverse order,
    /// which of them are already gone, and every resource the removal will destroy.
    /// <para>A teardown gets a plan because it is the operation that destroys things, and a safety gate that
    /// covers only the recoverable direction is not one.</para>
    /// </param>
    /// <param name="ct">Cancellation.</param>
    public async Task<Plan> PlanAsync(
        Procedure procedure, DeploymentRequest request,
        RunKind kind = RunKind.Apply, CancellationToken ct = default)
    {
        // Read once, for BOTH directions. Only an apply compares against state for drift — a teardown counts
        // what the provider actually has — but both need it to spot a unit state records and the procedure
        // no longer declares, and a destroy needs that most: it is the run after which the stranded thing is
        // all that is left.
        var recorded = state is null ? null : await state.GetAsync(procedure.Name, request.Prefix, ct);

        var steps = new List<PlannedStep>();
        var drift = new List<Drift>();
        var destroying = new List<Drift>();

        foreach (var unit in kind == RunKind.Apply ? procedure.Forward() : procedure.Reverse())
        {
            ct.ThrowIfCancellationRequested();
            // A plan is read-only, so nothing a driver says here is shown: progress belongs to a run.
            var context = new UnitContext(unit, request, Ignore, ct);

            var phase = await WithRetryAsync(() => target.Driver.PhaseAsync(context), ct);
            steps.Add(new PlannedStep(unit, phase,
                kind == RunKind.Apply ? Reconcile.Decide(phase) : Reconcile.DecideDestroy(phase)));

            // Drift is state-versus-reality, so it needs a state store. What a TEARDOWN will destroy is not:
            // it is read entirely from the provider, and gating it on a state store made a destroy plan of a
            // runner configured without one report "0 to destroy" and IsDestructive false — which is the
            // confirmation gate the README tells operators to put in front of the irreversible direction.
            if (kind == RunKind.Apply && recorded is null) continue;

            // REFRESH: re-read what actually exists and compare it to what state records. This is what
            // makes the add/change/destroy counts real rather than a guess from configuration — and it is
            // why a state that has gone stale repairs itself instead of needing to be edited by hand.
            var actual = await WithRetryAsync(() => target.Driver.RefreshAsync(context), ct);

            if (kind == RunKind.Apply)
            {
                drift.AddRange(StateDiff.ForUnit(unit.Name, recorded!.For(unit.Name), actual));
                continue;
            }

            // A teardown destroys what is ACTUALLY there, not what was once recorded. A resource already
            // deleted by hand is not something this run is about to take away, and counting it would inflate
            // the one number the operator is deciding on.
            destroying.AddRange(actual.Select(r => new Drift(unit.Name, r, ResourceChange.Destroy)));
        }

        // Both halves of "is anything already happening here?" — the provider, and the record of intent.
        // With a shared history the second one spans machines, which is what makes running Tyanor from a
        // laptop and a pipeline against the same deployment a visible situation rather than a silent race.
        var active = await history.LiveAsync(procedure.Name, request.Prefix, ct);
        return new Plan(procedure.Name, request.Prefix, steps, active)
        {
            Kind = kind,
            Drift = drift,
            Destroying = destroying,
            Orphaned = Orphaned(procedure, recorded),
        };
    }

    /// <summary>
    /// Check the whole procedure and request WITHOUT touching the target at all — no credentials, no
    /// network, nothing created.
    ///
    /// <para>The cheapest gate there is, and the only one that works before an account exists. Without it,
    /// a misconfigured unit is discovered by an apply that has already made two other units, and discovered
    /// one problem at a time; this returns every problem across every unit in one pass.</para>
    /// </summary>
    /// <param name="procedure">The units to check.</param>
    /// <param name="request">What would be deployed, and where.</param>
    /// <param name="ct">Cancellation.</param>
    /// <remarks>
    /// Says nothing about the world. A procedure can be perfectly valid and still fail to deploy because a
    /// bucket name is taken or a quota is reached — that is what <see cref="PlanAsync"/> and the run are for.
    /// </remarks>
    public async Task<Validation> ValidateAsync(
        Procedure procedure, DeploymentRequest request, CancellationToken ct = default)
    {
        var problems = new List<ValidationProblem>();

        foreach (var unit in procedure.Forward())
        {
            ct.ThrowIfCancellationRequested();
            // No retry: there is nothing transient about a definition, and nothing here should be reaching a
            // provider to fail transiently in the first place.
            var found = await target.Driver.ValidateAsync(new UnitContext(unit, request, Ignore, ct));
            problems.AddRange(found.Select(p => new ValidationProblem(unit.Name, p)));
        }

        return new Validation(problems);
    }

    /// <summary>
    /// What the deployment PRODUCED — a URL, an endpoint, a generated name — gathered from every unit.
    ///
    /// <para>The answer to "where is my site?", which is the question an operator has the moment an apply
    /// finishes. Read from the provider rather than from state, for the same reason phases are: what a
    /// deployment currently exposes is a fact about the deployment, and a stored copy is one more thing that
    /// can be wrong.</para>
    /// </summary>
    /// <param name="procedure">The units to ask.</param>
    /// <param name="request">Which deployment.</param>
    /// <param name="ct">Cancellation.</param>
    /// <remarks>
    /// Later units win a name collision, because they are the ones closer to the operator: an edge unit's
    /// <c>url</c> is the one someone visits, not the database's.
    /// </remarks>
    public async Task<IReadOnlyDictionary<string, string>> OutputsAsync(
        Procedure procedure, DeploymentRequest request, CancellationToken ct = default)
    {
        var outputs = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var unit in procedure.Forward())
        {
            ct.ThrowIfCancellationRequested();
            var context = new UnitContext(unit, request, Ignore, ct);
            foreach (var (key, value) in await WithRetryAsync(() => target.Driver.OutputsAsync(context), ct))
                outputs[key] = value;
        }

        return outputs;
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
            var context = new UnitContext(unit, request, Ignore, ct);
            var actual = await WithRetryAsync(() => target.Driver.RefreshAsync(context), ct);
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
    /// Destroy everything the procedure created, in REVERSE unit order so that whatever imports from a unit
    /// is gone before the unit itself.
    ///
    /// <para>Named for the operation an operator performs, the way <c>terraform destroy</c> is — while a
    /// DRIVER still says <see cref="IUnitDriver.RemoveAsync"/>, because it removes one unit rather than
    /// destroying a deployment. The asymmetry is the same one Terraform has between its command and a
    /// provider's per-resource delete, and it is worth keeping for the same reason: they are different jobs.
    /// Preview one with <see cref="PlanAsync"/> and <see cref="RunKind.Destroy"/>.</para>
    /// </summary>
    /// <param name="procedure">The units; removed in reverse of apply order.</param>
    /// <param name="request">Which deployment to remove.</param>
    /// <param name="report">Receives live progress lines.</param>
    /// <param name="runId">Pass the id of a paused teardown to continue it.</param>
    /// <param name="ct">Cancelling leaves the run LIVE.</param>
    public async Task<OperationOutcome> DestroyAsync(
        Procedure procedure, DeploymentRequest request, Action<ProgressReport>? report = null,
        string? runId = null, CancellationToken ct = default)
        => await RunAsync(procedure, procedure.Reverse().ToList(), RunKind.Destroy, request, report ?? Ignore, runId, ct);

    /// <summary>Progress is optional — a script or a test may not want it. Nothing else changes.</summary>
    private static void Ignore(ProgressReport _) { }

    /// <summary>
    /// Units state records that the procedure no longer declares.
    /// </summary>
    /// <remarks>
    /// A pure comparison of CONFIG against STATE, which is the one pairing the rest of the engine never
    /// makes — every other pass walks the procedure's units and therefore cannot see a unit that is not in
    /// it. Compared case-insensitively, for the reason <see cref="Procedure"/> refuses two names differing
    /// only by case: on Windows they were always the same directory.
    /// </remarks>
    private static IReadOnlyList<UnitState> Orphaned(Procedure procedure, DeploymentState? recorded)
    {
        // A NARROWED procedure is a partial view by request, so every unit it leaves out would look
        // stranded. Asking a targeted run about units it was told to skip would make the signal noise, and
        // noise is how a real orphan gets ignored. Ask the whole procedure instead.
        if (recorded is null || procedure.IsNarrowed) return [];

        var declared = procedure.Units.Select(u => u.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. recorded.Units.Where(u => !declared.Contains(u.Unit))];
    }

    private async Task<OperationOutcome> RunAsync(
        Procedure procedure, IReadOnlyList<ProcedureUnit> units, RunKind kind,
        DeploymentRequest request, Action<ProgressReport> report, string? runId, CancellationToken ct)
    {
        // A token already cancelled means the run never starts, and leaves NO trace. Without this the
        // behaviour depended on which side of the opening history write the cancellation landed: a
        // microsecond earlier and the write itself threw and recorded nothing, a microsecond later and the
        // history held a paused run that had touched nothing and would be adopted by the next apply.
        // Nothing happened, so nothing is recorded — deterministically.
        ct.ThrowIfCancellationRequested();

        // One id for the whole attempt INCLUDING its resumes — a resume continues a run rather than
        // starting one, or the history would show five failures where one interrupted job happened.
        //
        // With no id given, ADOPT a live run for this procedure + prefix if one exists. That is "resume is
        // a re-run" taken to its conclusion: the caller should not have to know whether they are starting
        // or continuing. With a shared history the live run may belong to another machine, and adopting it
        // is still right — the alternative is two competing records of one deployment, which is precisely
        // the out-of-sync state the plan exists to reveal.
        var live = await history.LiveAsync(procedure.Name, request.Prefix, ct);
        var id = runId ?? live?.Id ?? Guid.NewGuid().ToString("N");

        // Continuing a run keeps the moment it BEGAN. Stamping "now" over it would make one interrupted
        // three-hour job report as however long its last resume took, which is the same defect as giving a
        // resume a new id — the record stops describing the attempt and starts describing the retry.
        var started = live?.Id == id ? live.StartedAt : DateTimeOffset.UtcNow;
        var record = new RunRecord(id, procedure.Name, request.Prefix, kind, RunStatus.Running, started);
        await history.UpsertAsync(record, ct);

        var done = 0;
        try
        {
            foreach (var unit in units)
            {
                ct.ThrowIfCancellationRequested();

                // The driver reports progress through ITS unit; this turns that into progress through the
                // run. Done here rather than asked of every provider, because the engine is the only thing
                // that knows how many units there are or what they weigh — and a provider computing it would
                // need to be told, which is the sort of thing that gets told wrong.
                var context = new UnitContext(
                    unit, request, Rescale(report, unit, done, procedure.TotalWeight), ct);

                if (kind == RunKind.Apply) await ConvergeAsync(context);
                else await RetireAsync(context);

                // Keep state current as we go, not once at the end: a run that pauses halfway has still
                // created things, and state that only lands on success would omit exactly the resources a
                // resumed or abandoned run most needs to know about.
                await RecordStateAsync(procedure, request, unit, removed: kind == RunKind.Destroy, ct);

                done += unit.Weight;
                report(new ProgressReport(unit.Name, $"{unit.Label}: done.",
                    Percent(done, procedure.TotalWeight), ProgressStatus.Success));
            }

            await Finish(record with { Status = RunStatus.Succeeded, FinishedAt = DateTimeOffset.UtcNow });
            return OperationOutcome.Success();
        }
        catch (OperationCanceledException)
        {
            // Cancellation leaves the run LIVE on purpose. Whatever the provider had started is still
            // converging out there; marking this failed would hide work that is genuinely in flight.
            await Finish(record with { Status = RunStatus.Paused, Reason = PauseReason.External });
            throw;
        }
        catch (Exception ex)
        {
            var outcome = OperationOutcome.From(target.Classifier.Classify(ex) ?? FailureClass.Hard, ex.Message);
            report(new ProgressReport(procedure.Name, Explain(outcome), -1, ProgressStatus.Error));
            await Finish(record with
            {
                Status = outcome.Resumable ? RunStatus.Paused : RunStatus.Failed,
                FinishedAt = outcome.Resumable ? null : DateTimeOffset.UtcNow,
                Reason = outcome.Reason,
                Error = ex.Message,
            });
            return outcome;
        }

        // How a run ENDS is recorded with no cancellation token, deliberately, and this is the one place in
        // the engine where ignoring the caller's token is right.
        //
        // It used to pass `ct`. On the cancellation path that is the token that has just been cancelled, so
        // any history honouring it — including the shipped file one, whose gate takes the token — threw
        // instead of writing, and the run stayed recorded as `Running` with no reason. The operator could
        // not tell a deliberate cancel from a process that died, and `PauseReason.External` never reached a
        // record at all. On the failure path it was worse: a token cancelled around the same time as a
        // failure lost the outcome entirely and left the run open for ever.
        //
        // Being told to stop is not a reason to stop saying WHY you stopped.
        Task Finish(RunRecord ending) => history.UpsertAsync(ending, CancellationToken.None);
    }

    /// <summary>
    /// Bring ONE unit to the desired state — read the phase, decide, act. The decision is
    /// <see cref="Reconcile.Decide"/> and nothing else; this method only carries it out.
    /// </summary>
    private async Task ConvergeAsync(UnitContext context)
    {
        var phase = await WithRetryAsync(() => target.Driver.PhaseAsync(context), context.Cancellation);

        switch (Reconcile.Decide(phase))
        {
            case ReconcileAction.Attach:
                // Someone else's operation is already converging this unit. Watch it; issue nothing.
                context.Progress($"{context.Label}: already in progress — resuming.");
                await target.Driver.AwaitSettledAsync(context);
                return;

            case ReconcileAction.SettleThenRecreate:
                context.Progress($"{context.Label}: rolling back — waiting for it to settle.");
                // The wait is EXPECTED to end in failure: that is what unwinding settles into. Swallow it
                // here and let the recreate below be the real attempt.
                try { await target.Driver.AwaitSettledAsync(context); }
                catch (OperationCanceledException) { throw; }
                catch { /* settled into a failed state, which is exactly what we were waiting for */ }
                goto case ReconcileAction.Recreate;

            case ReconcileAction.Recreate:
                context.Progress($"{context.Label}: cannot be updated in place — remaking it.");
                await WithRetryAsync(() => target.Driver.RemoveAsync(context), context.Cancellation);
                await CreateAndWaitAsync(context);
                return;

            case ReconcileAction.Update:
                context.Progress($"{context.Label}: updating…");
                if (!await WithRetryAsync(() => target.Driver.UpdateAsync(context), context.Cancellation))
                {
                    // "Nothing to change" is success, and on a resume it is the ordinary answer.
                    context.Progress($"{context.Label}: already up to date.", status: ProgressStatus.Success);
                    return;
                }
                await target.Driver.AwaitSettledAsync(context);
                return;

            default:
                context.Progress($"{context.Label}: creating…");
                await CreateAndWaitAsync(context);
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
            : await WithRetryAsync(() => target.Driver.RefreshAsync(new UnitContext(unit, request, Ignore, ct)), ct);
        await state.SaveAsync(current.With(unit.Name, resources), ct);
    }

    private async Task CreateAndWaitAsync(UnitContext context)
    {
        await WithRetryAsync(() => target.Driver.CreateAsync(context), context.Cancellation);
        await target.Driver.AwaitSettledAsync(context);
    }

    /// <summary>Remove one unit, tolerating one that is already gone — teardown must be re-runnable.</summary>
    private async Task RetireAsync(UnitContext context)
    {
        var phase = await WithRetryAsync(() => target.Driver.PhaseAsync(context), context.Cancellation);

        // The same decision the teardown PLAN showed, taken again from what is true now — so what an
        // operator was shown and what happens come from one function rather than two that can disagree.
        if (Reconcile.DecideDestroy(phase) == ReconcileAction.Nothing)
        {
            context.Progress($"{context.Label}: already removed.", status: ProgressStatus.Success);
            return;
        }

        context.Progress($"{context.Label}: removing…");
        await WithRetryAsync(() => target.Driver.RemoveAsync(context), context.Cancellation);
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

    /// <summary>
    /// Wrap a progress callback so a driver's unit-relative percent arrives as a run-relative one.
    /// </summary>
    /// <remarks>
    /// -1 passes through untouched: a driver saying "I cannot tell" must not be turned into a number, which
    /// would be the one kind of progress worse than none. Anything outside 0–100 is treated the same way
    /// rather than clamped, because a driver reporting 140 has a bug and quietly making it 100 hides it.
    /// </remarks>
    private static Action<ProgressReport> Rescale(
        Action<ProgressReport> report, ProcedureUnit unit, int done, int total)
    {
        if (total <= 0) return report;

        return line =>
        {
            if (line.Percent is < 0 or > 100) { report(line); return; }

            var through = done + unit.Weight * (line.Percent / 100.0);
            report(line with { Percent = (int)Math.Round(100.0 * through / total) });
        };
    }

    /// <summary>
    /// What to tell the operator. A pause says the work is kept, because it is.
    /// </summary>
    /// <remarks>
    /// <para>Matched against the <see cref="PauseReason"/> values rather than against copies of their text.
    /// The literals were the same "two copies of a rule" defect as everywhere else: renaming a reason would
    /// have silently dropped it through to the failure wording, which is the one sentence that must not be
    /// wrong here.</para>
    /// <para><b>Any pause the table does not know still reads as a pause.</b>
    /// <see cref="PauseReason"/> is open on purpose — a provider or procedure may add one (DNS validation
    /// pending, a manual approval gate) — so the fallback has to keep the promise the class makes rather
    /// than telling an operator their intact deployment failed.</para>
    /// </remarks>
    private static string Explain(OperationOutcome outcome)
    {
        if (outcome.Reason is not { } reason)
            return "The run failed: " + (outcome.Error ?? "unknown error");

        if (reason == PauseReason.Credentials)
            return "The provider rejected your credentials — they may have expired. Re-enter them and " +
                   "resume; the progress so far is kept.";

        if (reason == PauseReason.Transient)
            return "A temporary provider error interrupted the run. You can resume — the progress so far is kept.";

        if (reason == PauseReason.External)
            return "The run is waiting on something outside the provider. You can resume once it is ready.";

        return $"The run is paused ({reason.Value}). You can resume — the progress so far is kept.";
    }
}
