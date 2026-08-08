namespace Tyanor.Testing;

/// <summary>
/// What a <see cref="UnitDriverContract"/> needs from your provider: a driver, one unit it can genuinely
/// deploy, and a way back to nothing.
/// </summary>
/// <remarks>
/// The unit should be the SMALLEST real thing the provider can make — the contract creates and destroys it
/// several times. It must be real, not a stub: a stub agrees with whatever the driver believes, which is
/// exactly what this suite exists to check.
/// </remarks>
public interface IUnitDriverFixture
{
    /// <summary>The driver under test.</summary>
    IUnitDriver Driver { get; }

    /// <summary>The unit the checks deploy.</summary>
    ProcedureUnit Unit { get; }

    /// <summary>A request that deploys it somewhere disposable.</summary>
    DeploymentRequest Request { get; }

    /// <summary>Return the target to "nothing was ever deployed". Called before every check.</summary>
    /// <param name="ct">Cancellation.</param>
    Task ResetAsync(CancellationToken ct);
}

/// <summary>
/// What every <see cref="IUnitDriver"/> must do for the engine to behave as documented.
///
/// <para><b>This is the suite to run before trusting a provider you wrote.</b> The engine assumes a
/// handful of things that are never stated in a signature: that a phase read changes nothing, that removing
/// something already gone is fine, that an update with nothing to change says so, and that a resource keeps
/// its identity across a refresh. Every one of those is easy to get almost right, and every one fails
/// quietly — as a duplicate deployment, a teardown that will not re-run, a resume that redoes finished work,
/// or a plan reporting drift that is not there.</para>
///
/// <para>It deploys real things. Point the fixture at a scratch location.</para>
/// </summary>
/// <param name="fixture">Your provider, and something it can deploy.</param>
public sealed class UnitDriverContract(IUnitDriverFixture fixture) : ContractSuite
{
    /// <inheritdoc/>
    public override string Subject => "IUnitDriver";

    private IUnitDriver Driver => fixture.Driver;
    private ProcedureUnit Unit => fixture.Unit;
    private DeploymentRequest Request => fixture.Request;

    private static void Ignore(ProgressReport _) { }

    /// <summary>Create it and wait, the way the engine does.</summary>
    private async Task DeployAsync(CancellationToken ct)
    {
        await Driver.CreateAsync(Unit, Request, ct);
        await Driver.AwaitSettledAsync(Unit, Request, Ignore, ct);
    }

    /// <inheritdoc/>
    protected override IReadOnlyList<(string Name, Func<CancellationToken, Task<string?>> Run)> Cases =>
    [
        ("Nothing deployed reads as Missing", async ct =>
        {
            await fixture.ResetAsync(ct);
            var phase = await Driver.PhaseAsync(Unit, Request, ct);
            return phase == UnitPhase.Missing ? null : $"got {phase}; the engine will not create it";
        }),

        ("Nothing deployed owns no resources", async ct =>
        {
            // Absent is a fact, not a failure. Throwing here makes a plan of an undeployed procedure
            // impossible, which is the plan people most want.
            await fixture.ResetAsync(ct);
            var resources = await Driver.RefreshAsync(Unit, Request, ct);
            return resources.Count == 0 ? null : $"reported {resources.Count} resources before anything existed";
        }),

        ("Removing what is not there does not throw", async ct =>
        {
            // Teardown must be re-runnable: an interrupted one is resumed by running it again, and it will
            // meet units it already removed.
            await fixture.ResetAsync(ct);
            await Driver.RemoveAsync(Unit, Request, ct);
            return null;
        }),

        ("After creating, it is no longer Missing", async ct =>
        {
            await fixture.ResetAsync(ct);
            await DeployAsync(ct);
            var phase = await Driver.PhaseAsync(Unit, Request, ct);
            return phase != UnitPhase.Missing ? null : "still Missing after create; the engine will create it again";
        }),

        ("After creating, it owns something", async ct =>
        {
            // What Tyanor OWNS is the whole reason state exists: without it a teardown cannot tell what it
            // created from what was already there.
            await fixture.ResetAsync(ct);
            await DeployAsync(ct);
            var resources = await Driver.RefreshAsync(Unit, Request, ct);
            return resources.Count > 0 ? null : "refresh reported nothing after a successful create";
        }),

        ("Resource identity survives a refresh", async ct =>
        {
            // Ids are what a diff compares on. An id that changes between reads — a pid, a timestamp, a
            // generated name — makes every plan report the whole unit destroyed and recreated.
            await fixture.ResetAsync(ct);
            await DeployAsync(ct);

            var first = (await Driver.RefreshAsync(Unit, Request, ct)).Select(r => r.Id).Order().ToList();
            var second = (await Driver.RefreshAsync(Unit, Request, ct)).Select(r => r.Id).Order().ToList();
            return first.SequenceEqual(second, StringComparer.Ordinal)
                ? null
                : $"ids changed between reads: [{string.Join(", ", first)}] then [{string.Join(", ", second)}]";
        }),

        ("A resource id is never empty", async ct =>
        {
            await fixture.ResetAsync(ct);
            await DeployAsync(ct);
            var blank = (await Driver.RefreshAsync(Unit, Request, ct)).Count(r => string.IsNullOrWhiteSpace(r.Id));
            return blank == 0 ? null : $"{blank} resources came back with no id";
        }),

        ("Reading the phase changes nothing", async ct =>
        {
            // PhaseAsync runs during a PLAN, which must be read-only. A driver that repairs something while
            // reporting on it makes the plan a lie and the apply a surprise.
            await fixture.ResetAsync(ct);
            await DeployAsync(ct);

            var first = await Driver.PhaseAsync(Unit, Request, ct);
            var second = await Driver.PhaseAsync(Unit, Request, ct);
            return first == second ? null : $"phase moved from {first} to {second} with nothing in between";
        }),

        ("Updating an unchanged deployment reports no change", async ct =>
        {
            // The property resume rests on. A driver that always returns true redoes finished work on every
            // resume, and makes a plan claim a redeploy will change things when it will not.
            await fixture.ResetAsync(ct);
            await DeployAsync(ct);

            var changed = await Driver.UpdateAsync(Unit, Request, ct);
            return changed ? "update reported a change immediately after a create" : null;
        }),

        ("After removing, it is Missing again", async ct =>
        {
            await fixture.ResetAsync(ct);
            await DeployAsync(ct);
            await Driver.RemoveAsync(Unit, Request, ct);

            var phase = await Driver.PhaseAsync(Unit, Request, ct);
            return phase == UnitPhase.Missing ? null : $"got {phase} after remove; the unit was not fully removed";
        }),

        ("After removing, it owns nothing", async ct =>
        {
            await fixture.ResetAsync(ct);
            await DeployAsync(ct);
            await Driver.RemoveAsync(Unit, Request, ct);

            var resources = await Driver.RefreshAsync(Unit, Request, ct);
            return resources.Count == 0 ? null : $"{resources.Count} resources survived the remove";
        }),

        ("Removing twice does not throw", async ct =>
        {
            await fixture.ResetAsync(ct);
            await DeployAsync(ct);
            await Driver.RemoveAsync(Unit, Request, ct);
            await Driver.RemoveAsync(Unit, Request, ct);
            return null;
        }),

        ("Creating again after a remove works", async ct =>
        {
            // Recreate is a real reconcile action — it is what a Broken unit gets — so a driver that can
            // only create once fails the first time something goes wrong rather than the first time it runs.
            await fixture.ResetAsync(ct);
            await DeployAsync(ct);
            await Driver.RemoveAsync(Unit, Request, ct);
            await DeployAsync(ct);

            var phase = await Driver.PhaseAsync(Unit, Request, ct);
            return phase != UnitPhase.Missing ? null : "the second create left it Missing";
        }),
    ];
}

/// <summary>
/// What a <see cref="FailureClassifierContract"/> needs: the classifier, and REAL errors of each class.
/// </summary>
/// <remarks>
/// The samples must be exceptions the provider genuinely produces, with the codes it genuinely sets. A
/// hand-rolled exception with the right shape proves the test, not the classifier — which is the whole
/// failure mode this contract exists to prevent.
/// </remarks>
public interface IFailureClassifierFixture
{
    /// <summary>The classifier under test.</summary>
    IFailureClassifier Classifier { get; }

    /// <summary>Errors meaning the provider rejected who we are. At least one.</summary>
    IReadOnlyList<Exception> CredentialErrors { get; }

    /// <summary>Errors meaning the provider was busy or briefly broken. At least one.</summary>
    IReadOnlyList<Exception> TransientErrors { get; }

    /// <summary>Errors meaning the request itself is wrong. May classify as Hard or not be recognised.</summary>
    IReadOnlyList<Exception> HardErrors { get; }
}

/// <summary>
/// What every <see cref="IFailureClassifier"/> must do.
///
/// <para><b>The classifier is the part of a provider most worth testing without the provider.</b> Both ways
/// of being wrong are quiet and expensive: a credential error read as hard throws away a deployment that was
/// entirely intact, and a hard error read as transient tells the same lie five times before reporting the
/// wrong thing anyway.</para>
/// </summary>
/// <param name="fixture">Your classifier and its real errors.</param>
public sealed class FailureClassifierContract(IFailureClassifierFixture fixture) : ContractSuite
{
    /// <inheritdoc/>
    public override string Subject => "IFailureClassifier";

    /// <summary>An error type no provider has ever seen, for the "not mine" check.</summary>
    private sealed class UnheardOfException() : Exception("nothing has ever thrown this");

    /// <inheritdoc/>
    protected override IReadOnlyList<(string Name, Func<CancellationToken, Task<string?>> Run)> Cases =>
    [
        ("Samples were supplied for the classes that matter", _ =>
        {
            // Checked first, because an empty list makes the two checks below pass without testing anything —
            // and a contract that can be satisfied by supplying nothing is worse than no contract.
            if (fixture.CredentialErrors.Count == 0) return Fail("no credential errors supplied");
            if (fixture.TransientErrors.Count == 0) return Fail("no transient errors supplied");
            return Ok;
        }),

        ("Every credential error classifies as Credentials", _ =>
        {
            foreach (var error in fixture.CredentialErrors)
            {
                var got = fixture.Classifier.Classify(error);
                if (got != FailureClass.Credentials)
                    return Fail($"{Describe(error)} classified as {got?.ToString() ?? "null (Hard)"}; " +
                                "the run will FAIL where it should pause, discarding work that is intact");
            }
            return Ok;
        }),

        ("Every transient error classifies as Transient", _ =>
        {
            foreach (var error in fixture.TransientErrors)
            {
                var got = fixture.Classifier.Classify(error);
                if (got != FailureClass.Transient)
                    return Fail($"{Describe(error)} classified as {got?.ToString() ?? "null (Hard)"}; " +
                                "it will not be retried");
            }
            return Ok;
        }),

        ("Every hard error ends the run", _ =>
        {
            // Hard and null are the same outcome — the engine's default for null is Hard — so both pass.
            // What must not happen is a hard error being retried or, worse, treated as a pause.
            foreach (var error in fixture.HardErrors)
            {
                var got = fixture.Classifier.Classify(error);
                if (got is FailureClass.Transient or FailureClass.Credentials)
                    return Fail($"{Describe(error)} classified as {got}; the run will offer a resume that " +
                                "cannot work");
            }
            return Ok;
        }),

        ("An error it has never seen is not guessed at", _ =>
        {
            // "Not mine" and "harmless" are different answers. Null is right, and the engine turns it into
            // Hard — the safe default, because the error nobody anticipated is the one not to retry silently.
            var got = fixture.Classifier.Classify(new UnheardOfException());
            return got is null or FailureClass.Hard
                ? Ok
                : Fail($"an unknown exception classified as {got}");
        }),

        ("A wrapped credential error still classifies", _ =>
        {
            // THE most common way a classifier goes quietly wrong. SDKs wrap the informative exception inside
            // a generic one, and a classifier reading only the outermost calls an expired token a hard
            // failure — throwing away a deployment that only needed re-authenticating.
            foreach (var error in fixture.CredentialErrors)
            {
                var wrapped = new InvalidOperationException("deploying a unit", error);
                if (fixture.Classifier.Classify(wrapped) != FailureClass.Credentials)
                    return Fail($"{Describe(error)} was not found inside an InvalidOperationException — " +
                                "walk the whole InnerException chain");
            }
            return Ok;
        }),

        ("A doubly wrapped transient error still classifies", _ =>
        {
            // A task-based call path adds an AggregateException on top of whatever the SDK already wrapped.
            foreach (var error in fixture.TransientErrors)
            {
                var wrapped = new InvalidOperationException("deploying a unit", new AggregateException(error));
                if (fixture.Classifier.Classify(wrapped) != FailureClass.Transient)
                    return Fail($"{Describe(error)} was not found two levels down");
            }
            return Ok;
        }),

        ("Classifying never throws", _ =>
        {
            // It runs while another failure is being handled. Throwing here replaces the real error with this
            // one, and the operator never learns what actually went wrong.
            var everything = fixture.CredentialErrors
                .Concat(fixture.TransientErrors)
                .Concat(fixture.HardErrors)
                .Append(new UnheardOfException())
                .Append(new Exception("bare"));

            foreach (var error in everything)
            {
                try { fixture.Classifier.Classify(error); }
                catch (Exception e) { return Fail($"threw {e.GetType().Name} classifying {Describe(error)}"); }
            }
            return Ok;
        }),
    ];

    private static Task<string?> Ok => Task.FromResult<string?>(null);

    private static Task<string?> Fail(string why) => Task.FromResult<string?>(why);

    private static string Describe(Exception error) =>
        $"{error.GetType().Name}(\"{Trim(error.Message)}\")";

    private static string Trim(string message) =>
        message.Length <= 60 ? message : message[..57] + "…";
}
