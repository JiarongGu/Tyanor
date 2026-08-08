using Amazon.CloudFormation;
using Amazon.CloudFormation.Model;
using Amazon.S3;
using Amazon.S3.Model;
// Both SDKs define Tag and this file uses only CloudFormation's, which is the one stacks carry.
using Tag = Amazon.CloudFormation.Model.Tag;

namespace Tyanor.Providers.Aws;

/// <summary>
/// A unit that IS a CloudFormation stack — the provider with a real control plane, and the shape every
/// assumption in the engine was extracted from.
///
/// <para><b>Almost nothing here is orchestration, and that is the point.</b> The deployer this was ported
/// from had one 50-line method that read the stack status and then branched: attach if converging, wait out
/// a rollback then delete and recreate, delete and recreate if settled unusable, otherwise update. All four
/// branches are <see cref="Reconcile.Decide"/> now, so what is left below is the six calls CloudFormation
/// actually needs. Bounded retry, classification and the pause/fail decision left with it.</para>
///
/// <para>Stacks deploy as <c>{prefix}-{unit}</c>, which is what lets one account host several independent
/// deployments of the same procedure.</para>
/// </summary>
internal sealed class StackUnit(
    IAmazonCloudFormation cfn, IAmazonS3 s3, AwsAccount account, string region) : IUnitDriver
{
    // CloudFormation charges nothing for DescribeStacks but it is rate limited, and a deploy can run for
    // twenty minutes. Six seconds is what the ported deployer used against real stacks.
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(6);

    /// <inheritdoc/>
    public async Task<UnitPhase> PhaseAsync(ProcedureUnit unit, DeploymentRequest request, CancellationToken ct)
        => CloudFormationPhases.Of(await StatusAsync(Name(request, unit), ct));

    /// <summary>
    /// Stage the template and its assets, then issue the create. Does NOT wait — the engine waits, so
    /// attaching to an operation someone else started uses the identical wait.
    /// </summary>
    public async Task CreateAsync(ProcedureUnit unit, DeploymentRequest request, CancellationToken ct)
    {
        var templateUrl = await StageAsync(unit, request, ct);
        await cfn.CreateStackAsync(new CreateStackRequest
        {
            StackName = Name(request, unit),
            TemplateURL = templateUrl,
            Parameters = Parameters(unit, request),
            Capabilities = Capabilities(unit, request),
            Tags = Tags(request),
            // ROLLBACK rather than DELETE: on failure the stack record and its events survive, so the cause
            // is still readable — and AwaitSettledAsync reads it, which is the difference between telling an
            // operator "the API stack failed because the role name was taken" and telling them it failed.
            OnFailure = OnFailure.ROLLBACK,
        }, ct);
    }

    /// <summary>
    /// Apply the template to an existing stack. Returns false when CloudFormation says there is nothing to
    /// change, which is a SUCCESS and on a resume is the ordinary answer for every unit that already finished.
    /// </summary>
    public async Task<bool> UpdateAsync(ProcedureUnit unit, DeploymentRequest request, CancellationToken ct)
    {
        var templateUrl = await StageAsync(unit, request, ct);
        try
        {
            await cfn.UpdateStackAsync(new UpdateStackRequest
            {
                StackName = Name(request, unit),
                TemplateURL = templateUrl,
                Parameters = Parameters(unit, request),
                Capabilities = Capabilities(unit, request),
                Tags = Tags(request),
            }, ct);
            return true;
        }
        catch (AmazonCloudFormationException e) when (CloudFormationPhases.IsNoUpdatesNeeded(e.Message))
        {
            return false;
        }
    }

    /// <summary>Delete the stack and wait until it is gone — removal is the one operation the engine cannot
    /// meaningfully attach to halfway, so the driver owns the wait.</summary>
    public async Task RemoveAsync(ProcedureUnit unit, DeploymentRequest request, CancellationToken ct)
    {
        var name = Name(request, unit);
        if (await StatusAsync(name, ct) is null) return;        // already gone; teardown must be re-runnable

        await cfn.DeleteStackAsync(new DeleteStackRequest { StackName = name }, ct);

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(Poll, ct);

            var status = await StatusAsync(name, ct);
            // Null is success here: once a stack is fully deleted CloudFormation stops describing it at all,
            // so "it does not exist" is exactly the outcome being waited for.
            if (status is null || status == "DELETE_COMPLETE") return;
            if (status == "DELETE_FAILED")
                throw new AwsDeploymentException(
                    $"Could not remove {name} (DELETE_FAILED). {await FirstFailureAsync(name, ct)}");
        }
    }

    /// <summary>
    /// Poll until the stack settles, streaming each resource event as it happens, and throw if it settled
    /// badly — including into a rollback that left the stack usable, because a reverted update is still an
    /// update that did not ship.
    /// </summary>
    public async Task AwaitSettledAsync(
        ProcedureUnit unit, DeploymentRequest request, Action<ProgressReport> report, CancellationToken ct)
    {
        var name = Name(request, unit);
        var seen = new HashSet<string>();

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(Poll, ct);
            await StreamEventsAsync(name, unit, seen, report, ct);

            var status = await StatusAsync(name, ct);
            if (!CloudFormationPhases.Settled(status)) continue;

            if (CloudFormationPhases.SettledBadly(status))
                // Hard, via the classifier not recognising it: the template produced this, and issuing the
                // same template again produces it again. What has to change is the definition.
                throw new AwsDeploymentException(
                    $"{unit.Label} failed ({status}). {await FirstFailureAsync(name, ct)}");

            report(new ProgressReport(unit.Name, $"{unit.Label}: done.", -1, ProgressStatus.Success));
            return;
        }
    }

    /// <summary>
    /// What the stack holds, straight from CloudFormation — the one provider that can be asked directly,
    /// because it tracks stack membership itself.
    /// </summary>
    /// <remarks>
    /// The fingerprint is the resource's CloudFormation status, not a content hash, and the limit is worth
    /// stating rather than leaving to be discovered: <b>CloudFormation cannot cheaply tell you whether a
    /// resource was changed outside it.</b> That is what <c>DetectStackDrift</c> is for, and it is a paid
    /// asynchronous operation per stack — far too expensive to run on every plan. So drift reported here is
    /// drift CloudFormation knows about. A resource someone edited in the console reads as unchanged, and
    /// the honest place to find out is CloudFormation's own drift detection.
    /// </remarks>
    public async Task<IReadOnlyList<ResourceState>> RefreshAsync(
        ProcedureUnit unit, DeploymentRequest request, CancellationToken ct)
    {
        var name = Name(request, unit);
        if (await StatusAsync(name, ct) is null) return [];      // absent is a fact, not a failure

        var resources = (await cfn.DescribeStackResourcesAsync(
            new DescribeStackResourcesRequest { StackName = name }, ct)).StackResources;

        return resources
            .Where(r => r.PhysicalResourceId is not null)
            .Select(r => new ResourceState(r.PhysicalResourceId, r.ResourceType, r.ResourceStatus?.Value))
            .ToList();
    }

    /// <summary>The CloudFormation outputs of a settled stack — how a content unit finds the bucket a stack
    /// created without either of them naming the other in code.</summary>
    internal async Task<IReadOnlyDictionary<string, string>> OutputsAsync(string stackName, CancellationToken ct)
    {
        var stacks = (await cfn.DescribeStacksAsync(new DescribeStacksRequest { StackName = stackName }, ct)).Stacks;
        return stacks[0].Outputs?
            .Where(o => o.OutputKey is not null && o.OutputValue is not null)
            .ToDictionary(o => o.OutputKey, o => o.OutputValue)
            ?? new Dictionary<string, string>();
    }

    private static string Name(DeploymentRequest request, ProcedureUnit unit) => $"{request.Prefix}-{unit.Name}";

    /// <summary>
    /// The stack's status, or null when it does not exist.
    /// </summary>
    /// <remarks>
    /// Only a genuine "does not exist" is read as absent. The ported code swallowed every
    /// <see cref="AmazonCloudFormationException"/> here, which meant a THROTTLE read as "no stack" — and the
    /// create that followed would fail against a stack that was there all along. Anything else propagates
    /// and is classified, so a throttle gets retried like the transient error it is.
    /// </remarks>
    private async Task<string?> StatusAsync(string name, CancellationToken ct)
    {
        try
        {
            var stacks = (await cfn.DescribeStacksAsync(new DescribeStacksRequest { StackName = name }, ct)).Stacks;
            return stacks.Count == 0 ? null : stacks[0].StackStatus?.Value;
        }
        catch (AmazonCloudFormationException e) when (CloudFormationPhases.IsStackMissing(e.ErrorCode, e.Message))
        {
            return null;
        }
    }

    /// <summary>
    /// Upload the template and any assets it references to a per-account staging bucket, and return the
    /// template's URL.
    /// </summary>
    /// <remarks>
    /// By URL rather than inline because a real template exceeds CloudFormation's inline size limit. The
    /// bucket is <c>{prefix}-deploy-{account}</c> — per account so two operators never collide, and derived
    /// rather than configured so there is nothing to get wrong.
    /// </remarks>
    private async Task<string> StageAsync(ProcedureUnit unit, DeploymentRequest request, CancellationToken ct)
    {
        var template = Part(unit, request, AwsOptions.Template, mustBeDirectory: false);
        var bucket = $"{request.Prefix}-deploy-{await account.IdAsync(ct)}".ToLowerInvariant();
        await EnsureBucketAsync(bucket, ct);

        // Assets keep their file names: those ARE the object keys the synthesized template refers to, so
        // renaming one here would produce a stack that cannot find its own Lambda code.
        if (request.Option(unit.Name, AwsOptions.Assets) is not null)
        {
            var assets = Part(unit, request, AwsOptions.Assets, mustBeDirectory: true);
            foreach (var file in Directory.EnumerateFiles(assets, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                await s3.PutObjectAsync(new PutObjectRequest
                {
                    BucketName = bucket,
                    Key = Path.GetRelativePath(assets, file).Replace('\\', '/'),
                    FilePath = file,
                }, ct);
            }
        }

        var key = $"{Name(request, unit)}/{Path.GetFileName(template)}";
        await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket, Key = key, FilePath = template, ContentType = "application/json",
        }, ct);

        return $"https://{bucket}.s3.{region}.amazonaws.com/{key}";
    }

    private async Task EnsureBucketAsync(string bucket, CancellationToken ct)
    {
        try { await s3.PutBucketAsync(new PutBucketRequest { BucketName = bucket, UseClientRegion = true }, ct); }
        catch (AmazonS3Exception e) when (e.ErrorCode is "BucketAlreadyOwnedByYou" or "BucketAlreadyExists")
        {
            // Ours already, from a previous deployment. Reuse it.
        }
    }

    private async Task StreamEventsAsync(
        string name, ProcedureUnit unit, HashSet<string> seen, Action<ProgressReport> report, CancellationToken ct)
    {
        List<StackEvent> events;
        try
        {
            events = (await cfn.DescribeStackEventsAsync(
                new DescribeStackEventsRequest { StackName = name }, ct)).StackEvents;
        }
        catch (AmazonCloudFormationException e) when (CloudFormationPhases.IsStackMissing(e.ErrorCode, e.Message))
        {
            return;
        }

        // Oldest first, and only resource statuses that have actually settled — the in-progress ones would
        // double every line without saying anything the completion does not.
        foreach (var e in Enumerable.Reverse(events))
        {
            if (e.EventId is null || !seen.Add(e.EventId)) continue;

            var status = e.ResourceStatus?.Value ?? "";
            var failed = status.EndsWith("_FAILED", StringComparison.Ordinal);
            if (!failed && !status.EndsWith("_COMPLETE", StringComparison.Ordinal)) continue;

            var reason = failed && !string.IsNullOrWhiteSpace(e.ResourceStatusReason)
                ? $" — {e.ResourceStatusReason}"
                : "";
            report(new ProgressReport(unit.Name, $"{e.LogicalResourceId}: {status}{reason}", -1,
                failed ? ProgressStatus.Error : ProgressStatus.Info));
        }
    }

    /// <summary>
    /// The reason of the FIRST resource that failed, which is the one that caused everything after it.
    /// Reporting the last would name a resource that only failed because it was rolled back.
    /// </summary>
    private async Task<string> FirstFailureAsync(string name, CancellationToken ct)
    {
        try
        {
            var events = (await cfn.DescribeStackEventsAsync(
                new DescribeStackEventsRequest { StackName = name }, ct)).StackEvents;

            return Enumerable.Reverse(events)
                .FirstOrDefault(e => (e.ResourceStatus?.Value ?? "").EndsWith("_FAILED", StringComparison.Ordinal)
                    && !string.IsNullOrWhiteSpace(e.ResourceStatusReason))
                ?.ResourceStatusReason ?? "";
        }
        catch
        {
            // Best effort by design: this runs while reporting another failure, and failing to enrich a
            // message must never replace it.
            return "";
        }
    }

    private static List<Parameter> Parameters(ProcedureUnit unit, DeploymentRequest request) =>
        request.OptionSet(unit.Name, AwsOptions.ParameterPrefix)
            .Select(kv => new Parameter { ParameterKey = kv.Key, ParameterValue = kv.Value })
            .ToList();

    private static List<string> Capabilities(ProcedureUnit unit, DeploymentRequest request) =>
        (request.Option(unit.Name, AwsOptions.Capabilities) ?? AwsOptions.DefaultCapabilities)
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .ToList();

    private static List<Tag> Tags(DeploymentRequest request) =>
        request.Tags?.Select(kv => new Tag { Key = kv.Key, Value = kv.Value }).ToList() ?? [];

    /// <summary>
    /// Resolve an artifact part to a local path. A part the artifact does not carry is a HARD failure raised
    /// before anything is uploaded — the operator named something that is not there.
    /// </summary>
    private static string Part(ProcedureUnit unit, DeploymentRequest request, string option, bool mustBeDirectory)
    {
        var name = request.Option(unit.Name, option)
            ?? throw new AwsDeploymentException(
                $"Unit '{unit.Name}' names no '{option}' — say which part of the artifact it is.");

        var path = request.Artifact.Part(name)
            ?? throw new AwsDeploymentException(
                $"The artifact has no part named '{name}'. It carries: " +
                $"{string.Join(", ", request.Artifact.Parts.Keys.DefaultIfEmpty("nothing"))}.");

        var exists = mustBeDirectory ? Directory.Exists(path) : File.Exists(path);
        if (!exists)
            throw new AwsDeploymentException(
                $"Artifact part '{name}' points at '{path}', which is not " +
                $"{(mustBeDirectory ? "a directory" : "a file")} on this machine. Build first.");

        return path;
    }
}
