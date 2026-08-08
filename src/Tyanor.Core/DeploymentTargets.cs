namespace Tyanor;

/// <summary>
/// The targets an application can deploy to, selectable by <see cref="IDeploymentTarget.Id"/>.
///
/// <para><b>This exists because one target was one too few.</b> With a single provider, "the target" was
/// unambiguous and resolving it by type worked. With two — and the whole point of a provider seam is that
/// there will be more, including ones written outside this repository — resolving by type silently returns
/// whichever was registered last, and there is no way to ask for a particular one. That is a wrong
/// deployment produced by a wiring detail, which is exactly the class of failure a plan cannot catch,
/// because the plan would be computed against the wrong target too.</para>
///
/// <para>Immutable, and built in the composition root. Nothing is discovered from disk: a deployment tool
/// holds credentials and mutates infrastructure, so loading code it merely FOUND is a security question
/// nobody asked for (<c>docs/DECISIONS.md</c> D6). Writing your own provider is entirely supported — it is
/// registered here, in one line, like the built-in ones.</para>
/// </summary>
public sealed class DeploymentTargets
{
    private readonly Dictionary<string, IDeploymentTarget> _targets;

    /// <summary>Build a registry over the targets this application offers.</summary>
    /// <param name="targets">The targets. Ids are compared case-insensitively.</param>
    /// <exception cref="ArgumentException">Two targets share an id, or one has none.</exception>
    public DeploymentTargets(IEnumerable<IDeploymentTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        _targets = new Dictionary<string, IDeploymentTarget>(StringComparer.OrdinalIgnoreCase);

        foreach (var target in targets)
        {
            if (string.IsNullOrWhiteSpace(target.Id))
                throw new ArgumentException(
                    $"{target.GetType().Name} has no Id. A target's id is how a caller asks for it by name.",
                    nameof(targets));

            // Refused rather than resolved by order. Two providers claiming "aws" is a mistake somebody made
            // in a composition root, and the last-one-wins version of it is undiscoverable.
            if (!_targets.TryAdd(target.Id, target))
                throw new ArgumentException(
                    $"Two targets both claim the id '{target.Id}'. Ids have to be unique — that is what " +
                    "makes one selectable.", nameof(targets));
        }
    }

    /// <summary>Build a registry over the targets given.</summary>
    /// <param name="targets">The targets.</param>
    public DeploymentTargets(params IDeploymentTarget[] targets) : this((IEnumerable<IDeploymentTarget>)targets) { }

    /// <summary>The ids registered, in order, for an error message or a picker in a UI.</summary>
    public IReadOnlyCollection<string> Ids => _targets.Keys.Order().ToList();

    /// <summary>
    /// The target with this id.
    /// </summary>
    /// <param name="id">The provider id — <c>"aws"</c>, <c>"local"</c>.</param>
    /// <exception cref="ArgumentException">No target has that id.</exception>
    public IDeploymentTarget Get(string id) =>
        TryGet(id) ?? throw new ArgumentException(
            $"No deployment target with id '{id}'. Registered: {Describe()}.", nameof(id));

    /// <summary>The target with this id, or null.</summary>
    /// <param name="id">The provider id.</param>
    public IDeploymentTarget? TryGet(string id) =>
        id is not null && _targets.TryGetValue(id, out var target) ? target : null;

    /// <summary>
    /// The only target, when there is exactly one.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// There are none, or more than one. Deliberately not "the first" — an application with two providers
    /// that asks for "the" target has a question only it can answer, and answering it by registration order
    /// would deploy to whichever provider happened to be wired up second.
    /// </exception>
    public IDeploymentTarget Single() => _targets.Count switch
    {
        1 => _targets.Values.First(),
        0 => throw new InvalidOperationException(
            "No deployment targets are registered. Add one in your composition root."),
        _ => throw new InvalidOperationException(
            $"{_targets.Count} deployment targets are registered ({Describe()}), so there is no single one. " +
            "Ask for the one you mean by id."),
    };

    private string Describe() => _targets.Count == 0 ? "none" : string.Join(", ", Ids);
}
