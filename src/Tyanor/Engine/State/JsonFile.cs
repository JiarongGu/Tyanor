using System.Collections.Concurrent;
using System.Text.Json;

namespace Tyanor.Engine.State;

/// <summary>
/// A list of records in one JSON file, read and written atomically — what
/// <see cref="FileStateStore"/> and <see cref="FileRunHistory"/> are both made of.
///
/// <para><b>Write-then-replace is the whole point.</b> The failure these files exist to survive is the
/// process dying, and dying midway through rewriting the file that records what was happening would be a
/// poor way to learn that lesson. A crash leaves either the old file or the new one, never half of either.
/// Both stores had this written out separately, identically, which is one edit away from only one of them
/// being atomic.</para>
/// </summary>
/// <typeparam name="T">The record shape stored in the file.</typeparam>
/// <param name="path">Where the file lives. Created, with its directory, on first write.</param>
/// <param name="options">Serializer settings — each store's, because each has its own reasons.</param>
/// <param name="unreadable">
/// The message when the file is not readable JSON. Deliberately per-store: what is at stake differs, and so
/// does what the operator should do about it.
/// </param>
internal sealed class JsonFile<T>(string path, JsonSerializerOptions options, Func<string, string> unreadable)
{
    /// <summary>
    /// One gate per FILE, shared by every instance in this process — not one per instance.
    /// </summary>
    /// <remarks>
    /// <para>The lock used to belong to the object, which quietly assumed a consumer keeps exactly one store
    /// per path. Nothing says so, and the guide invites the opposite by writing <c>new FileStateStore(path)</c>
    /// at the point of use — so two instances doing a read-modify-write of the same file interleaved and one
    /// unit's state simply vanished. A test writing twenty-four units through twenty-four instances kept
    /// about half of them.</para>
    /// <para>This is NOT the cross-machine trade D11 and D12 accept. That one is stated, bounded and about
    /// two processes; this was a single process losing its own writes because of how somebody happened to
    /// construct an object.</para>
    /// <para>Held in a NON-generic type on purpose: a static inside <c>JsonFile&lt;T&gt;</c> is per closed
    /// type, so two stores of different record shapes on one path would each get their own gate and be back
    /// where they started. The gate belongs to the FILE, so it cannot live anywhere that knows about T.</para>
    /// </remarks>
    private readonly SemaphoreSlim _gate = JsonFileGates.For(path);

    /// <summary>
    /// Read the file, or an empty list when it is not there yet.
    /// </summary>
    /// <param name="ct">Cancellation.</param>
    /// <exception cref="InvalidOperationException">
    /// It exists and is not readable JSON. Never silently replaced — see the note on each store for why the
    /// file is worth more than the convenience of starting over.
    /// </exception>
    /// <remarks>
    /// <b>Reads take the gate too.</b> They used not to, which meant a read racing a write in the SAME
    /// process threw <see cref="IOException"/> — the atomic replace needs the destination to itself, and
    /// <c>File.OpenRead</c> was not asking anyone's permission. An application polling state to show a
    /// deployment's progress while that deployment writes state per unit is the ordinary shape, not an
    /// exotic one, and on a provider whose classifier does not recognise an IOException the run would have
    /// failed outright.
    /// </remarks>
    public async Task<List<T>> ReadAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return await ReadUnlockedAsync(ct); }
        finally { _gate.Release(); }
    }

    /// <summary>The read itself, for callers that already hold the gate.</summary>
    private async Task<List<T>> ReadUnlockedAsync(CancellationToken ct)
    {
        if (!File.Exists(path)) return [];
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<List<T>>(stream, options, ct) ?? [];
        }
        catch (JsonException)
        {
            throw new InvalidOperationException(unreadable(path));
        }
    }

    /// <summary>
    /// Read, change, and write back under a lock — the read-modify-write every mutation here is.
    /// </summary>
    /// <param name="change">
    /// Mutates the list in place and returns whether anything actually changed. Returning false skips the
    /// write, so deleting something that was already gone does not rewrite the file.
    /// </param>
    /// <param name="ct">Cancellation.</param>
    /// <remarks>
    /// The gate is per FILE and therefore per PROCESS. Another process holding the same file is
    /// last-writer-wins, which is stated rather than hidden: <c>docs/DECISIONS.md</c> D11 and D20 say a
    /// backend that needs better than that is where the answer belongs.
    /// </remarks>
    public async Task MutateAsync(Func<List<T>, bool> change, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            // Re-read rather than trusting an in-memory copy: another process may own the same file, and a
            // local-first tool has no lock server to arbitrate. Unlocked, because this already holds the
            // gate and SemaphoreSlim is not re-entrant — taking it twice here would deadlock the store.
            var all = await ReadUnlockedAsync(ct);
            if (change(all)) await WriteAsync(all, ct);
        }
        finally { _gate.Release(); }
    }

    private async Task WriteAsync(List<T> all, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var temp = path + ".tmp";
        await using (var stream = File.Create(temp))
            await JsonSerializer.SerializeAsync(stream, all, options, ct);

        if (File.Exists(path)) File.Replace(temp, path, null);
        else File.Move(temp, path);
    }
}

/// <summary>
/// One lock per FILE, shared by every <see cref="JsonFile{T}"/> in this process whatever it holds.
/// </summary>
/// <remarks>
/// Non-generic deliberately. A static field inside a generic type exists once per CLOSED type, so putting
/// this in <c>JsonFile&lt;T&gt;</c> would give a state store and a run log pointed at one path a gate each —
/// which is exactly the interleaving it exists to prevent, reintroduced by a language rule rather than by a
/// design choice.
/// </remarks>
internal static class JsonFileGates
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    /// <summary>The gate for a path. Full-pathed first, so two spellings of one file are one lock.</summary>
    /// <param name="path">Where the file lives.</param>
    public static SemaphoreSlim For(string path) =>
        Gates.GetOrAdd(Path.GetFullPath(path), _ => new SemaphoreSlim(1, 1));
}
