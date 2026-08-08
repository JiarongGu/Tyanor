namespace Tyanor.Providers.Local;

/// <summary>
/// Routes each unit to the kind of thing it is.
///
/// <para><b>This dispatch is the shape a cloud provider never has, and the reason it exists is worth
/// naming.</b> Every CloudFormation unit is a stack, so there the unit's name is the whole of its
/// configuration and one driver serves all of them. A machine deployment is heterogeneous — a directory
/// here, a process there — so a unit has to say what it is, per unit, which is what
/// <see cref="DeploymentRequest.Option(string, string)"/> was added for. See <c>docs/DECISIONS.md</c> D13.</para>
///
/// <para>There is no orchestration here and there must never be. Ordering, reconcile, retry and the
/// pause/fail decision all live in the engine; a provider that starts branching on run state has begun
/// writing a second engine inside itself.</para>
/// </summary>
/// <param name="root">The machine's deployment root.</param>
internal sealed class LocalUnitDriver(string root) : IUnitDriver
{
    private readonly DirectoryUnit _directory = new(root);
    private readonly ProcessUnit _process = new(root);

    /// <inheritdoc/>
    public Task<UnitPhase> PhaseAsync(ProcedureUnit unit, DeploymentRequest request, CancellationToken ct)
        => Kind(unit, request).PhaseAsync(unit, request, ct);

    /// <inheritdoc/>
    public Task CreateAsync(ProcedureUnit unit, DeploymentRequest request, CancellationToken ct)
        => Kind(unit, request).CreateAsync(unit, request, ct);

    /// <inheritdoc/>
    public Task<bool> UpdateAsync(ProcedureUnit unit, DeploymentRequest request, CancellationToken ct)
        => Kind(unit, request).UpdateAsync(unit, request, ct);

    /// <inheritdoc/>
    public Task RemoveAsync(ProcedureUnit unit, DeploymentRequest request, CancellationToken ct)
        => Kind(unit, request).RemoveAsync(unit, request, ct);

    /// <inheritdoc/>
    public Task AwaitSettledAsync(
        ProcedureUnit unit, DeploymentRequest request, Action<ProgressReport> report, CancellationToken ct)
        => Kind(unit, request).AwaitSettledAsync(unit, request, report, ct);

    /// <inheritdoc/>
    public Task<IReadOnlyList<ResourceState>> RefreshAsync(
        ProcedureUnit unit, DeploymentRequest request, CancellationToken ct)
        => Kind(unit, request).RefreshAsync(unit, request, ct);

    // No default. A unit that does not say what it is has been misconfigured, and inventing an answer
    // would deploy something the operator never described — the one failure a deployment tool must not
    // make quietly.
    private IUnitDriver Kind(ProcedureUnit unit, DeploymentRequest request) =>
        request.Option(unit.Name, LocalOptions.Kind) switch
        {
            LocalOptions.DirectoryKind => _directory,
            LocalOptions.ProcessKind => _process,
            null => throw LocalDeploymentException.Misconfigured(unit.Name,
                $"Unit '{unit.Name}' declares no '{LocalOptions.Kind}'. Set it to " +
                $"'{LocalOptions.DirectoryKind}' or '{LocalOptions.ProcessKind}'."),
            var other => throw LocalDeploymentException.Misconfigured(unit.Name,
                $"Unit '{unit.Name}' declares kind '{other}', which this provider does not have."),
        };
}
