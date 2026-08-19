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

    /// <summary>
    /// Output names this unit produces once deployed — a URL, an endpoint, a generated name. Empty when it
    /// produces none, which is the default and a legitimate answer.
    /// </summary>
    /// <remarks>
    /// <para><b>Without this the outputs checks cannot fail.</b> "An undeployed unit produces nothing" is
    /// satisfied trivially by a driver that produces nothing ever, so a suite that only knows how to look
    /// for emptiness is checking that emptiness is empty. Naming the keys is what lets it verify the
    /// interesting direction too: they appear after a deploy, and they are gone after a remove.</para>
    /// <para>A default member, so adding it broke no implementation — the pattern D18 established for
    /// growing a contract: a new capability arrives meaning <i>I do not do that</i>.</para>
    /// </remarks>
    IReadOnlyCollection<string> ExpectedOutputs => [];
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

    /// <summary>
    /// A context the way the engine builds one — except that progress goes nowhere, because what a driver
    /// SAYS is not what this contract is about.
    /// </summary>
    private UnitContext Context(CancellationToken ct) =>
        new(fixture.Unit, fixture.Request, _ => { }, ct);

    /// <summary>Create it and wait, the way the engine does.</summary>
    private async Task DeployAsync(CancellationToken ct)
    {
        await Driver.CreateAsync(Context(ct));
        await Driver.AwaitSettledAsync(Context(ct));
    }

    /// <inheritdoc/>
    protected override IReadOnlyList<(string Name, Func<CancellationToken, Task<string?>> Run)> Cases =>
    [
        ("Nothing deployed reads as Missing", async ct =>
        {
            await fixture.ResetAsync(ct);
            var phase = await Driver.PhaseAsync(Context(ct));
            return phase == UnitPhase.Missing ? null : $"got {phase}; the engine will not create it";
        }),

        ("Nothing deployed owns no resources", async ct =>
        {
            // Absent is a fact, not a failure. Throwing here makes a plan of an undeployed procedure
            // impossible, which is the plan people most want.
            await fixture.ResetAsync(ct);
            var resources = await Driver.RefreshAsync(Context(ct));
            return resources.Count == 0 ? null : $"reported {resources.Count} resources before anything existed";
        }),

        ("Removing what is not there does not throw", async ct =>
        {
            // Teardown must be re-runnable: an interrupted one is resumed by running it again, and it will
            // meet units it already removed.
            await fixture.ResetAsync(ct);
            await Driver.RemoveAsync(Context(ct));
            return null;
        }),

        ("After creating, it is no longer Missing", async ct =>
        {
            await fixture.ResetAsync(ct);
            await DeployAsync(ct);
            var phase = await Driver.PhaseAsync(Context(ct));
            return phase != UnitPhase.Missing ? null : "still Missing after create; the engine will create it again";
        }),

        ("After creating, it owns something", async ct =>
        {
            // What Tyanor OWNS is the whole reason state exists: without it a teardown cannot tell what it
            // created from what was already there.
            await fixture.ResetAsync(ct);
            await DeployAsync(ct);
            var resources = await Driver.RefreshAsync(Context(ct));
            return resources.Count > 0 ? null : "refresh reported nothing after a successful create";
        }),

        ("Resource identity survives a refresh", async ct =>
        {
            // Ids are what a diff compares on. An id that changes between reads — a pid, a timestamp, a
            // generated name — makes every plan report the whole unit destroyed and recreated.
            await fixture.ResetAsync(ct);
            await DeployAsync(ct);

            var first = (await Driver.RefreshAsync(Context(ct))).Select(r => r.Id).Order().ToList();
            var second = (await Driver.RefreshAsync(Context(ct))).Select(r => r.Id).Order().ToList();
            return first.SequenceEqual(second, StringComparer.Ordinal)
                ? null
                : $"ids changed between reads: [{string.Join(", ", first)}] then [{string.Join(", ", second)}]";
        }),

        ("A resource id is never empty", async ct =>
        {
            await fixture.ResetAsync(ct);
            await DeployAsync(ct);
            var blank = (await Driver.RefreshAsync(Context(ct))).Count(r => string.IsNullOrWhiteSpace(r.Id));
            return blank == 0 ? null : $"{blank} resources came back with no id";
        }),

        ("Resource ids are unique within a unit", async ct =>
        {
            // An id is what a diff matches on, so two resources sharing one cannot both be tracked: the
            // second hides the first, and whatever the first was doing becomes invisible to every plan and
            // to the teardown. `StateDiff` already assumes this; the assumption belongs where an implementer
            // can find out they broke it.
            await fixture.ResetAsync(ct);
            await DeployAsync(ct);

            var ids = (await Driver.RefreshAsync(Context(ct))).Select(r => r.Id).ToList();
            var repeated = ids.GroupBy(id => id, StringComparer.Ordinal)
                .Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            return repeated.Count == 0
                ? null
                : $"refresh reported these ids more than once: {string.Join(", ", repeated)}";
        }),

        ("Reading the phase changes nothing", async ct =>
        {
            // PhaseAsync runs during a PLAN, which must be read-only. A driver that repairs something while
            // reporting on it makes the plan a lie and the apply a surprise.
            await fixture.ResetAsync(ct);
            await DeployAsync(ct);

            var first = await Driver.PhaseAsync(Context(ct));
            var second = await Driver.PhaseAsync(Context(ct));
            return first == second ? null : $"phase moved from {first} to {second} with nothing in between";
        }),

        ("Reading the phase of NOTHING does not bring it into being", async ct =>
        {
            // The check above only looks after a deploy, so a driver whose PhaseAsync creates the thing it is
            // asked about would pass it. That is the moment read-only matters most: planning a deployment
            // that does not exist yet is the plan people run first, and it must create nothing.
            await fixture.ResetAsync(ct);

            await Driver.PhaseAsync(Context(ct));
            await Driver.PhaseAsync(Context(ct));

            var resources = await Driver.RefreshAsync(Context(ct));
            if (resources.Count > 0) return $"asking for the phase created {resources.Count} resources";

            var phase = await Driver.PhaseAsync(Context(ct));
            return phase == UnitPhase.Missing ? null : $"after three phase reads and nothing else it is {phase}";
        }),

        ("Nothing deployed produces no outputs, and does not throw", async ct =>
        {
            // Asking a procedure that is not deployed yet what it produced is a reasonable question with the
            // answer "nothing". The guide tells consumers a UI rendering "your site is at …" need not guard
            // the call, and that promise was made in the interface docs and checked nowhere.
            await fixture.ResetAsync(ct);

            var outputs = await Driver.OutputsAsync(Context(ct));
            return outputs.Count == 0
                ? null
                : $"an undeployed unit produced {outputs.Count} outputs: {string.Join(", ", outputs.Keys)}";
        }),

        ("A deployed unit produces the outputs it says it does", async ct =>
        {
            // Vacuous for a driver that produces none — which is the honest degenerate case, not a hole,
            // because such a driver has nothing to get wrong here.
            if (fixture.ExpectedOutputs.Count == 0) return null;

            await fixture.ResetAsync(ct);
            await DeployAsync(ct);

            var outputs = await Driver.OutputsAsync(Context(ct));
            var missing = fixture.ExpectedOutputs.Where(k => !outputs.ContainsKey(k)).ToList();
            return missing.Count == 0
                ? null
                : $"deployed, but produced no {string.Join(", ", missing)} — it has: " +
                  $"{string.Join(", ", outputs.Keys.DefaultIfEmpty("nothing"))}";
        }),

        ("Outputs do not survive a remove", async ct =>
        {
            // The direction that fails quietly: outputs read from a stored copy rather than from the
            // provider keep answering after the thing is gone, and a UI goes on showing an address that
            // stopped resolving. `IUnitDriver.OutputsAsync` says read from the target for exactly this.
            if (fixture.ExpectedOutputs.Count == 0) return null;

            await fixture.ResetAsync(ct);
            await DeployAsync(ct);
            await Driver.RemoveAsync(Context(ct));

            var outputs = await Driver.OutputsAsync(Context(ct));
            var survivors = fixture.ExpectedOutputs.Where(outputs.ContainsKey).ToList();
            return survivors.Count == 0
                ? null
                : $"{string.Join(", ", survivors)} still answered after the unit was removed";
        }),

        ("Validating REPORTS its problems rather than throwing", async ct =>
        {
            // A validation pass exists to return the whole list. A driver that throws stops at the first
            // unconfigured unit, which is exactly the behaviour offline validation replaces — and it does it
            // to the WHOLE procedure, not just to itself.
            await fixture.ResetAsync(ct);
            await Driver.ValidateAsync(Context(ct));
            return null;
        }),

        ("Updating an unchanged deployment reports no change", async ct =>
        {
            // The property resume rests on. A driver that always returns true redoes finished work on every
            // resume, and makes a plan claim a redeploy will change things when it will not.
            await fixture.ResetAsync(ct);
            await DeployAsync(ct);

            var changed = await Driver.UpdateAsync(Context(ct));
            return changed ? "update reported a change immediately after a create" : null;
        }),

        ("After removing, it is Missing again", async ct =>
        {
            await fixture.ResetAsync(ct);
            await DeployAsync(ct);
            await Driver.RemoveAsync(Context(ct));

            var phase = await Driver.PhaseAsync(Context(ct));
            return phase == UnitPhase.Missing ? null : $"got {phase} after remove; the unit was not fully removed";
        }),

        ("After removing, it owns nothing", async ct =>
        {
            await fixture.ResetAsync(ct);
            await DeployAsync(ct);
            await Driver.RemoveAsync(Context(ct));

            var resources = await Driver.RefreshAsync(Context(ct));
            return resources.Count == 0 ? null : $"{resources.Count} resources survived the remove";
        }),

        ("Removing twice does not throw", async ct =>
        {
            await fixture.ResetAsync(ct);
            await DeployAsync(ct);
            await Driver.RemoveAsync(Context(ct));
            await Driver.RemoveAsync(Context(ct));
            return null;
        }),

        ("Creating again after a remove works", async ct =>
        {
            // Recreate is a real reconcile action — it is what a Broken unit gets — so a driver that can
            // only create once fails the first time something goes wrong rather than the first time it runs.
            await fixture.ResetAsync(ct);
            await DeployAsync(ct);
            await Driver.RemoveAsync(Context(ct));
            await DeployAsync(ct);

            var phase = await Driver.PhaseAsync(Context(ct));
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

        ("A credential error beside a SIBLING in an aggregate still classifies", _ =>
        {
            // The check above passes with an aggregate holding ONE inner, because AggregateException.
            // InnerException returns that one — so a classifier walking only `e.InnerException` looks correct
            // right up until something fails alongside something else. Anything awaiting several operations
            // together produces exactly that, and the credential error is then not first.
            //
            // The cost of missing it is the worst one this class of code has: unrecognised means Hard, so the
            // run ENDS instead of pausing, and every unit already deployed is discarded — for want of looking
            // one element to the right. FailureClassifiers.Walk does it for you.
            foreach (var error in fixture.CredentialErrors)
            {
                var beside = new AggregateException("several operations failed", new UnheardOfException(), error);
                if (fixture.Classifier.Classify(beside) != FailureClass.Credentials)
                    return Fail($"{Describe(error)} was not found beside another exception in an " +
                                "AggregateException — walk every InnerExceptions entry, not just the first");
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
