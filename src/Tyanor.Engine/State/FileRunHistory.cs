using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tyanor.Engine.State;

/// <summary>
/// Run history in one JSON file at a location the CONSUMER chooses.
///
/// <para><b>This is state, and Tyanor needs it.</b> What Tyanor does not keep is a mirror of the
/// provider's resources — see <c>docs/DECISIONS.md</c> D1/D7. What it must keep is the record of INTENT:
/// that a run was attempted, with which configuration, and how it ended. Without something durable here a
/// run cannot be resumed after the process dies, which is the whole point of the engine.</para>
///
/// <para><b>Why a plain file is the default.</b> Tyanor is local-first, and the default should work with
/// no server, no schema migration and no dependency — a library that needs a database before it can record
/// that a deployment started has the wrong default. Consumers that want a real store implement
/// <see cref="IRunHistory"/>; that is the seam, and this is one implementation of it.</para>
///
/// <para>Every write goes through a temp file and an atomic replace, because the failure this class exists
/// to survive is the process dying — and dying midway through rewriting the file that records what was
/// happening would be a poor way to learn that lesson.</para>
/// </summary>
/// <param name="path">Where the file lives. Created, with its directory, on first write.</param>
public sealed class FileRunHistory(string path) : IRunHistory
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,                                        // a human may need to read this
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Where this history is stored.</summary>
    public string Path { get; } = path;

    /// <inheritdoc/>
    public async Task UpsertAsync(RunRecord record, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            // Re-read rather than trusting an in-memory copy: another process may own the same file, and a
            // local-first tool has no lock server to arbitrate. Last writer wins, per RUN, not per file.
            var all = await ReadAsync(ct);
            var i = all.FindIndex(r => r.Id == record.Id);
            if (i >= 0) all[i] = Dto.From(record); else all.Add(Dto.From(record));
            await WriteAsync(all, ct);
        }
        finally { _gate.Release(); }
    }

    /// <inheritdoc/>
    public async Task<RunRecord?> LiveAsync(string procedure, string prefix, CancellationToken ct = default)
    {
        var all = await ReadAsync(ct);
        // Newest first: if more than one is somehow live, the latest attempt is the one to resume.
        return all.AsEnumerable().Reverse()
            .Select(d => d.ToRecord())
            .FirstOrDefault(r => r.Procedure == procedure && r.Prefix == prefix && r.IsLive);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<RunRecord>> RecentAsync(int limit = 50, CancellationToken ct = default)
    {
        var all = await ReadAsync(ct);
        return all.AsEnumerable().Reverse().Take(limit).Select(d => d.ToRecord()).ToList();
    }

    /// <summary>
    /// Delete a finished run. REFUSES a live one: it is the operator's only handle on work that may still
    /// be converging in the provider, and deleting it strands that work with nothing left to say it is
    /// happening. See <c>.claude/rules/reconcile-dont-mirror.md</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The run is running or paused.</exception>
    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var all = await ReadAsync(ct);
            var found = all.FirstOrDefault(d => d.Id == id);
            if (found is null) return;                               // already gone — deleting is idempotent
            if (found.ToRecord().IsLive)
                throw new InvalidOperationException(
                    $"Run '{id}' is {found.Status} and may still be converging in the provider. " +
                    "Finish or resume it before deleting the record.");
            all.Remove(found);
            await WriteAsync(all, ct);
        }
        finally { _gate.Release(); }
    }

    private async Task<List<Dto>> ReadAsync(CancellationToken ct)
    {
        if (!File.Exists(Path)) return [];
        try
        {
            await using var stream = File.OpenRead(Path);
            return await JsonSerializer.DeserializeAsync<List<Dto>>(stream, Json, ct) ?? [];
        }
        catch (JsonException)
        {
            // A truncated or hand-mangled file must not make the tool unusable — but it must also not be
            // silently overwritten, because it may be the only record of something still running.
            throw new InvalidOperationException(
                $"The run history at '{Path}' is not readable JSON. Move it aside to start a fresh history.");
        }
    }

    private async Task WriteAsync(List<Dto> all, CancellationToken ct)
    {
        var dir = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // Write-then-replace. A crash leaves either the old file or the new one, never half of either.
        var temp = Path + ".tmp";
        await using (var stream = File.Create(temp))
            await JsonSerializer.SerializeAsync(stream, all, Json, ct);

        if (File.Exists(Path)) File.Replace(temp, Path, null);
        else File.Move(temp, Path);
    }

    /// <summary>
    /// The persisted shape, kept separate from <see cref="RunRecord"/> on purpose: the domain type is free
    /// to change, and a file written by an older version still has to load. <see cref="PauseReason"/> is
    /// stored as its string so the format stays readable and open.
    /// </summary>
    private sealed record Dto(
        string Id, string Procedure, string Prefix, string Kind, string Status,
        DateTimeOffset StartedAt, DateTimeOffset? FinishedAt, string? Reason, string? Error)
    {
        public static Dto From(RunRecord r) => new(
            r.Id, r.Procedure, r.Prefix, r.Kind.ToString(), r.Status.ToString(),
            r.StartedAt, r.FinishedAt, r.Reason?.Value, r.Error);

        public RunRecord ToRecord() => new(
            Id, Procedure, Prefix,
            Enum.TryParse<RunKind>(Kind, out var k) ? k : RunKind.Apply,
            // An unrecognised status is treated as LIVE, deliberately: the safe error is to protect a
            // record we cannot classify, not to let it be deleted.
            Enum.TryParse<RunStatus>(Status, out var s) ? s : RunStatus.Paused,
            StartedAt, FinishedAt,
            Reason is null ? null : new PauseReason(Reason), Error);
    }
}
