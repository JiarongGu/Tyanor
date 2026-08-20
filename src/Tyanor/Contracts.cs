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
/// A provider's implementation of the five things the engine needs to converge ONE unit.
///
/// <para>Everything provider-shaped lives behind this: status vocabulary, API calls, waiting, and error
/// classification (via <see cref="IFailureClassifier"/>). The engine calls these in an order decided
/// entirely by <see cref="Reconcile.Decide"/> and knows nothing else about the target.</para>
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
}
