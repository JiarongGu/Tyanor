namespace Tyanor;

/// <summary>
/// One thing a provider created and Tyanor is responsible for.
///
/// <para><b>Opaque on purpose.</b> Tyanor stores the identity and a fingerprint; it does not model what an
/// S3 bucket or a Deployment *is*. That keeps resource modelling — and the dependency graph it drags
/// behind it — out of Core, while still letting state answer the question that matters:
/// <i>did we create this, and is it still what we left?</i></para>
/// </summary>
/// <param name="Id">Provider-scoped identity, stable across runs. The key everything else compares on.</param>
/// <param name="Type">The provider's own word for it ("AWS::S3::Bucket", "apps/v1 Deployment"). For display
/// and for the operator's judgement — Tyanor never branches on it.</param>
/// <param name="Fingerprint">
/// Whatever the provider considers "the state of this thing" — a version id, an ETag, a hash of the
/// resolved configuration. Two resources with the same <paramref name="Id"/> and different fingerprints
/// have CHANGED. Null means the provider cannot tell, and a plan will say so rather than guess.
/// </param>
public sealed record ResourceState(string Id, string Type, string? Fingerprint = null);

/// <summary>What Tyanor believes exists for one unit, and when it last looked.</summary>
/// <param name="Unit">The unit's name.</param>
/// <param name="Resources">Its resources, as last recorded or refreshed.</param>
/// <param name="RecordedAt">When this was written. Staleness is the operator's to judge — a plan reports it.</param>
public sealed record UnitState(string Unit, IReadOnlyList<ResourceState> Resources, DateTimeOffset RecordedAt);

/// <summary>
/// THE state for one deployment — one set, at a location the developer chooses, kept up to date by Tyanor.
///
/// <para><b>This is a mirror, and that is now deliberate</b> (see <c>docs/DECISIONS.md</c> D12, which
/// supersedes D1's "no mirror"). It is what makes two things possible that the provider alone cannot give:
/// knowing what Tyanor OWNS — so a teardown removes what it created and not what was already there — and
/// computing an honest add / change / destroy before anything is touched.</para>
///
/// <para><b>Drift is expected and repairable.</b> The world moves outside the tool; <c>Refresh</c> re-reads
/// reality and rewrites state to match, which is the answer to a mirror going stale — not surgery on the
/// tool's private bookkeeping.</para>
/// </summary>
/// <param name="Procedure">Which procedure.</param>
/// <param name="Prefix">Which deployment of it.</param>
/// <param name="Units">Per-unit resource state.</param>
/// <param name="UpdatedAt">When Tyanor last wrote this.</param>
/// <param name="Serial">
/// Incremented on every write. A store that supports conditional writes uses it to refuse a save based on
/// state someone else has since replaced — the cheap version of "don't clobber another machine".
/// </param>
public sealed record DeploymentState(
    string Procedure,
    string Prefix,
    IReadOnlyList<UnitState> Units,
    DateTimeOffset UpdatedAt,
    long Serial = 0)
{
    /// <summary>An empty state — nothing has been deployed yet, or it was torn down.</summary>
    public static DeploymentState Empty(string procedure, string prefix) =>
        new(procedure, prefix, [], DateTimeOffset.UtcNow);

    /// <summary>The recorded resources for a unit, or empty when Tyanor has no record of it.</summary>
    public IReadOnlyList<ResourceState> For(string unit) =>
        Units.FirstOrDefault(u => u.Unit == unit)?.Resources ?? [];

    /// <summary>Replace one unit's resources, bumping <see cref="Serial"/> and the timestamp.</summary>
    public DeploymentState With(string unit, IReadOnlyList<ResourceState> resources)
    {
        var next = Units.Where(u => u.Unit != unit).ToList();
        if (resources.Count > 0) next.Add(new UnitState(unit, resources, DateTimeOffset.UtcNow));
        return this with { Units = next, UpdatedAt = DateTimeOffset.UtcNow, Serial = Serial + 1 };
    }
}

/// <summary>
/// Where the one set of state lives — a local file, a bucket, a table. The developer's choice, exactly as
/// it is for a state-file tool, and the reason this is an interface rather than a path.
/// </summary>
public interface IStateStore
{
    /// <summary>
    /// Read the current state, or <see cref="DeploymentState.Empty"/> when there is none. Never null: "no
    /// state" and "an empty deployment" are the same thing to a plan, and forcing every caller to
    /// distinguish them invites a null check that quietly means "assume nothing exists".
    /// </summary>
    Task<DeploymentState> GetAsync(string procedure, string prefix, CancellationToken ct = default);

    /// <summary>
    /// Write state.
    /// </summary>
    /// <remarks>
    /// A store CAPABLE of conditional writes should refuse when the stored <see cref="DeploymentState.Serial"/>
    /// is not the one this state was derived from, and say so — that is the difference between two machines
    /// diverging visibly and one silently overwriting the other. A plain file store cannot, and says so in
    /// its own documentation rather than pretending.
    /// </remarks>
    Task SaveAsync(DeploymentState state, CancellationToken ct = default);

    /// <summary>Forget this deployment entirely — after a teardown, or to start over.</summary>
    Task DeleteAsync(string procedure, string prefix, CancellationToken ct = default);
}

/// <summary>What a plan says will happen to one resource.</summary>
public enum ResourceChange
{
    /// <summary>Not in state; will be created.</summary>
    Add,

    /// <summary>In state, and its fingerprint differs from what is deployed — or is unknowable.</summary>
    Change,

    /// <summary>In state but gone from the provider, or being removed.</summary>
    Destroy,

    /// <summary>In state, present, and unchanged.</summary>
    None,
}

/// <summary>
/// A difference between what state records and what the provider actually has — the thing
/// <c>Refresh</c> finds and a plan reports.
/// </summary>
/// <param name="Unit">Which unit it belongs to.</param>
/// <param name="Resource">The resource, as last recorded.</param>
/// <param name="Change">What happened to it outside Tyanor.</param>
public sealed record Drift(string Unit, ResourceState Resource, ResourceChange Change);
