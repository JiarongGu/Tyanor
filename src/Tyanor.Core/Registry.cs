namespace Tyanor;

/// <summary>
/// The things an application offers of one sort, keyed by a name a caller asks for — deployment targets by
/// <see cref="IDeploymentTarget.Id"/>, storage backends by <see cref="IStorageBackend.Kind"/>.
///
/// <para><b>Written twice before it was written once.</b> <see cref="DeploymentTargets"/> and
/// <see cref="StorageBackends"/> arrived a fortnight apart and reached the same shape independently: a
/// case-insensitive dictionary, a blank key refused, a duplicate key refused rather than resolved by
/// registration order, and a lookup failure that names what IS registered. That is the same signal
/// <c>UnitKindDriver</c> was extracted on (<c>docs/DECISIONS.md</c> D15) — two independent arrivals at one
/// shape means the shape belongs to the framework, and a third registry would otherwise get one of the four
/// subtly wrong.</para>
///
/// <para><b>Internal, and the two registries stay separate public types.</b> A caller asks for a TARGET or a
/// BACKEND, never for "a registered thing"; collapsing them into one generic public type would trade two
/// readable APIs for one that reads like plumbing. What is shared is the behaviour, which is where the bugs
/// were.</para>
/// </summary>
/// <typeparam name="T">What is registered.</typeparam>
/// <param name="key">How to read an entry's key — its id, its kind.</param>
/// <param name="what">What one of these IS, for a message: <c>"target"</c>, <c>"storage backend"</c>.</param>
/// <param name="keyName">What its key is called, for a message: <c>"Id"</c>, <c>"Kind"</c>.</param>
internal sealed class Registry<T>(Func<T, string> key, string what, string keyName) where T : class
{
    private readonly Dictionary<string, T> _entries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Add everything, refusing a blank or duplicate key.
    /// </summary>
    /// <param name="entries">The entries. Keys are compared case-insensitively.</param>
    /// <param name="parameter">The caller's parameter name, so the exception points at their code.</param>
    /// <exception cref="ArgumentException">Two entries claim one key, or one has none.</exception>
    /// <remarks>
    /// A duplicate is refused rather than resolved by order. Last-one-wins is a mistake somebody made in a
    /// composition root, and the undiscoverable version of it — for targets, a deployment that goes to the
    /// wrong provider and a plan computed against the wrong provider, which therefore agrees.
    /// </remarks>
    public void AddAll(IEnumerable<T> entries, string parameter)
    {
        ArgumentNullException.ThrowIfNull(entries);

        foreach (var entry in entries)
        {
            var id = key(entry);
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException(
                    $"{entry.GetType().Name} has no {keyName}, so no caller could ask for it by name.", parameter);

            if (!_entries.TryAdd(id, entry))
                throw new ArgumentException(
                    $"Two {what}s both claim the {keyName.ToLowerInvariant()} '{id}'. " +
                    $"{keyName}s have to be unique — that is what makes one selectable.", parameter);
        }
    }

    /// <summary>How many are registered.</summary>
    public int Count => _entries.Count;

    /// <summary>The keys, in order, for an error message or a picker in a UI.</summary>
    public IReadOnlyCollection<string> Keys => _entries.Keys.Order().ToList();

    /// <summary>The entry with this key, or null.</summary>
    /// <param name="id">The key.</param>
    public T? TryGet(string? id) => id is not null && _entries.TryGetValue(id, out var entry) ? entry : null;

    /// <summary>The only entry, or null when there is not exactly one.</summary>
    public T? Only => _entries.Count == 1 ? _entries.Values.First() : null;

    /// <summary>What IS registered, for a message. Never an empty string.</summary>
    public string Describe() => _entries.Count == 0 ? "none" : string.Join(", ", Keys);
}
