using Amazon.CloudFront;
using Amazon.CloudFormation;
using Amazon.S3;

namespace Tyanor.Providers.Aws;

/// <summary>
/// Routes each unit to the kind of AWS thing it is.
///
/// <para>Two kinds, and the pair is the whole of what the first consumer's site needs: CloudFormation stacks
/// for the infrastructure, and a bucket of files for the website — which is not a CloudFormation asset and
/// therefore cannot be one of the stacks.</para>
///
/// <para>There is no orchestration here. Ordering, reconcile, retry and the pause/fail decision are the
/// engine's, and the temptation to re-add them is real: the code this was ported from had all four inside a
/// single method, and pulling them out is most of why this file is short.</para>
/// </summary>
internal sealed class AwsUnitDriver : IUnitDriver
{
    private readonly StackUnit _stack;
    private readonly ContentUnit _content;

    public AwsUnitDriver(
        IAmazonCloudFormation cfn, IAmazonS3 s3, IAmazonCloudFront cloudFront, AwsAccount account, string region)
    {
        _stack = new StackUnit(cfn, s3, account, region);
        _content = new ContentUnit(s3, cloudFront, _stack);
    }

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

    // No default, and in particular not "stack" as a default. Guessing would deploy CloudFormation against a
    // template the operator never named — the one failure a deployment tool must not make quietly.
    private IUnitDriver Kind(ProcedureUnit unit, DeploymentRequest request) =>
        request.Option(unit.Name, AwsOptions.Kind) switch
        {
            AwsOptions.StackKind => _stack,
            AwsOptions.ContentKind => _content,
            null => throw new AwsDeploymentException(
                $"Unit '{unit.Name}' declares no '{AwsOptions.Kind}'. Set it to " +
                $"'{AwsOptions.StackKind}' or '{AwsOptions.ContentKind}'."),
            var other => throw new AwsDeploymentException(
                $"Unit '{unit.Name}' declares kind '{other}', which this provider does not have."),
        };
}
