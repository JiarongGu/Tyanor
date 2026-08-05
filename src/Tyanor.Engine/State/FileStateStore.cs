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
/// <param name="path">Where the state file lives. Created, with its directory, on first write.</param>
public sealed class FileStateStore(string path) : IStateStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Where this state is stored.</summary>
    public string Path { get; } = path;

    /// <inheritdoc/>
    public async Task<DeploymentState> GetAsync(string procedure, string prefix, CancellationToken ct = default)
    {
        var all = await ReadAsync(ct);
        return all.FirstOrDefault(s => s.Procedure == procedure && s.Prefix == prefix)
            ?? DeploymentState.Empty(procedure, prefix);
    }

    /// <inheritdoc/>
    public async Task SaveAsync(DeploymentState state, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var all = await ReadAsync(ct);
            all.RemoveAll(s => s.Procedure == state.Procedure && s.Prefix == state.Prefix);
            all.Add(state);
            await WriteAsync(all, ct);
        }
        finally { _gate.Release(); }
    }

    /// <inheritdoc/>
    public async Task DeleteAsync(string procedure, string prefix, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var all = await ReadAsync(ct);
            if (all.RemoveAll(s => s.Procedure == procedure && s.Prefix == prefix) > 0) await WriteAsync(all, ct);
        }
        finally { _gate.Release(); }
    }

    private async Task<List<DeploymentState>> ReadAsync(CancellationToken ct)
    {
        if (!File.Exists(Path)) return [];
        try
        {
            await using var stream = File.OpenRead(Path);
            return await JsonSerializer.DeserializeAsync<List<DeploymentState>>(stream, Json, ct) ?? [];
        }
        catch (JsonException)
        {
            // Never silently replaced: this file records what Tyanor OWNS, and losing it means a teardown
            // can no longer tell what it created from what was already there.
            throw new InvalidOperationException(
                $"The state at '{Path}' is not readable JSON. Move it aside and re-sync with Refresh, " +
                "which rebuilds state from the real deployment.");
        }
    }

    private async Task WriteAsync(List<DeploymentState> all, CancellationToken ct)
    {
        var dir = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var temp = Path + ".tmp";
        await using (var stream = File.Create(temp))
            await JsonSerializer.SerializeAsync(stream, all, Json, ct);

        if (File.Exists(Path)) File.Replace(temp, Path, null);
        else File.Move(temp, Path);
    }
}
