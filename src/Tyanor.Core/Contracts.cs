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

    /// <summary>True when every named part is present — check BEFORE starting, so a missing input is a
    /// clear refusal rather than a failure three units in.</summary>
    public bool Has(params string[] names) => names.All(Parts.ContainsKey);
}

/// <summary>
/// One request to converge a target on a desired state.
/// </summary>
/// <param name="Prefix">Operator-chosen name base. Units deploy as <c>{Prefix}-{unit}</c>, which is what
/// lets one account host several independent deployments of the same procedure.</param>
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
    Task<UnitPhase> PhaseAsync(ProcedureUnit unit, DeploymentRequest request, CancellationToken ct);

    /// <summary>Create the unit. Must NOT wait for convergence — the engine does that separately, so that
    /// attaching to someone else's in-flight operation uses the identical wait.</summary>
    Task CreateAsync(ProcedureUnit unit, DeploymentRequest request, CancellationToken ct);

    /// <summary>
    /// Apply the desired configuration to an existing unit. Return <c>false</c> when the provider reports
    /// there is nothing to change — that is a SUCCESS (and on a resume it is the common case), not an
    /// error to be swallowed by the caller.
    /// </summary>
    Task<bool> UpdateAsync(ProcedureUnit unit, DeploymentRequest request, CancellationToken ct);

    /// <summary>Remove the unit and wait until it is gone. Removal is the one operation the engine cannot
    /// meaningfully attach to halfway, so drivers own the wait.</summary>
    Task RemoveAsync(ProcedureUnit unit, DeploymentRequest request, CancellationToken ct);

    /// <summary>
    /// Wait until the unit stops converging. Throw if it settles into a failed state — the engine turns
    /// that into a classified outcome. Report progress through <paramref name="report"/> as it goes.
    /// </summary>
    Task AwaitSettledAsync(ProcedureUnit unit, DeploymentRequest request, Action<ProgressReport> report, CancellationToken ct);

    /// <summary>
    /// What actually exists for this unit right now, as the provider sees it. This is how state gets
    /// re-synced from a real deployment rather than trusted: a refresh re-reads reality and state is
    /// rewritten to match, so drift is repaired instead of accumulating.
    /// </summary>
    /// <remarks>
    /// Return an empty list when the unit is absent — that is a fact, not a failure. A provider that
    /// genuinely cannot enumerate its resources should return empty and leave `Fingerprint` null
    /// elsewhere; a plan then reports what it cannot know rather than inventing certainty.
    /// </remarks>
    Task<IReadOnlyList<ResourceState>> RefreshAsync(ProcedureUnit unit, DeploymentRequest request, CancellationToken ct);
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
