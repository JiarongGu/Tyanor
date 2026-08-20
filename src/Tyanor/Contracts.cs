namespace Tyanor;

/// <summary>
/// Credentials for a target. Key/secret/region cover the common clouds; <paramref name="Extra"/> carries
/// whatever else one needs (subscription id, project id, kubeconfig context) WITHOUT that provider's
/// vocabulary reaching this contract.
/// </summary>
public sealed record TargetCredentials(
    string KeyId, string Secret, string? Region = null, IReadOnlyDictionary<string, string>? Extra = null);

/// <summary>Who the provider says we are, once the credentials have actually been exercised.</summary>
/// <param name="Ok">The provider accepted them.</param>
/// <param name="Account">Account/subscription/project identifier, for showing the operator WHERE they are
/// about to deploy — the cheapest guard against deploying into the wrong account.</param>
/// <param name="Principal">The identity itself (user, role, service account).</param>
/// <param name="Error">Why the provider refused. Present iff <paramref name="Ok"/> is false.</param>
public sealed record TargetIdentity(bool Ok, string? Account = null, string? Principal = null, string? Error = null);

/// <summary>
/// What is being deployed, as OPAQUE named parts.
///
/// <para><b>This is the type that keeps Tyanor provider-neutral, and it is the one most likely to be
/// eroded.</b> The deployer this was extracted from had <c>CdkOutDir</c> and <c>WebDir</c> on its request
/// type — so the "generic" interface named an AWS tool and assumed a single-page app, and no second
/// provider could have implemented it. Parts are <c>name → path</c>; only the provider knows that
/// <c>"infrastructure"</c> happens to be a synthesized CloudFormation assembly, or that <c>"web"</c> is
/// static files for a bucket.</para>
/// </summary>
/// <param name="Parts">Logical name → local path. Names are the procedure's business, not Tyanor's.</param>
public sealed record DeploymentArtifact(IReadOnlyDictionary<string, string> Parts)
{
    /// <summary>The path for a part, or null when the artifact does not carry it.</summary>
    public string? Part(string name) => Parts.TryGetValue(name, out var p) ? p : null;

    /// <summary>
    /// The local path for a part that MUST be there, refusing clearly when it is not.
    /// </summary>
    /// <param name="name">The part name, as the procedure chose it.</param>
    /// <param name="expect">What the part has to be on disk. Checked, because a part pointing at the wrong
    /// kind of thing fails later and more confusingly than it needs to.</param>
    /// <exception cref="ArtifactException">The part is absent, or is not what <paramref name="expect"/> says.</exception>
    /// <remarks>
    /// <para>Here rather than in each provider because the first two both wrote it, identically, down to the
    /// three separate failure messages — and the third would have written it again, slightly differently, so
    /// that operators got a different sentence about the same mistake depending on where they deployed.</para>
    /// <para>The failure is always terminal: no amount of retrying produces a file the build did not make.
    /// Providers do not need to classify it — <see cref="IFailureClassifier"/> returning null for it is
    /// correct, and the engine's default for null is <see cref="FailureClass.Hard"/>.</para>
    /// </remarks>
    public string RequirePart(string name, ArtifactPart expect = ArtifactPart.Any)
    {
        var path = Part(name)
            ?? throw new ArtifactException($"The artifact has no part named '{name}'. It carries: {Describe()}.");

        var ok = expect switch
        {
            ArtifactPart.Directory => Directory.Exists(path),
            ArtifactPart.File => File.Exists(path),
            _ => Directory.Exists(path) || File.Exists(path),
        };

        if (!ok)
            throw new ArtifactException(
                $"Artifact part '{name}' points at '{path}', which is not " +
                $"{(expect == ArtifactPart.Directory ? "a directory" : expect == ArtifactPart.File ? "a file" : "on this machine")}. " +
                "Build first.");

        return path;
    }

    /// <summary>What this artifact carries, for a message. Never an empty string.</summary>
    /// <remarks>
    /// Internal because it exists for two error messages and is not a question anyone else asks —
    /// <see cref="Parts"/> is already public for anyone who wants the names themselves.
    /// </remarks>
    internal string Describe() =>
        string.Join(", ", Parts.Keys.OrderBy(k => k, StringComparer.Ordinal).DefaultIfEmpty("nothing"));
}

/// <summary>What an artifact part has to be on disk.</summary>
public enum ArtifactPart
{
    /// <summary>Either — the provider does not care which.</summary>
    Any,

    /// <summary>A tree of files: a publish output, a bundle, a chart.</summary>
    Directory,

    /// <summary>A single file: a template, a manifest, an archive.</summary>
    File,
}

/// <summary>
/// The artifact does not carry what the procedure asked for — a part that is not there, or one pointing at
/// nothing. Always terminal: a build that did not happen does not start happening because someone retried.
/// </summary>
/// <param name="message">Plain language, naming what the artifact DOES carry.</param>
public sealed class ArtifactException(string message) : DefinitionException(message);

/// <summary>
/// A setting is written where it cannot mean what it says — today, a unit's ADDRESS written procedure-wide.
/// Always terminal: nothing about it improves on a retry.
/// </summary>
/// <param name="message">Plain language, naming the setting and the spelling that would work.</param>
/// <remarks>
/// Separate from <see cref="ArtifactException"/> because it is a different conversation with the operator:
/// the artifact one means "the build did not produce what you named", and this one means "you named it in a
/// place where it applies to everything". Both are <see cref="DefinitionException"/>, so a consumer that
/// only wants to tell configuration from a provider saying no still catches one type.
/// </remarks>
public sealed class OptionException(string message) : DefinitionException(message);

/// <summary>
/// One request to converge a target on a desired state.
/// </summary>
/// <param name="Prefix">
/// Operator-chosen name base. Units deploy as <c>{Prefix}-{unit}</c>, which is what lets one account host
/// several independent deployments of the same procedure.
/// <para>Checked, because it is not a label: it becomes a directory under a provider's root and a component
/// of resource names. Letters, digits, <c>-</c>, <c>_</c> and <c>.</c> only; no leading dot, no <c>..</c>,
/// at most 255 characters — so a name can never be read as a path. A provider adds its own stricter rule in
/// its own words (<c>docs/DECISIONS.md</c> D17).</para>
/// </param>
/// <param name="Artifact">What to deploy.</param>
/// <param name="Options">Procedure- and provider-specific settings (a compute tier, a domain name, a
/// replica count). Deliberately untyped here: the moment this becomes a fixed set of fields, it grows one
/// field per provider and stops being neutral.</param>
/// <param name="Tags">Applied to created resources where the provider supports them.</param>
public sealed record DeploymentRequest(
    string Prefix,
    DeploymentArtifact Artifact,
    IReadOnlyDictionary<string, string>? Options = null,
    IReadOnlyDictionary<string, string>? Tags = null)
{
    private readonly string _prefix = Identifiers.Require(Prefix, "prefix");

    /// <inheritdoc cref="Prefix"/>
    public string Prefix
    {
        get => _prefix;
        init => _prefix = Identifiers.Require(value, "prefix");
    }

    /// <summary>An option value, or null.</summary>
    public string? Option(string key) => Options is not null && Options.TryGetValue(key, out var v) ? v : null;

    /// <summary>
    /// An option scoped to ONE unit, falling back to the procedure-wide value: <c>"{unit}.{key}"</c>, then
    /// <c>"{key}"</c>.
    /// </summary>
    /// <param name="unit">The unit's <see cref="ProcedureUnit.Name"/>.</param>
    /// <param name="key">The setting.</param>
    /// <remarks>
    /// <para>A provider whose units are all the SAME kind of thing never needs this — every CloudFormation
    /// unit is a stack, so the unit name is the whole of its configuration. A provider with
    /// <b>heterogeneous</b> units (a directory here, a long-running process there) has to configure each
    /// one differently, and without a convention in the contract every provider invents its own. See
    /// <c>docs/DECISIONS.md</c> D13, which is where this came from.</para>
    /// <para>The fallback is what stops it becoming verbose: a setting that is the same for every unit is
    /// written once, unscoped, and only the exceptions are named.</para>
    /// </remarks>
    public string? Option(string unit, string key) => Option($"{unit}.{key}") ?? Option(key);

    /// <summary>
    /// A setting for ONE unit and no other — <c>"{unit}.{key}"</c>, with no fall back to the shared value.
    /// </summary>
    /// <param name="unit">The unit's <see cref="ProcedureUnit.Name"/>.</param>
    /// <param name="key">The setting.</param>
    /// <remarks>
    /// <para><b>For a setting that IS the unit's identity</b> — where it lives on disk, which bucket it fills,
    /// which port it answers on. The shared fallback that makes
    /// <see cref="Option(string, string)"/> convenient is exactly wrong for these: a procedure-wide
    /// <c>"path"</c> does not mean "every unit defaults to this directory", it means every unit deploys ON
    /// TOP of every other, silently, and removing one removes them all.</para>
    /// <para>That is the collision <see cref="Procedure"/> refuses when two units share a name, arriving
    /// through a different door — a unit's address must be its own however it is spelled. So an
    /// identity-bearing setting is read with this, and everything else with the convenient one.</para>
    /// </remarks>
    public string? OwnOption(string unit, string key) => Option($"{unit}.{key}");

    /// <summary>
    /// A setting that IS the unit's address, read per unit — and REFUSED rather than misapplied when it was
    /// written procedure-wide. Null when it is not set at all, which is the caller's to interpret.
    /// </summary>
    /// <param name="unit">The unit's <see cref="ProcedureUnit.Name"/>.</param>
    /// <param name="key">The setting: its path, its bucket, its port.</param>
    /// <exception cref="OptionException">
    /// This unit has no <c>"{unit}.{key}"</c> and an unscoped <c>"{key}"</c> is set, so the value the
    /// operator wrote is one no unit can honestly use.
    /// </exception>
    /// <remarks>
    /// <para><b><see cref="OwnOption"/> with the silence taken out.</b> Reading an address with
    /// <see cref="Option(string, string)"/> shares it — every unit gets the same one, which for an address
    /// is every unit deploying on top of every other. Reading it with <see cref="OwnOption"/> fixes that and
    /// leaves a second, quieter fault: the line the operator wrote is now read by nothing at all, and
    /// nothing says so. Both shipped providers had one of the two, which is what produced this method
    /// (<c>docs/DECISIONS.md</c> D36).</para>
    /// <para><b>It throws a <see cref="DefinitionException"/>, so ONE call gets both halves</b> —
    /// <see cref="UnitProblems.Check"/> collects it, so <see cref="IUnitDriver.ValidateAsync"/> reports it
    /// offline, and an apply that skipped validation refuses at the point of use. That is
    /// <see cref="UnitContext.RequirePart"/>'s shape, for the same reason: an offline check and the real
    /// thing must not be able to disagree.</para>
    /// <para><b>The unit's own value WINS rather than also being refused</b>, deliberately. The refusal
    /// fires exactly when the shared value would otherwise be used or dropped in silence; a unit that named
    /// its own address has nothing silently wrong with it. An unscoped key left over beside a full set of
    /// per-unit ones is dead configuration rather than a misapplied value, and reporting it once per unit
    /// would name every unit but the one line to delete.</para>
    /// </remarks>
    public string? Address(string unit, string key)
    {
        if (OwnOption(unit, key) is { } own) return own;
        if (Option(key) is null) return null;

        throw new OptionException(
            $"'{key}' is set for the whole procedure, but unit '{unit}' reads it as its own address — a " +
            "shared one is not a default, it is every unit using the same value and overwriting each " +
            $"other. Write \"{unit}.{key}\".");
    }

    /// <summary>
    /// A whole GROUP of options for one unit, gathered by prefix and returned with the prefix stripped:
    /// <c>"{unit}.{prefix}.Name"</c> and <c>"{prefix}.Name"</c> both yield <c>Name</c>, and the unit-scoped
    /// one wins.
    /// </summary>
    /// <param name="unit">The unit's <see cref="ProcedureUnit.Name"/>.</param>
    /// <param name="prefix">The group, without a trailing dot — <c>"parameter"</c>, <c>"label"</c>, <c>"env"</c>.</param>
    /// <remarks>
    /// Some settings are a SET whose keys the provider cannot know in advance — CloudFormation stack
    /// parameters, Kubernetes labels, environment variables for a process. Reading them one at a time is
    /// impossible and encoding them into one value re-invents a serialization format inside a string, which
    /// is how an untyped map becomes worse than typed fields rather than better. This keeps the untyped map
    /// and lets a group be authored one line per entry.
    /// </remarks>
    public IReadOnlyDictionary<string, string> OptionSet(string unit, string prefix)
    {
        var gathered = new Dictionary<string, string>();
        if (Options is null) return gathered;

        // Unscoped first, then unit-scoped over the top — the same "shared default, named exception" shape
        // as Option(unit, key), so one procedure-wide parameter does not have to be repeated per unit.
        foreach (var scope in (string[])[$"{prefix}.", $"{unit}.{prefix}."])
            foreach (var (key, value) in Options)
                if (key.StartsWith(scope, StringComparison.Ordinal) && key.Length > scope.Length)
                    gathered[key[scope.Length..]] = value;

        return gathered;
    }
}

/// <summary>
/// A provider's implementation of the six things the engine needs to converge ONE unit, plus three it can
/// manage without.
///
/// <para>Everything provider-shaped lives behind this: status vocabulary, API calls, waiting, and error
/// classification (via <see cref="IFailureClassifier"/>). The engine calls these in an order decided
/// entirely by <see cref="Reconcile.Decide"/> and knows nothing else about the target.</para>
///
/// <para><b>Required:</b> <see cref="PhaseAsync"/>, <see cref="CreateAsync"/>, <see cref="UpdateAsync"/>,
/// <see cref="RemoveAsync"/>, <see cref="AwaitSettledAsync"/>, <see cref="RefreshAsync"/>. <b>Defaulted:</b>
/// <see cref="ValidateAsync"/>, <see cref="OutputsAsync"/> and <see cref="IsRemovable"/> — each arrived
/// later meaning <i>I do not do that</i>, which is how this contract grows without breaking every
/// implementation, including the ones written outside this repository (<c>docs/DECISIONS.md</c> D18).</para>
///
/// <para><b>A unit that is one STEP rather than infrastructure</b> — a check, a gate, a migration — should
/// start from <see cref="StepUnitDriver"/> and write two methods instead of six.</para>
/// </summary>
public interface IUnitDriver
{
    /// <summary>What the provider says about this unit right now, normalized. This is the ONLY place a
    /// provider's status strings are interpreted.</summary>
    /// <param name="context">The unit, the request, progress and cancellation.</param>
    /// <remarks>
    /// Must be READ-ONLY. It runs during a plan, and a driver that repairs something while reporting on it
    /// makes the plan a lie and the apply a surprise.
    /// </remarks>
    Task<UnitPhase> PhaseAsync(UnitContext context);

    /// <summary>Create the unit. Must NOT wait for convergence — the engine does that separately, so that
    /// attaching to someone else's in-flight operation uses the identical wait.</summary>
    /// <param name="context">The unit, the request, progress and cancellation.</param>
    /// <remarks>
    /// "Does not wait" means does not wait for a CONTROL PLANE. A provider without one does its work right
    /// here — copying files, starting a process — and should report progress while doing it.
    /// </remarks>
    Task CreateAsync(UnitContext context);

    /// <summary>
    /// Apply the desired configuration to an existing unit. Return <c>false</c> when the provider reports
    /// there is nothing to change — that is a SUCCESS (and on a resume it is the common case), not an
    /// error to be swallowed by the caller.
    /// </summary>
    /// <param name="context">The unit, the request, progress and cancellation.</param>
    Task<bool> UpdateAsync(UnitContext context);

    /// <summary>Remove the unit and wait until it is gone. Removal is the one operation the engine cannot
    /// meaningfully attach to halfway, so drivers own the wait — and should report progress through it,
    /// because a teardown that takes minutes and says nothing looks like one that has frozen.</summary>
    /// <param name="context">The unit, the request, progress and cancellation.</param>
    /// <remarks>
    /// Never called for a unit whose <see cref="IsRemovable"/> is false, so an irreversible one does not
    /// have to lie or throw here.
    /// </remarks>
    Task RemoveAsync(UnitContext context);

    /// <summary>
    /// Whether a teardown can take this unit away at all. <c>false</c> for something IRREVERSIBLE by
    /// nature — a published package version, an audit record, a sent email, a migration that cannot be
    /// rolled back.
    /// </summary>
    /// <param name="context">The unit and the request, because a provider dispatching several KINDS
    /// answers differently per unit.</param>
    /// <remarks>
    /// <para><b>This exists because "remove and wait until it is gone" is not a promise every unit can
    /// make.</b> A publish cannot be unpublished, so before this the only options were to lie — return
    /// quietly and let a destroy report success over something that is still out there — or to throw, and
    /// fail a teardown that had nothing wrong with it. Both were worse than saying so.</para>
    /// <para>Answering <c>false</c> makes <see cref="Reconcile.DecideDestroy"/> choose
    /// <see cref="ReconcileAction.Retain"/>: the plan lists the unit as RETAINED before anything runs, the
    /// teardown leaves it alone, and its state is KEPT rather than cleared — because Tyanor still owns it,
    /// and forgetting that is how a resource becomes unmanaged and unmentioned.</para>
    /// <para><b>It is not a permission check.</b> "I am not allowed to delete this today" is a credential
    /// failure and belongs in <see cref="IFailureClassifier"/>. This is about what the thing IS.</para>
    /// <para>Defaulted to <c>true</c>, so adding it broke no implementation — the pattern
    /// <c>docs/DECISIONS.md</c> D18 established for growing this contract: a new capability arrives meaning
    /// <i>I do not do that</i>.</para>
    /// </remarks>
    bool IsRemovable(UnitContext context) => true;

    /// <summary>
    /// Wait until the unit stops converging. Throw if it settles into a failed state — the engine turns
    /// that into a classified outcome. Report progress as it goes.
    /// </summary>
    /// <param name="context">The unit, the request, progress and cancellation.</param>
    Task AwaitSettledAsync(UnitContext context);

    /// <summary>
    /// What actually exists for this unit right now, as the provider sees it. This is how state gets
    /// re-synced from a real deployment rather than trusted: a refresh re-reads reality and state is
    /// rewritten to match, so drift is repaired instead of accumulating.
    /// </summary>
    /// <param name="context">The unit, the request, progress and cancellation.</param>
    /// <remarks>
    /// Return an empty list when the unit is absent — that is a fact, not a failure. A provider that
    /// genuinely cannot enumerate its resources should return empty and leave `Fingerprint` null
    /// elsewhere; a plan then reports what it cannot know rather than inventing certainty.
    /// </remarks>
    Task<IReadOnlyList<ResourceState>> RefreshAsync(UnitContext context);

    /// <summary>
    /// Everything wrong with this unit's CONFIGURATION, found without touching the target.
    /// </summary>
    /// <param name="context">The unit and the request. Progress and cancellation are available but unused by
    /// most providers — there is nothing slow to narrate.</param>
    /// <returns>One entry per problem; empty when the unit is configured correctly.</returns>
    /// <remarks>
    /// <para><b>Make no network calls here.</b> The value of this is that a whole procedure can be checked
    /// before an account exists, before credentials are entered, and before anything is created. A provider
    /// that reaches for its API turns an offline check into an online one and takes that away.</para>
    /// <para><b>Reuse the resolution the apply does</b> rather than writing the checks twice — resolve the
    /// options and artifact parts exactly as <see cref="CreateAsync"/> would and collect the
    /// <see cref="DefinitionException"/>s. Two copies of a rule is two rules, and they diverge.</para>
    /// <para>Returning nothing is a legitimate answer for a provider with no configuration to get wrong,
    /// which is why this has a default and adding it broke nobody.</para>
    /// </remarks>
    Task<IReadOnlyList<string>> ValidateAsync(UnitContext context) =>
        Task.FromResult<IReadOnlyList<string>>([]);

    /// <summary>
    /// What this unit PRODUCED that someone outside might need — a URL, an endpoint, a generated name.
    /// </summary>
    /// <param name="context">The unit and the request.</param>
    /// <returns>Name → value; empty when the unit produces nothing, or is not deployed.</returns>
    /// <remarks>
    /// <para>Read from the target rather than from state, for the same reason phases are: what a deployment
    /// currently exposes is a fact about the deployment. A stored copy would be one more thing that can be
    /// stale, and the honest answer to "what is my site's address" is the one the provider gives now.</para>
    /// <para>Absent is empty, not an exception — asking a procedure that is not deployed yet what it produced
    /// is a reasonable question with the answer "nothing".</para>
    /// </remarks>
    Task<IReadOnlyDictionary<string, string>> OutputsAsync(UnitContext context) =>
        Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());
}

/// <summary>
/// Everything a target is given when a completed teardown asks it to remove what it created for ITSELF.
///
/// <para>Scoped to the DEPLOYMENT rather than to a unit, which is the whole point: <see cref="Procedure"/>
/// and <see cref="DeploymentRequest.Prefix"/> together are the same key state and history are filed under,
/// and provider-owned infrastructure belongs to that pair rather than to any one unit in it.</para>
/// </summary>
/// <param name="Procedure">Which procedure was torn down — <see cref="Tyanor.Procedure.Name"/>.</param>
/// <param name="Request">The deployment it was torn down from. The prefix is the identifying half.</param>
/// <param name="Report">Progress, for a sweep slow enough to be worth narrating.</param>
/// <param name="Cancellation">Cancelling leaves the run LIVE, exactly as it does anywhere else.</param>
public sealed record SweepContext(
    string Procedure,
    DeploymentRequest Request,
    Action<ProgressReport> Report,
    CancellationToken Cancellation)
{
    /// <summary>
    /// A context with progress going nowhere and nothing to cancel — for calling a target DIRECTLY, which
    /// tests and tooling do and the engine never does.
    /// </summary>
    /// <param name="procedure">Which procedure was torn down.</param>
    /// <param name="request">The deployment it was torn down from.</param>
    public SweepContext(string procedure, DeploymentRequest request)
        : this(procedure, request, _ => { }, CancellationToken.None) { }

    /// <summary>Which deployment of the procedure — the identifying half of the pair.</summary>
    public string Prefix => Request.Prefix;

    /// <summary>A procedure-wide option. There is no unit to scope one to.</summary>
    /// <param name="key">The setting.</param>
    public string? Option(string key) => Request.Option(key);

    /// <summary>Say something to whoever is watching. Reported against the procedure, as run-level lines are.</summary>
    /// <param name="message">Plain language, for a person.</param>
    /// <param name="status">Tone.</param>
    public void Progress(string message, ProgressStatus status = ProgressStatus.Info) =>
        Report(new ProgressReport(Procedure, message, -1, status));

    /// <summary>Throw if the caller has cancelled.</summary>
    public void ThrowIfCancelled() => Cancellation.ThrowIfCancellationRequested();
}

/// <summary>A deployment target: credentials, identity, and a driver for its units.</summary>
public interface IDeploymentTarget
{
    /// <summary>Stable provider id ("aws", "kubernetes", "local").</summary>
    string Id { get; }

    /// <summary>Exercise the credentials against the provider and report who we are. A real call — "the
    /// fields are filled in" is not validation, and the operator deserves to see the account before a
    /// deployment starts.</summary>
    /// <param name="credentials">
    /// <c>null</c> when the target authenticates AMBIENTLY and there is nothing to supply: the local
    /// machine's own user, an instance role, a kubeconfig context already selected, a session someone else
    /// established.
    /// <para>This was originally non-nullable, which quietly asserted that every target has a key and a
    /// secret — the same species of assumption as the <c>CdkOutDir</c> in <c>docs/DECISIONS.md</c> D4, and
    /// invisible while every target was a cloud. A target that DOES need credentials and is handed none
    /// returns <see cref="TargetIdentity"/> with <c>Ok: false</c> and says so; it does not throw, because
    /// "who am I" is a question with an answer, not an error.</para>
    /// </param>
    /// <param name="ct">Cancellation.</param>
    Task<TargetIdentity> ValidateAsync(TargetCredentials? credentials, CancellationToken ct);

    /// <summary>The per-unit driver.</summary>
    IUnitDriver Driver { get; }

    /// <summary>How to read this provider's failures.</summary>
    IFailureClassifier Classifier { get; }

    /// <summary>
    /// Remove what THIS PROVIDER created for its own use during a deployment — after a full teardown has
    /// finished with every unit. Do nothing if there is nothing of yours to remove.
    /// </summary>
    /// <param name="context">The procedure and deployment that were just torn down, progress and cancellation.</param>
    /// <remarks>
    /// <para><b>Why this cannot be a unit's job, which is the whole reason it exists.</b> Both shipped
    /// providers keep bookkeeping of their own for a deployment: the AWS one creates a staging bucket to
    /// upload templates and assets through, and the local one keeps a directory of pid files beside the
    /// units. Neither belongs to any single unit — every unit uses it — so a unit removing it would be
    /// reaching sideways to delete something the units either side of it still need, which is exactly what
    /// removing in reverse order exists to make impossible
    /// (<c>.claude/rules/units-not-graphs.md</c>). Before this, nothing removed either: a destroy took away
    /// every unit and left the provider's own scaffolding standing, for ever, and
    /// <c>docs/adoption.md</c> claimed a teardown left nothing. See <c>docs/DECISIONS.md</c> D33.</para>
    /// <para><b>Called only after a FULL destroy.</b> A narrowed one (<see cref="Procedure.Only"/>) is a
    /// partial teardown by request — the rest of the deployment is still there and still needs whatever this
    /// would remove — so it never sweeps. A destroy that paused or failed part-way does not reach here
    /// either, for the same reason.</para>
    /// <para><b>Some units may have been RETAINED.</b> An irreversible unit is one a teardown will never
    /// take away (<see cref="IUnitDriver.IsRemovable"/>), so waiting for it would mean never sweeping at
    /// all. If your scaffolding is genuinely still needed by something retained, do not remove it — you are
    /// the only one who can know that.</para>
    /// <para><b>It must be re-runnable and it must tolerate nothing being there</b>, because a teardown is
    /// re-runnable and the second one reaches this with the first one's work already done.</para>
    /// <para><b>Failing here does not fail the teardown.</b> The units are gone; the deployment IS
    /// destroyed, and failing the run would send an operator to re-run a destroy with nothing left to do.
    /// The engine reports the failure loudly instead and the run still succeeds — so raise something whose
    /// message names what was left behind, since that message is all the operator gets.</para>
    /// <para>Defaulted to doing nothing, so adding it broke no implementation — the pattern
    /// <c>docs/DECISIONS.md</c> D18 established: a new capability arrives meaning <i>I do not do that</i>.
    /// A provider that creates nothing of its own is correct to leave it alone.</para>
    /// </remarks>
    Task SweepAsync(SweepContext context) => Task.CompletedTask;
}
