using System.Security.Cryptography;
using System.Text;

namespace Tyanor.Providers.Local;

/// <summary>
/// Content hashes — the provider's answer to "is what is deployed still what I deployed?".
///
/// <para>These are a fact about OUR inputs and about files on THIS machine, which is what makes them a
/// legitimate local record rather than a mirror of somebody else's database: nothing happening elsewhere
/// can make one stale. See <c>.claude/rules/reconcile-dont-mirror.md</c>, "Where a cache IS legitimate".</para>
/// </summary>
internal static class Fingerprints
{
    // A separator that cannot occur in a path, a command line or a hash, so no combination of real values
    // can collide with another by an accident of concatenation.
    private const char Separator = (char)0;

    // Distinct from an empty string on purpose: "no arguments" and "empty arguments" are different
    // requests, and conflating them would skip a restart the operator asked for.
    private const string Absent = "<none>";

    /// <summary>
    /// Hash a tree: every file's relative path, length and content, in sorted order.
    ///
    /// <para>Paths are normalized to <c>/</c> and lower-cased so the same tree hashes the same on Windows
    /// and Linux — a fingerprint that changed when the operator switched machines would report drift that
    /// is not there, and an operator who sees phantom drift stops reading the number.</para>
    /// </summary>
    /// <param name="directory">The tree. A directory that does not exist hashes to null.</param>
    public static string? OfDirectory(string directory)
    {
        if (!Directory.Exists(directory)) return null;

        var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Select(f => (Relative: Relative(directory, f), Full: f))
            .OrderBy(f => f.Relative, StringComparer.Ordinal)
            .ToList();

        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (relative, full) in files)
        {
            digest.AppendData(Encoding.UTF8.GetBytes(relative + Separator + new FileInfo(full).Length + Separator));
            using var stream = File.OpenRead(full);
            digest.AppendData(SHA256.HashData(stream));
        }
        return Short(digest.GetHashAndReset());
    }

    /// <summary>
    /// Hash a set of settings into one fingerprint — how a process unit says "the thing I am running is
    /// the thing you asked for", covering the command, its arguments AND the content it serves.
    /// </summary>
    /// <param name="parts">The settings, in a fixed order. A null part is encoded distinctly from an
    /// empty one.</param>
    public static string Of(params string?[] parts) =>
        Short(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(Separator, parts.Select(p => p ?? Absent)))));

    // Truncated to 16 hex characters: this identifies a build to an operator reading a plan, and it is not
    // defending against anyone constructing a collision on purpose.
    private static string Short(byte[] hash) => Convert.ToHexString(hash)[..16].ToLowerInvariant();

    private static string Relative(string root, string file) =>
        Path.GetRelativePath(root, file).Replace('\\', '/').ToLowerInvariant();
}
