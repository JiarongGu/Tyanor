using System.Text.Json;

namespace Tyanor.Engine.State;

/// <summary>
/// The one set of deployment state, in a JSON file at a location the developer chooses — the local half of
/// "local or remote, your call".
///
/// <para><b>What it does not do, stated rather than left to be discovered:</b> there are no conditional
/// writes. Two machines saving at the same instant will have one silently overwrite the other. That is
/// acceptable for a single operator or a single pipeline and is not acceptable for a team — which is what
/// a remote store is for, and why <see cref="DeploymentState.Serial"/> exists for a store that CAN check
/// it (an S3 precondition, a Postgres transaction). See <c>docs/DECISIONS.md</c> D12.</para>
/// </summary>
public sealed class FileStateStore : IStateStore
{
    /// <summary>
    /// The persisted schema version. Bump it when the stored shape changes in a way an older reader would
    /// misunderstand, and teach <see cref="Dto.ToState"/> to read the older one.
    /// </summary>
    private const int Schema = 1;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly JsonFile<Dto> _file;

    /// <summary>Keep state in a JSON file.</summary>
    /// <param name="path">Where the state file lives. Created, with its directory, on first write.</param>
    public FileStateStore(string path)
    {
        Path = path;
        // Never silently replaced: this file records what Tyanor OWNS, and losing it means a teardown can
        // no longer tell what it created from what was already there. Expensive to lose, so refuse.
        _file = new JsonFile<Dto>(path, Json, where =>
            $"The state at '{where}' is not readable JSON. Move it aside and re-sync with Refresh, " +
            "which rebuilds state from the real deployment.");
    }

    /// <summary>Where this state is stored.</summary>
    public string Path { get; }

    /// <inheritdoc/>
    public async Task<DeploymentState> GetAsync(string procedure, string prefix, CancellationToken ct = default) =>
        (await ReadAsync(ct)).FirstOrDefault(s => s.Procedure == procedure && s.Prefix == prefix)?.ToState()
        ?? DeploymentState.Empty(procedure, prefix);

    /// <summary>
    /// Write state at the next version.
    /// </summary>
    /// <remarks>
    /// The store owns the serial, which is what makes it mean anything: the caller hands back the version it
    /// READ, and this persists at one past it. No conditional check here — a plain file cannot do one, and
    /// the class summary says so rather than implying otherwise.
    /// </remarks>
    public Task SaveAsync(DeploymentState state, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(state);

        return _file.MutateAsync(all =>
        {
            all.RemoveAll(s => s.Procedure == state.Procedure && s.Prefix == state.Prefix);
            all.Add(Dto.From(state with { Serial = state.Serial + 1 }));
            return true;
        }, ct);
    }

    /// <inheritdoc/>
    public Task DeleteAsync(string procedure, string prefix, CancellationToken ct = default) =>
        _file.MutateAsync(all => all.RemoveAll(s => s.Procedure == procedure && s.Prefix == prefix) > 0, ct);

    private async Task<List<Dto>> ReadAsync(CancellationToken ct)
    {
        var all = await _file.ReadAsync(ct);

        // A file written by a NEWER Tyanor is refused rather than half-read. Reading it with this version's
        // assumptions would produce a state that looks fine and is wrong about what Tyanor owns, and the
        // first thing that notices would be a teardown.
        if (all.FirstOrDefault(d => d.Version > Schema) is { } ahead)
            throw new InvalidOperationException(
                $"The state at '{Path}' is version {ahead.Version}; this build of Tyanor understands " +
                $"{Schema}. It was written by a newer version — upgrade rather than overwrite it.");

        return all;
    }

    /// <summary>
    /// The persisted shape, kept separate from <see cref="DeploymentState"/> for the reason the run log
    /// already does it: the domain type is free to change, and a file written by an older version still has
    /// to load.
    /// </summary>
    /// <remarks>
    /// State had no such separation and no version at all, while the run LOG — the cheaper of the two to
    /// lose — had both. So the file that answers "what does Tyanor own?", and therefore what a teardown may
    /// remove, was the one with nothing standing between a property rename and an unreadable deployment.
    /// </remarks>
    private sealed record Dto(
        int Version,
        string Procedure,
        string Prefix,
        IReadOnlyList<UnitState> Units,
        DateTimeOffset UpdatedAt,
        long Serial)
    {
        public static Dto From(DeploymentState s) =>
            new(Schema, s.Procedure, s.Prefix, s.Units, s.UpdatedAt, s.Serial);

        /// <summary>A version of 0 is a file written before versioning existed; it is this shape.</summary>
        public DeploymentState ToState() => new(Procedure, Prefix, Units, UpdatedAt, Serial);
    }
}
