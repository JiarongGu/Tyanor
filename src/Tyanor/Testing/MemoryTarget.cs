namespace Tyanor.Testing;

/// <summary>
/// A deployment target that deploys to a dictionary — for testing YOUR code, not Tyanor's.
///
/// <para><b>This is a provider, not a mock.</b> The distinction matters and it is the reason this ships at
/// all. <c>LocalTarget</c> deploys to a machine; this deploys to memory. It is not pretending to be AWS and
/// it never simulates another provider's semantics, so it cannot teach you a wrong belief about one. It
/// passes <see cref="UnitDriverContract"/> by genuinely behaving — the same entry ticket every other
/// implementation has to buy (<c>docs/DECISIONS.md</c> D23 draws that line).</para>
///
/// <para><b>What it is for.</b> An application that deploys through Tyanor has its own logic to test: does
/// the UI offer a Resume button when a run pauses, does the pipeline stop when validation fails, does the
/// operator see the right thing when someone else's deployment is already in flight. Reaching those states
/// against a real target means credentials, money and minutes. Reaching them here is one line.</para>
///
/// <example>
/// The ordinary case — a target that just works, so a test can be about something else:
/// <code>
/// var runner = new ProcedureRunner(new MemoryTarget(), new InMemoryRunHistory());
/// Assert.True((await runner.ApplyAsync(procedure, request)).Ok);
/// </code>
/// And the case it exists for — driving your code down a path that is expensive to reach for real:
/// <code>
/// var target = new MemoryTarget().Fails("api", FailureClass.Credentials, "the token expired");
///
/// var outcome = await runner.ApplyAsync(procedure, request);
///
/// Assert.True(outcome.Resumable);          // …now assert YOUR application offers the resume
/// </code>
/// </example>
///
/// <para><b>It hosts your own units too.</b> A step written against <c>IUnitDriver</c> — verify a migration
/// applied, warm a cache — can be registered here exactly as it would be in a real provider
/// (<c>docs/DECISIONS.md</c> D19), so it can be developed and driven through a whole procedure before it
/// ever meets a cloud. That is the development loop D19 describes, and without this the only harness for it
/// was a real target.</para>
///
/// <example>
/// <code>
/// var target = new MemoryTarget(new CustomUnits
/// {
///     Classifier = new MyClassifier(),
///     ["migration"] = new VerifyMigrationUnit(http),
/// });
///
/// // ["migration.kind"] = "migration" in the request; every other unit needs no kind at all.
/// </code>
/// </example>
///
/// <para><b>Unlike a real provider it has no REQUIRED kind, and that is the only difference.</b>
/// <c>UnitKindDriver</c> refuses a unit that declares none, because guessing would deploy something the
/// operator never described. Here the guess is a dictionary, which is harmless — so a unit that declares no
/// kind gets the memory behaviour, and that is what keeps the ordinary case a single line.</para>
///
/// <para>A unit that declares a kind this target does NOT have is refused, exactly as a real provider refuses
/// it, and for exactly the reason above: the operator named a kind. Falling back to memory there was a real
/// defect — an adopter who forgot to register their units on a new platform got a green test suite here and an
/// exception in production, which inverts what this type is for. Its own test could not fail, which is why it
/// survived.</para>
///
/// <para><b>What it does not do:</b> it is not thread-safe across concurrent runs, because a test that needs
/// that is testing the engine rather than using it.</para>
/// </summary>
public sealed class MemoryTarget : IDeploymentTarget, IUnitDriver, IFailureClassifier
{
    /// <summary>The option a unit sets to say what it is — the same convention every provider uses.</summary>
    public const string KindOption = "kind";

    private readonly Dictionary<string, int> _deployed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IUnitDriver> _custom;

    /// <summary>A target that deploys to a dictionary, optionally hosting units of your own.</summary>
    /// <param name="custom">
    /// Your own unit kinds, registered as they would be in a real provider. Their errors are classified by
    /// <see cref="CustomUnits.Classifier"/> chained AFTER this target's own, so a step of yours can pause
    /// rather than only fail.
    /// </param>
    public MemoryTarget(CustomUnits? custom = null)
    {
        // COPIED, not held. UnitKindDriver.Register(CustomUnits) copies too, so a real provider never sees a
        // kind added after it was built. A test target that DID see one would let a test pass here and then
        // behave differently against AwsTarget — the single thing this type must never do (D24).
        _custom = custom is null
            ? new Dictionary<string, IUnitDriver>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, IUnitDriver>(custom, StringComparer.OrdinalIgnoreCase);

        Classifier = FailureClassifiers.Chain(this, custom?.Classifier);
    }

    /// <summary>The driver for one unit: yours when it declares a kind you registered, otherwise memory.</summary>
    /// <exception cref="UnitKindException">It declares a kind that is not registered here.</exception>
    private IUnitDriver? Own(UnitContext context)
    {
        if (context.Option(KindOption) is not { } kind) return null;     // no kind at all: memory, by design
        if (_custom.TryGetValue(kind, out var driver)) return driver;

        // A kind was DECLARED and is not registered. Falling back to memory behaviour here is the mistake this
        // whole type exists to not make: the operator said "this is a discovery unit" and we would deploy a
        // dictionary entry instead — quietly, and only on the target they test against. A real provider refuses
        // and names what it has, so this refuses and names what it has.
        throw new UnitKindException(
            $"Unit '{context.Name}' declares {KindOption} '{kind}', which is not registered on this " +
            $"MemoryTarget. It offers: {Available()}. A unit that declares NO kind gets the memory behaviour; " +
            "one that names a kind is asking for that kind specifically.");
    }

    private string Available() =>
        _custom.Count == 0
            ? "none — no CustomUnits were handed to this MemoryTarget"
            : string.Join(", ", _custom.Keys.Order());

    /// <summary>The id this target answers to. Change it to test wiring that selects by id.</summary>
    public string Id { get; init; } = "memory";

    /// <summary>
    /// What the next apply considers a NEW build. Bump it and every unit's update reports a change; leave
    /// it and an update over an unchanged deployment reports no change, which is what a resume relies on.
    /// </summary>
    public int Revision { get; set; }

    /// <inheritdoc/>
    public IUnitDriver Driver => this;

    /// <summary>This target's own reading of an error, with <see cref="CustomUnits.Classifier"/> after it.</summary>
    public IFailureClassifier Classifier { get; }

    /// <summary>
    /// Who this target says we are. Set it to test what your application shows when credentials are refused.
    /// </summary>
    public TargetIdentity Identity { get; set; } = new(true, "memory-account", "memory-principal");

    // ── what a test can script ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Phases to report INSTEAD of the truth, per unit — the one deliberate departure from honesty here,
    /// and the reason for it is that <see cref="UnitPhase.Converging"/> and <see cref="UnitPhase.Broken"/>
    /// are states a real target reaches by timing or by failing, neither of which a test can arrange.
    /// A unit with no entry reports what is actually deployed.
    /// </summary>
    public Dictionary<string, UnitPhase> Phases { get; } = new(StringComparer.Ordinal);

    /// <summary>Faults to raise, per unit. See <see cref="Fails"/>.</summary>
    public Dictionary<string, MemoryFault> Faults { get; } = new(StringComparer.Ordinal);

    /// <summary>Resources a unit reports owning INSTEAD of its one default. For testing plan counts.</summary>
    public Dictionary<string, IReadOnlyList<ResourceState>> Resources { get; } = new(StringComparer.Ordinal);

    /// <summary>Validation problems a unit reports, so an application's validation screen can be tested.</summary>
    public Dictionary<string, IReadOnlyList<string>> Problems { get; } = new(StringComparer.Ordinal);

    /// <summary>Outputs a DEPLOYED unit produces — a URL, an endpoint. Empty until the unit exists.</summary>
    public Dictionary<string, Dictionary<string, string>> Outputs { get; } = new(StringComparer.Ordinal);

    /// <summary>The unit-relative percent a unit reports while settling, for testing a progress bar.</summary>
    public Dictionary<string, int> Progress { get; } = new(StringComparer.Ordinal);

    // ── what a test can observe ──────────────────────────────────────────────────────────────────

    /// <summary>Every mutating call, in order, as <c>"{unit}:{verb}"</c>. What the engine actually decided.</summary>
    public List<string> Calls { get; } = [];

    /// <summary>How many times each unit's phase was read — how a bounded retry is counted.</summary>
    public Dictionary<string, int> Attempts { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// How many times each unit was asked what it owns. Separate from <see cref="Calls"/>, which records
    /// only the mutating verbs, so adding this did not change what any existing sequence assertion sees.
    /// </summary>
    /// <remarks>
    /// Worth being able to count: a plan and an apply both refresh, and a TEARDOWN deliberately does not —
    /// it knows a removed unit owns nothing rather than asking. That saves a provider call per unit and is
    /// otherwise invisible, which is how it would quietly stop being true.
    /// </remarks>
    public Dictionary<string, int> Refreshes { get; } = new(StringComparer.Ordinal);

    /// <summary>The units currently deployed, in the order they were created.</summary>
    public IReadOnlyCollection<string> Deployed => _deployed.Keys;

    // ── scripting helpers ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Make a unit fail, every time, with a classified error.
    /// </summary>
    /// <param name="unit">Which unit.</param>
    /// <param name="failure">Which class — and therefore whether the run pauses or fails. This is the knob
    /// that matters: <see cref="FailureClass.Credentials"/> and <see cref="FailureClass.Transient"/> pause,
    /// and only <see cref="FailureClass.Hard"/> is terminal.</param>
    /// <param name="message">What the operator is told.</param>
    public MemoryTarget Fails(string unit, FailureClass failure, string message = "the memory target was told to fail")
    {
        Faults[unit] = new MemoryFault(new MemoryFaultException(failure, message));
        return this;
    }

    /// <summary>
    /// Make a unit fail ONCE and then succeed — a transient blip, for testing that a bounded retry rides
    /// it out rather than surfacing it.
    /// </summary>
    /// <param name="unit">Which unit.</param>
    /// <param name="failure">Which class.</param>
    /// <param name="message">What the operator would be told if it ran out of retries.</param>
    public MemoryTarget FailsOnce(string unit, FailureClass failure = FailureClass.Transient,
        string message = "the memory target was told to fail once")
    {
        Faults[unit] = new MemoryFault(new MemoryFaultException(failure, message), Once: true);
        return this;
    }

    /// <summary>
    /// Make a unit throw an error of YOUR choosing — one this target's classifier will not recognise.
    /// </summary>
    /// <param name="unit">Which unit.</param>
    /// <param name="error">The exception to raise.</param>
    /// <param name="once">Raise it once and then succeed.</param>
    /// <remarks>
    /// A different path from <see cref="Fails"/>, and the difference is worth having: a classified fault
    /// tests what the engine does with a KNOWN class, and this tests what it does with an error nobody
    /// claims. The engine's default for unrecognised is <see cref="FailureClass.Hard"/> — safe, and
    /// therefore the path a custom unit throwing something unexpected takes.
    /// </remarks>
    public MemoryTarget Throws(string unit, Exception error, bool once = false)
    {
        Faults[unit] = new MemoryFault(error, once);
        return this;
    }

    /// <summary>Report a unit as being in a phase, whatever is actually deployed. See <see cref="Phases"/>.</summary>
    /// <param name="unit">Which unit.</param>
    /// <param name="phase">What to report.</param>
    public MemoryTarget Reports(string unit, UnitPhase phase)
    {
        Phases[unit] = phase;
        return this;
    }

    /// <summary>Put a unit into the store without running a procedure — a deployment that already existed.</summary>
    /// <param name="units">The unit names.</param>
    public MemoryTarget AlreadyDeployed(params string[] units)
    {
        foreach (var unit in units) _deployed[unit] = Revision;
        return this;
    }

    /// <summary>
    /// Change what is deployed WITHOUT Tyanor doing it — somebody edited it in the console.
    /// </summary>
    /// <param name="units">The unit names. Ones that are not deployed are ignored: nothing can drift.</param>
    /// <remarks>
    /// <para>This is a different thing from bumping <see cref="Revision"/>, and the distinction is the one
    /// people get wrong. Revision is what you WANT — a new build waiting to go out, which an apply will
    /// deploy. This is what IS — the deployment moving underneath you, which a plan reports as drift and an
    /// apply repairs.</para>
    /// <para>A real target cannot be asked to do this, which is exactly why reaching the state is worth one
    /// line here: an application with a "your deployment has drifted" view has no other cheap way to see it.</para>
    /// </remarks>
    public MemoryTarget Drifted(params string[] units)
    {
        foreach (var unit in units)
            if (_deployed.TryGetValue(unit, out var at)) _deployed[unit] = at - 1;

        return this;
    }

    // ── IDeploymentTarget ────────────────────────────────────────────────────────────────────────

    /// <summary>Report <see cref="Identity"/>. Credentials are ignored: this target has none.</summary>
    /// <param name="credentials">Ignored.</param>
    /// <param name="ct">Cancellation.</param>
    public Task<TargetIdentity> ValidateAsync(TargetCredentials? credentials, CancellationToken ct) =>
        Task.FromResult(Identity);

    // ── IFailureClassifier ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Recognise only this target's own faults, found anywhere in the chain — the same shape every real
    /// classifier has, so an engine test exercises the real code path rather than a shortcut.
    /// </summary>
    /// <param name="error">The error.</param>
    /// <remarks>
    /// Through <see cref="FailureClassifiers.Walk"/> like the shipped providers, rather than a loop of its
    /// own. This was the THIRD hand-written copy of that walk, which is what settled the question of whether
    /// it belonged in the framework — and an unrecognised error still returns null, which the engine treats
    /// as <see cref="FailureClass.Hard"/>.
    /// </remarks>
    public FailureClass? Classify(Exception error) =>
        FailureClassifiers.Walk(error, e => e is MemoryFaultException fault ? fault.Failure : null);

    // ── IUnitDriver ──────────────────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public Task<UnitPhase> PhaseAsync(UnitContext context)
    {
        if (Own(context) is { } own) return own.PhaseAsync(context);

        Attempts[context.Name] = Attempts.GetValueOrDefault(context.Name) + 1;
        Raise(context);

        // A scripted phase wins; otherwise report what is honestly there.
        return Task.FromResult(Phases.TryGetValue(context.Name, out var scripted)
            ? scripted
            : _deployed.ContainsKey(context.Name) ? UnitPhase.Ready : UnitPhase.Missing);
    }

    /// <inheritdoc/>
    public Task CreateAsync(UnitContext context)
    {
        if (Own(context) is { } own) return own.CreateAsync(context);

        Raise(context);
        Calls.Add($"{context.Name}:create");
        _deployed[context.Name] = Revision;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Re-deploy when <see cref="Revision"/> has moved on, and report no change when it has not — which is
    /// the property a resume rests on, and the one <see cref="UnitDriverContract"/> checks.
    /// </summary>
    public Task<bool> UpdateAsync(UnitContext context)
    {
        if (Own(context) is { } own) return own.UpdateAsync(context);

        Raise(context);
        Calls.Add($"{context.Name}:update");

        if (_deployed.TryGetValue(context.Name, out var at) && at == Revision) return Task.FromResult(false);

        _deployed[context.Name] = Revision;
        return Task.FromResult(true);
    }

    /// <inheritdoc/>
    public Task RemoveAsync(UnitContext context)
    {
        if (Own(context) is { } own) return own.RemoveAsync(context);

        Raise(context);
        Calls.Add($"{context.Name}:remove");
        _deployed.Remove(context.Name);            // already gone is fine — a teardown must be re-runnable
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task AwaitSettledAsync(UnitContext context)
    {
        if (Own(context) is { } own) return own.AwaitSettledAsync(context);

        Raise(context);
        // Recorded only when waiting IS the whole action, so Calls shows what the engine decided rather
        // than logging one for every create.
        if (Phases.GetValueOrDefault(context.Name) == UnitPhase.Converging) Calls.Add($"{context.Name}:await");
        if (Progress.TryGetValue(context.Name, out var percent))
            context.Progress($"{context.Label}: working…", percent);

        return Task.CompletedTask;
    }

    /// <summary>
    /// One resource per deployed unit — a stable id and a fingerprint that moves with
    /// <see cref="Revision"/>, so a plan reports drift when the deployment has moved on.
    /// </summary>
    public Task<IReadOnlyList<ResourceState>> RefreshAsync(UnitContext context)
    {
        if (Own(context) is { } own) return own.RefreshAsync(context);

        Refreshes[context.Name] = Refreshes.GetValueOrDefault(context.Name) + 1;
        Raise(context);

        if (Resources.TryGetValue(context.Name, out var scripted)) return Task.FromResult(scripted);

        return Task.FromResult<IReadOnlyList<ResourceState>>(
            _deployed.TryGetValue(context.Name, out var at)
                ? [new ResourceState($"memory://{context.Request.Prefix}/{context.Name}", "memory/unit", $"r{at}")]
                : []);
    }

    /// <summary>
    /// Yours if the unit is one of yours, otherwise removable — a dictionary entry always can be.
    /// </summary>
    /// <param name="context">The unit and the request.</param>
    /// <remarks>
    /// Forwarded rather than answered here, so an IRREVERSIBLE step of your own behaves in this target
    /// exactly as it will in a real one: the plan says RETAINED, the teardown leaves it, and its state is
    /// kept. A test target that quietly claimed everything is removable would let a publish unit pass here
    /// and surprise somebody in production, which is the single thing this type must never do (D24).
    /// </remarks>
    public bool IsRemovable(UnitContext context) => Own(context)?.IsRemovable(context) ?? true;

    /// <inheritdoc/>
    public Task<IReadOnlyList<string>> ValidateAsync(UnitContext context) =>
        Own(context)?.ValidateAsync(context) ?? Task.FromResult(Problems.GetValueOrDefault(context.Name, []));

    /// <inheritdoc/>
    public Task<IReadOnlyDictionary<string, string>> OutputsAsync(UnitContext context) =>
        Own(context)?.OutputsAsync(context) ?? Task.FromResult<IReadOnlyDictionary<string, string>>(
            _deployed.ContainsKey(context.Name) && Outputs.TryGetValue(context.Name, out var outputs)
                ? outputs
                : new Dictionary<string, string>());

    /// <summary>Raise this unit's scripted fault, if it has one, clearing a once-only fault as it goes.</summary>
    private void Raise(UnitContext context)
    {
        if (!Faults.TryGetValue(context.Name, out var fault)) return;
        if (fault.Once) Faults.Remove(context.Name);

        throw fault.Error;
    }
}

/// <summary>A failure a <see cref="MemoryTarget"/> has been told to produce.</summary>
/// <param name="Error">
/// The exception to raise. A <see cref="MemoryFaultException"/> carries a class the target's own classifier
/// reads back; anything else is unrecognised, which the engine treats as <see cref="FailureClass.Hard"/>.
/// </param>
/// <param name="Once">Raise it once and then succeed — a transient blip.</param>
public sealed record MemoryFault(Exception Error, bool Once = false);

/// <summary>
/// The exception a <see cref="MemoryTarget"/> raises, carrying the class its classifier will read back.
/// </summary>
/// <remarks>
/// A real type rather than a bare <see cref="Exception"/>, so a test can assert on it — and so the engine's
/// classify-then-decide path runs exactly as it does for a real provider.
/// </remarks>
/// <param name="failure">Credentials, transient, or hard.</param>
/// <param name="message">What the operator is told.</param>
public sealed class MemoryFaultException(FailureClass failure, string message) : Exception(message)
{
    /// <summary>What the operator should do next, in the only three flavours there are.</summary>
    public FailureClass Failure { get; } = failure;
}
