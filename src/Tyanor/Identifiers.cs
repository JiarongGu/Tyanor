namespace Tyanor;

/// <summary>
/// The names a deployment is addressed by, checked once at the edge.
///
/// <para><b>Why this exists, concretely.</b> A prefix and a unit name are not labels — they become real
/// things: <c>Path.Combine(root, prefix, unit)</c> in a machine deployment, a CloudFormation stack name, a
/// component of a bucket name. Unvalidated, a prefix of <c>"../../etc"</c> makes a provider write outside
/// its own root, and makes its TEARDOWN — a recursive delete — do the same. That is not a hypothetical
/// misuse: the prefix is documented as operator-chosen, and an operator typing a path fragment into a name
/// field is an ordinary mistake with an extraordinary result.</para>
///
/// <para><b>What it does NOT do.</b> It does not encode any provider's naming rules. CloudFormation wants a
/// stack name starting with a letter; S3 wants lowercase and no dots; a filesystem does not care. Those are
/// each a provider's business to enforce and refuse, because Core naming a vendor's constraint is the leak
/// <c>docs/DECISIONS.md</c> D4 is about. What is checked here is only what is true everywhere: that a name
/// is a name, and cannot be a path.</para>
/// </summary>
internal static class Identifiers
{
    // Most filesystems cap a single path COMPONENT at 255 bytes, and every one of these names becomes one.
    // A neutral limit from the one constraint that applies to every target; providers impose their own
    // stricter ones (a stack name, a bucket label) and say so in their own words.
    private const int MaxLength = 255;

    /// <summary>
    /// Return <paramref name="name"/> if it is usable as a deployment name, and throw if it is not.
    /// </summary>
    /// <param name="name">The candidate.</param>
    /// <param name="what">What it is, for the message — <c>"prefix"</c>, <c>"unit name"</c>.</param>
    /// <exception cref="ArgumentException">
    /// It is empty, too long, contains something other than letters, digits, <c>-</c>, <c>_</c> or <c>.</c>,
    /// or is a dotted form that means something to a filesystem rather than naming anything.
    /// </exception>
    public static string Require(string? name, string what)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException($"A {what} is required and cannot be blank.", what);

        if (name.Length > MaxLength)
            throw new ArgumentException(
                $"The {what} '{Shorten(name)}' is {name.Length} characters; {MaxLength} is the most a path " +
                "component can be on most filesystems, and every one of these becomes one.", what);

        foreach (var c in name)
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.'))
                throw new ArgumentException(
                    $"The {what} '{name}' contains '{Describe(c)}'. Use letters, digits, '-', '_' or '.' — " +
                    "these names become directories and provider resource names, so anything that could be " +
                    "read as a path is refused rather than escaped.", what);

        // Rejecting the CHARACTERS of a path separator above already prevents traversal; this refuses the
        // intent as well, so `..` fails with a message about what it is rather than resolving to a directory
        // nobody named.
        //
        // BEFORE the leading-dot rule, and that order is the whole of why this message is reachable. The
        // other way round, the one input this sentence was written for — `..` itself — matched "starts with
        // a dot" and was told about hidden files and provider bookkeeping, while the parent-directory
        // wording only ever appeared for names like `v1..2`, where it is not true.
        if (name.Contains(".."))
            throw new ArgumentException(
                $"The {what} '{name}' contains '..', which names a parent directory rather than a deployment.",
                what);

        // A leading dot is refused for a reason beyond tidiness: the local provider keeps its own bookkeeping
        // in `.tyanor` beside the units, so a unit allowed to be named `.tyanor` would deploy on top of it.
        if (name[0] == '.')
            throw new ArgumentException(
                $"The {what} '{name}' starts with a dot. That is hidden on some systems and collides with " +
                "the bookkeeping a provider keeps beside your units.", what);

        return name;
    }

    private static string Describe(char c) =>
        char.IsControl(c) ? $"a control character (U+{(int)c:X4})" : c.ToString();

    private static string Shorten(string name) => name.Length <= 32 ? name : name[..29] + "…";
}
