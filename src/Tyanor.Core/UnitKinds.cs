namespace Tyanor;

/// <summary>
/// An <see cref="IUnitDriver"/> assembled from several KINDS of unit, chosen per unit by an option.
///
/// <para><b>This exists because it was written twice, identically.</b> The first two providers each ended up
/// with the same file: a switch on a <c>kind</c> option and six one-line forwards to the driver it picked.
/// Two independent arrivals at one shape is the signal that the shape belongs to the framework — and a third
/// provider written outside this repository would otherwise write it a third time, differently, and get the
/// error message for "you did not say what this unit is" subtly wrong.</para>
///
/// <para>Deriving from this is optional. A provider whose units are all the same kind of thing — every
/// CloudFormation unit is a stack — should implement <see cref="IUnitDriver"/> directly and ignore this
/// entirely.</para>
///
/// <example>
/// <code>
/// public sealed class MyDriver : UnitKindDriver
/// {
///     public MyDriver(string root) : base("kind")
///     {
///         Register("directory", new DirectoryUnit(root));
///         Register("process", new ProcessUnit(root));
///     }
/// }
/// </code>
/// </example>
/// </summary>
public abstract class UnitKindDriver : IUnitDriver
{
    private readonly Dictionary<string, IUnitDriver> _kinds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Build a driver whose units declare their kind under <paramref name="option"/>.</summary>
    /// <param name="option">The option name a unit sets to say what it is. <c>"kind"</c> by convention.</param>
    protected UnitKindDriver(string option = "kind")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(option);
        Option = option;
    }

    /// <summary>The option a unit sets to declare its kind.</summary>
    public string Option { get; }

    /// <summary>The kinds this provider offers, for an error message worth reading.</summary>
    public IReadOnlyCollection<string> Kinds => _kinds.Keys;

    /// <summary>Add a kind. Call from the constructor; a driver that changes its kinds later is not one.</summary>
    /// <param name="kind">What a unit writes in the option — lowercase by convention, matched case-insensitively.</param>
    /// <param name="driver">The driver for units of that kind.</param>
    /// <exception cref="ArgumentException">That kind is already registered.</exception>
    protected void Register(string kind, IUnitDriver driver)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentNullException.ThrowIfNull(driver);
        if (!_kinds.TryAdd(kind, driver))
            throw new ArgumentException($"Kind '{kind}' is already registered.", nameof(kind));
    }

    /// <summary>
    /// The driver for one unit.
    /// </summary>
    /// <param name="unit">The unit.</param>
    /// <param name="request">Where its kind is declared.</param>
    /// <exception cref="UnitKindException">The unit declares no kind, or one this provider does not have.</exception>
    /// <remarks>
    /// There is deliberately NO default kind, not even when a provider offers only one. Guessing would deploy
    /// something the operator never described, which is the one failure a deployment tool must not make
    /// quietly — and the moment a second kind is added, every unit that relied on the default changes meaning
    /// without changing text.
    /// </remarks>
    public IUnitDriver For(ProcedureUnit unit, DeploymentRequest request)
    {
        var kind = request.Option(unit.Name, Option);
        if (kind is null)
            throw new UnitKindException(
                $"Unit '{unit.Name}' declares no '{Option}'. Set it to one of: {Available()}.");

        return _kinds.TryGetValue(kind, out var driver)
            ? driver
            : throw new UnitKindException(
                $"Unit '{unit.Name}' declares {Option} '{kind}', which this provider does not have. " +
                $"It offers: {Available()}.");
    }

    /// <inheritdoc/>
    public Task<UnitPhase> PhaseAsync(ProcedureUnit unit, DeploymentRequest request, CancellationToken ct)
        => For(unit, request).PhaseAsync(unit, request, ct);

    /// <inheritdoc/>
    public Task CreateAsync(ProcedureUnit unit, DeploymentRequest request, CancellationToken ct)
        => For(unit, request).CreateAsync(unit, request, ct);

    /// <inheritdoc/>
    public Task<bool> UpdateAsync(ProcedureUnit unit, DeploymentRequest request, CancellationToken ct)
        => For(unit, request).UpdateAsync(unit, request, ct);

    /// <inheritdoc/>
    public Task RemoveAsync(ProcedureUnit unit, DeploymentRequest request, CancellationToken ct)
        => For(unit, request).RemoveAsync(unit, request, ct);

    /// <inheritdoc/>
    public Task AwaitSettledAsync(
        ProcedureUnit unit, DeploymentRequest request, Action<ProgressReport> report, CancellationToken ct)
        => For(unit, request).AwaitSettledAsync(unit, request, report, ct);

    /// <inheritdoc/>
    public Task<IReadOnlyList<ResourceState>> RefreshAsync(
        ProcedureUnit unit, DeploymentRequest request, CancellationToken ct)
        => For(unit, request).RefreshAsync(unit, request, ct);

    private string Available() =>
        _kinds.Count == 0 ? "none — this provider registered no kinds" : string.Join(", ", _kinds.Keys.Order());
}

/// <summary>
/// A unit does not say what it is, or says something the provider does not offer. Always terminal: it is the
/// definition that is wrong, and retrying re-reads the same definition.
/// </summary>
/// <param name="message">Plain language, naming the kinds that ARE available.</param>
public sealed class UnitKindException(string message) : DefinitionException(message);
