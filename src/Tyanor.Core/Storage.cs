namespace Tyanor;

/// <summary>
/// Where state or run history lives, as ONE string: <c>"{kind}:{target}"</c>.
///
/// <para>Terraform's word for this is a backend, and this is the same idea: the KIND says what kind of store
/// it is and the TARGET says which one — a file path, a database connection string, a bucket and key. Both
/// halves in one value so an application can read it from configuration instead of branching in code.</para>
///
/// <code>
/// json:/var/lib/myapp/state.json
/// json:C:\ProgramData\myapp\state.json
/// sqlite:/var/lib/myapp/tyanor.db
/// postgres:Host=db;Database=ops;Username=tyanor
/// s3://my-bucket/tyanor/state.json
/// </code>
///
/// <para><b>The kind is required.</b> A bare path is not accepted, even though it would be convenient,
/// because the convenient version has to guess — and a typo like <c>"sqlite/state.db"</c> would be read as a
/// file path called <c>sqlite/state.db</c> and silently write state somewhere nobody meant. A deployment
/// tool asking rather than guessing is worth one extra word.</para>
/// </summary>
/// <param name="Kind">Which sort of store — <c>"json"</c>, <c>"sqlite"</c>, <c>"postgres"</c>, <c>"s3"</c>.</param>
/// <param name="Target">Everything after the kind, interpreted by that backend and nobody else.</param>
public sealed record StorageConnection(string Kind, string Target)
{
    /// <summary>Read a descriptor.</summary>
    /// <param name="descriptor">Of the form <c>"{kind}:{target}"</c>.</param>
    /// <exception cref="ArgumentException">No kind, no target, or a kind that is not a kind.</exception>
    /// <remarks>
    /// A kind must be at least two characters, which is what stops <c>"C:\data\state.json"</c> being read as
    /// the kind <c>C</c> — the same rule URI parsers use to tell a scheme from a Windows drive letter, and
    /// worth having because a Windows path is the most likely thing anyone types here.
    /// </remarks>
    public static StorageConnection Parse(string descriptor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptor);

        var colon = descriptor.IndexOf(':');
        if (colon <= 0)
            throw new ArgumentException(
                $"'{descriptor}' names no storage kind. Write it as \"{{kind}}:{{target}}\" — if that is a " +
                $"file path you want, \"json:{descriptor}\".", nameof(descriptor));

        var kind = descriptor[..colon];
        if (kind.Length < 2 || !kind.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '+' or '.'))
            throw new ArgumentException(
                $"'{kind}' is not a storage kind. A bare path is not accepted — write \"json:{descriptor}\" " +
                "if that is what you meant, so nothing has to be guessed.", nameof(descriptor));

        // A URL-shaped target keeps its own shape minus the separator: "s3://bucket/key" targets
        // "bucket/key", which is what a backend wants rather than what a URI parser would leave behind.
        var target = descriptor[(colon + 1)..];
        if (target.StartsWith("//", StringComparison.Ordinal)) target = target[2..];

        if (string.IsNullOrWhiteSpace(target))
            throw new ArgumentException(
                $"'{descriptor}' names the kind '{kind}' but no location.", nameof(descriptor));

        return new StorageConnection(kind, target);
    }

    /// <summary>The descriptor this was read from, near enough to round-trip.</summary>
    public override string ToString() => $"{Kind}:{Target}";
}

/// <summary>
/// Something that can open a store of one KIND — the seam a SQLite, Postgres or S3 backend implements.
///
/// <para><b>Register it; nothing is discovered.</b> Same rule as a provider (<c>docs/DECISIONS.md</c> D6): a
/// deployment tool holds credentials, so it does not load code it merely found. And the same path as D19 —
/// write the backend your application needs where you need it, hold it to
/// <c>RunHistoryContract</c> and <c>StateStoreContract</c>, and upstream it if it generalizes.</para>
/// </summary>
public interface IStorageBackend
{
    /// <summary>The kind this opens — <c>"json"</c>, <c>"sqlite"</c>. Matched case-insensitively.</summary>
    string Kind { get; }

    /// <summary>Open the deployment state — what Tyanor owns.</summary>
    /// <param name="connection">The parsed descriptor. Its <see cref="StorageConnection.Target"/> is yours to
    /// interpret.</param>
    IStateStore OpenState(StorageConnection connection);

    /// <summary>Open the run log — what was attempted.</summary>
    /// <param name="connection">The parsed descriptor.</param>
    /// <remarks>
    /// A backend that genuinely cannot hold one of the two should throw <see cref="NotSupportedException"/>
    /// with a sentence saying so, rather than returning something that quietly loses writes.
    /// </remarks>
    IRunHistory OpenHistory(StorageConnection connection);
}

/// <summary>
/// The storage backends an application offers, keyed by kind — the same shape as
/// <see cref="DeploymentTargets"/>, for the same reason.
/// </summary>
public sealed class StorageBackends
{
    private readonly Registry<IStorageBackend> _backends = new(b => b.Kind, "storage backend", "Kind");

    /// <summary>Build a registry.</summary>
    /// <param name="backends">The backends. Kinds are compared case-insensitively and must be unique.</param>
    /// <exception cref="ArgumentException">Two backends claim one kind, or one has no kind.</exception>
    public StorageBackends(IEnumerable<IStorageBackend> backends) => _backends.AddAll(backends, nameof(backends));

    /// <summary>Build a registry over the backends given.</summary>
    /// <param name="backends">The backends.</param>
    public StorageBackends(params IStorageBackend[] backends) : this((IEnumerable<IStorageBackend>)backends) { }

    /// <summary>The kinds available, for an error message or a picker.</summary>
    public IReadOnlyCollection<string> Kinds => _backends.Keys;

    /// <summary>Open deployment state from a descriptor.</summary>
    /// <param name="descriptor">Of the form <c>"{kind}:{target}"</c>.</param>
    public IStateStore State(string descriptor) => Backend(descriptor, out var c).OpenState(c);

    /// <summary>Open the run log from a descriptor.</summary>
    /// <param name="descriptor">Of the form <c>"{kind}:{target}"</c>.</param>
    public IRunHistory History(string descriptor) => Backend(descriptor, out var c).OpenHistory(c);

    /// <exception cref="ArgumentException">
    /// No backend is registered for that kind. The message names what IS registered and says how to add one,
    /// because "unknown kind sqlite" with no further help is a support conversation waiting to happen.
    /// </exception>
    private IStorageBackend Backend(string descriptor, out StorageConnection connection)
    {
        connection = StorageConnection.Parse(descriptor);

        return _backends.TryGet(connection.Kind)
            ?? throw new ArgumentException(
                $"No storage backend is registered for '{connection.Kind}'. Registered: " +
                $"{_backends.Describe()}. Reference the package that provides it, or implement " +
                "IStorageBackend yourself and register it in your composition root.",
                nameof(descriptor));
    }
}
