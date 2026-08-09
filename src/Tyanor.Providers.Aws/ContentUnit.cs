using Amazon.CloudFront;
using Amazon.CloudFront.Model;
using Amazon.S3;
using Amazon.S3.Model;

namespace Tyanor.Providers.Aws;

/// <summary>
/// A unit that IS a directory of files in an S3 bucket, optionally with the CDN in front of it invalidated
/// afterwards — a website, in other words.
///
/// <para><b>The bucket usually belongs to another unit.</b> A stack creates it and exports its name; this
/// unit reads that output at apply time via <c>bucketFrom</c>. So the dependency is expressed by declaring
/// the stack first and never by an edge — which is <c>units-not-graphs.md</c> working on the provider it was
/// written for.</para>
///
/// <para><b>There is no <see cref="UnitPhase.Converging"/> here.</b> An S3 sync is converged only by the
/// process doing it; nothing on AWS keeps uploading after that process dies. Exactly the property the local
/// provider has, on the cloud — which is a useful reminder that "has a control plane" is a property of a
/// SERVICE and not of a vendor.</para>
/// </summary>
internal sealed class ContentUnit(IAmazonS3 s3, IAmazonCloudFront cloudFront, StackUnit stacks) : IUnitDriver
{
    /// <summary>
    /// Missing or Ready, and the question is only whether the BUCKET is there — not whether its contents are
    /// current.
    /// </summary>
    /// <remarks>
    /// Deliberately not "Missing when a file differs", which was the first shape of this and made the plan
    /// lie: <see cref="ReconcileAction.Create"/> renders as "create (nothing there now)", and reading that
    /// about a website that is up and serving is worse than saying nothing. Whether the files are current is
    /// what <see cref="UpdateAsync"/> is for, and "already up to date" is an answer the engine already knows
    /// how to report.
    /// </remarks>
    public async Task<UnitPhase> PhaseAsync(UnitContext context)
    {
        var bucket = await BucketAsync(context);
        if (bucket is null) return UnitPhase.Missing;         // the stack that makes it is not deployed yet

        return await ObjectsAsync(bucket, context.Cancellation) is null ? UnitPhase.Missing : UnitPhase.Ready;
    }

    /// <inheritdoc/>
    public Task CreateAsync(UnitContext context)
        => SyncAsync(context);

    /// <summary>
    /// Re-sync, and report whether anything actually moved.
    /// </summary>
    /// <remarks>
    /// Files are compared by name and SIZE, never by content: comparing bodies means downloading them, and
    /// the operator asked for a deployment rather than a bandwidth bill. So an edit that preserves a file's
    /// byte count is not noticed — stated plainly, because the alternative is an operator discovering it.
    /// Reporting false here is what stops a redeploy of an unchanged build from invalidating a CDN for
    /// nothing.
    /// </remarks>
    public async Task<bool> UpdateAsync(UnitContext context)
    {
        var bucket = await BucketAsync(context);
        var deployed = bucket is null ? null : await ObjectsAsync(bucket, context.Cancellation);

        if (deployed is not null
            && LocalFiles(context).All(f => deployed.TryGetValue(f.Key, out var size) && size == f.Value))
            return false;

        await SyncAsync(context);
        return true;
    }

    /// <summary>
    /// Empty the prefix this unit uploaded. The BUCKET is not deleted — it belongs to the stack that created
    /// it, and that stack's own removal takes it. A unit that deleted another unit's resource would break
    /// reverse-order teardown by reaching sideways.
    /// </summary>
    public async Task RemoveAsync(UnitContext context)
    {
        var bucket = await BucketAsync(context);
        if (bucket is null) return;

        var deployed = await ObjectsAsync(bucket, context.Cancellation);
        if (deployed is null || deployed.Count == 0) return;

        foreach (var batch in deployed.Keys.Chunk(1000))       // DeleteObjects takes at most 1000 at a time
        {
            context.ThrowIfCancelled();
            await s3.DeleteObjectsAsync(new DeleteObjectsRequest
            {
                BucketName = bucket,
                Objects = batch.Select(k => new KeyVersion { Key = k }).ToList(),
            }, context.Cancellation);
        }
    }

    /// <summary>Nothing to wait for — see the note on this class about why there is no converging state.</summary>
    public Task AwaitSettledAsync(UnitContext context)
        => Task.CompletedTask;

    /// <summary>
    /// One resource: the bucket, fingerprinted by what is in it. The fingerprint is a count and a total size
    /// rather than a hash of every object, which is the honest trade — it catches a website that lost files
    /// or was replaced, and it does not catch an edit that happens to preserve the byte count.
    /// </summary>
    public async Task<IReadOnlyList<ResourceState>> RefreshAsync(UnitContext context)
    {
        var bucket = await BucketAsync(context);
        if (bucket is null) return [];

        var deployed = await ObjectsAsync(bucket, context.Cancellation);
        if (deployed is null) return [];

        return [new ResourceState($"s3://{bucket}", "AWS::S3::Bucket",
            $"{deployed.Count} objects, {deployed.Values.Sum()} bytes")];
    }

    /// <summary>
    /// Resolve what a sync would resolve, without calling AWS. The bucket reference is checked for SHAPE
    /// only — whether the stack it names has actually been deployed is a question for a plan.
    /// </summary>
    public Task<IReadOnlyList<string>> ValidateAsync(UnitContext context)
    {
        var problems = new List<string>();

        try { Source(context); }
        catch (DefinitionException e) { problems.Add(e.Message); }

        if (context.Option(AwsOptions.Bucket) is null && context.Option(AwsOptions.BucketFrom) is null)
            problems.Add(
                $"Names no destination bucket. Set '{AwsOptions.Bucket}', or '{AwsOptions.BucketFrom}' to " +
                "\"{unit}:{OutputKey}\" naming a stack that exports one.");

        foreach (var option in (string[])[AwsOptions.BucketFrom, AwsOptions.InvalidateFrom])
            if (context.Option(option) is { } reference && !IsReference(reference))
                problems.Add($"'{option}' is '{reference}'; it must be \"{{unit}}:{{OutputKey}}\".");

        return Task.FromResult<IReadOnlyList<string>>(problems);
    }

    private static bool IsReference(string reference)
    {
        var parts = reference.Split(':', 2);
        return parts.Length == 2 && !parts.Any(string.IsNullOrWhiteSpace);
    }

    private async Task SyncAsync(UnitContext context)
    {
        var bucket = await BucketAsync(context)
            ?? throw new AwsConfigurationException(
                $"Unit '{context.Name}' has no destination bucket. Set '{AwsOptions.Bucket}', or " +
                $"'{AwsOptions.BucketFrom}' to \"{{unit}}:{{OutputKey}}\" naming a stack that exports one.");

        var source = Source(context);

        // Listed rather than streamed so the count is knowable. A website is hundreds of small files and
        // this is the slow part of the run — the same reason the local provider narrates its copy, and for
        // the same reason the engine cannot do it for us: the work is inside a create, where a provider
        // with a control plane would have nothing to do.
        var files = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).ToList();
        var uploaded = 0;

        foreach (var file in files)
        {
            context.ThrowIfCancelled();
            await s3.PutObjectAsync(new PutObjectRequest
            {
                BucketName = bucket,
                Key = Path.GetRelativePath(source, file).Replace('\\', '/'),
                FilePath = file,
                ContentType = ContentTypes.Of(file),
            }, context.Cancellation);

            // Percent is through THIS unit; the engine rescales it into the run.
            uploaded++;
            if (uploaded % 25 == 0 || uploaded == files.Count)
                context.Progress($"{context.Label}: uploaded {uploaded} of {files.Count} files…",
                    (int)(100.0 * uploaded / files.Count));
        }

        // Without this the files are up and the CDN keeps serving the old ones until they expire, which
        // looks exactly like a deployment that silently did nothing.
        if (await ResolveAsync(context, AwsOptions.InvalidateFrom) is { } distribution)
        {
            context.Progress($"{context.Label}: clearing the CDN cache…");
            await cloudFront.CreateInvalidationAsync(new CreateInvalidationRequest
            {
                DistributionId = distribution,
                InvalidationBatch = new InvalidationBatch
                {
                    // A caller reference must be unique per request; CloudFront rejects a repeat.
                    CallerReference = Guid.NewGuid().ToString("N"),
                    Paths = new Paths { Quantity = 1, Items = ["/*"] },
                },
            }, context.Cancellation);
        }
    }

    /// <summary>Object key → size, or null when the bucket itself is not there.</summary>
    private async Task<Dictionary<string, long>?> ObjectsAsync(string bucket, CancellationToken ct)
    {
        var found = new Dictionary<string, long>(StringComparer.Ordinal);
        string? token = null;
        try
        {
            do
            {
                var page = await s3.ListObjectsV2Async(
                    new ListObjectsV2Request { BucketName = bucket, ContinuationToken = token }, ct);
                foreach (var o in page.S3Objects ?? []) found[o.Key] = o.Size ?? 0;
                token = page.IsTruncated == true ? page.NextContinuationToken : null;
            } while (token is not null);
            return found;
        }
        catch (AmazonS3Exception e) when (e.ErrorCode is "NoSuchBucket") { return null; }
    }

    private static Dictionary<string, long> LocalFiles(UnitContext context)
    {
        var source = Source(context);
        return Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(source, f).Replace('\\', '/'), f => new FileInfo(f).Length,
                StringComparer.Ordinal);
    }

    private static string Source(UnitContext context)
    {
        var name = context.Option(AwsOptions.Source)
            ?? throw new AwsConfigurationException(
                $"Unit '{context.Name}' is content but names no '{AwsOptions.Source}'.");

        return context.Artifact.RequirePart(name, ArtifactPart.Directory);
    }

    private async Task<string?> BucketAsync(UnitContext context)
        => context.Option(AwsOptions.Bucket)
           ?? await ResolveAsync(context, AwsOptions.BucketFrom);

    /// <summary>
    /// Read a <c>"{unit}:{OutputKey}"</c> reference out of another stack's outputs. Null when the stack is
    /// not deployed yet — which is a legitimate answer during a plan of a deployment that does not exist.
    /// </summary>
    private async Task<string?> ResolveAsync(UnitContext context, string option)
    {
        if (context.Option(option) is not { } reference) return null;

        var parts = reference.Split(':', 2);
        if (parts.Length != 2 || parts.Any(string.IsNullOrWhiteSpace))
            throw new AwsConfigurationException(
                $"'{option}' on unit '{context.Name}' is '{reference}'; it must be \"{{unit}}:{{OutputKey}}\".");

        try
        {
            var outputs = await stacks.OutputsAsync($"{context.Request.Prefix}-{parts[0]}", context.Cancellation);
            return outputs.TryGetValue(parts[1], out var value) ? value : null;
        }
        catch (Amazon.CloudFormation.AmazonCloudFormationException e)
            when (CloudFormationPhases.IsStackMissing(e.ErrorCode, e.Message))
        {
            return null;
        }
    }
}

/// <summary>
/// MIME types for the files a website is made of.
/// </summary>
/// <remarks>
/// S3 defaults every upload to <c>application/octet-stream</c>, which makes a browser download a page
/// instead of rendering it. So this is not polish — a site uploaded without it does not work.
/// </remarks>
internal static class ContentTypes
{
    /// <summary>The type for a file, by extension.</summary>
    public static string Of(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" or ".htm" => "text/html",
        ".js" or ".mjs" => "application/javascript",
        ".css" => "text/css",
        ".json" or ".map" => "application/json",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".avif" => "image/avif",
        ".ico" => "image/x-icon",
        ".woff" => "font/woff",
        ".woff2" => "font/woff2",
        ".ttf" => "font/ttf",
        ".xml" => "application/xml",
        ".txt" => "text/plain",
        ".webmanifest" => "application/manifest+json",
        ".wasm" => "application/wasm",
        _ => "application/octet-stream",
    };
}
