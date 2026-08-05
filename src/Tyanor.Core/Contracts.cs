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
    Task<TargetIdentity> ValidateAsync(TargetCredentials credentials, CancellationToken ct);

    /// <summary>The per-unit driver.</summary>
    IUnitDriver Driver { get; }

    /// <summary>How to read this provider's failures.</summary>
    IFailureClassifier Classifier { get; }
}
