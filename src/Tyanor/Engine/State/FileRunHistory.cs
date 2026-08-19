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
public sealed class FileRunHistory : IRunHistory
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,                                        // a human may need to read this
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly JsonFile<Dto> _file;

    /// <summary>Keep the run log in a JSON file.</summary>
    /// <param name="path">Where the file lives. Created, with its directory, on first write.</param>
    public FileRunHistory(string path)
    {
        Path = path;
        // A truncated or hand-mangled file must not make the tool unusable — but it must also not be
        // silently overwritten, because it may be the only record of something still running.
        _file = new JsonFile<Dto>(path, Json, where =>
            $"The run history at '{where}' is not readable JSON. Move it aside to start a fresh history.");
    }

    /// <summary>Where this history is stored.</summary>
    public string Path { get; }

    /// <inheritdoc/>
    public Task UpsertAsync(RunRecord record, CancellationToken ct = default) =>
        // Last writer wins, per RUN, not per file: the read-modify-write re-reads what is on disk, so two
        // processes recording DIFFERENT runs do not lose each other's.
        _file.MutateAsync(all =>
        {
            var i = all.FindIndex(r => r.Id == record.Id);
            if (i >= 0) all[i] = Dto.From(record); else all.Add(Dto.From(record));
            return true;
        }, ct);

    /// <inheritdoc/>
    public async Task<RunRecord?> LiveAsync(string procedure, string prefix, CancellationToken ct = default) =>
        // Newest first: if more than one is somehow live, the latest attempt is the one to resume.
        (await Newest(ct)).FirstOrDefault(r => r.Procedure == procedure && r.Prefix == prefix && r.IsLive);

    /// <inheritdoc/>
    public async Task<IReadOnlyList<RunRecord>> RecentAsync(int limit = 50, CancellationToken ct = default) =>
        (await Newest(ct)).Take(limit).ToList();

    /// <summary>
    /// Delete a finished run. REFUSES a live one: it is the operator's only handle on work that may still
    /// be converging in the provider, and deleting it strands that work with nothing left to say it is
    /// happening. See <c>.claude/rules/reconcile-dont-mirror.md</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The run is running or paused.</exception>
    public Task DeleteAsync(string id, CancellationToken ct = default) =>
        _file.MutateAsync(all =>
        {
            var found = all.FirstOrDefault(d => d.Id == id);
            if (found is null) return false;                         // already gone — deleting is idempotent

            // The framework's refusal, not ours, so every store says the same sentence about it.
            found.ToRecord().RefuseDeleteWhileLive();
            all.Remove(found);
            return true;
        }, ct);

    private async Task<IEnumerable<RunRecord>> Newest(CancellationToken ct) =>
        (await _file.ReadAsync(ct)).AsEnumerable().Reverse().Select(d => d.ToRecord());

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
            Read(Kind, RunKind.Apply),
            // An unrecognised status is treated as LIVE, deliberately: the safe error is to protect a
            // record we cannot classify, not to let it be deleted.
            Read(Status, RunStatus.Paused),
            StartedAt, FinishedAt,
            Reason is null ? null : new PauseReason(Reason), Error);

        /// <summary>
        /// Parse an enum written as its NAME, falling back when it is anything else.
        /// </summary>
        /// <remarks>
        /// <see cref="Enum.TryParse{T}(string, out T)"/> alone is not enough, and the gap defeats the
        /// guarantee above: it happily accepts a NUMBER, so a status of <c>"9"</c> parses to an undefined
        /// <see cref="RunStatus"/> that is neither Running nor Paused — and therefore reads as not live, and
        /// becomes deletable. A hand-edited file is exactly the case the fallback exists for, so it has to
        /// hold for a hand-edited file.
        /// </remarks>
        private static T Read<T>(string? stored, T fallback) where T : struct, Enum =>
            Enum.TryParse<T>(stored, out var value) && Enum.IsDefined(value) ? value : fallback;
    }
}
