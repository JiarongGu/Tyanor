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
    /// Missing or Ready, and the question is whether this unit has put ANYTHING there — not whether what is
    /// there is current.
    /// </summary>
    /// <remarks>
    /// <para>Deliberately not "Missing when a file differs", which was the first shape of this and made the
    /// plan lie: <see cref="ReconcileAction.Create"/> renders as "create (nothing there now)", and reading
    /// that about a website that is up and serving is worse than saying nothing. Whether the files are
    /// current is what <see cref="UpdateAsync"/> is for, and "already up to date" is an answer the engine
    /// already knows how to report.</para>
    /// <para><b>An EMPTY bucket is Missing, and that is a correction.</b> This used to ask only whether the
    /// bucket EXISTED — but the bucket is the stack's resource, not this unit's, and it outlives this unit's
    /// teardown by design. So an emptied bucket reported <see cref="UnitPhase.Ready"/>: a destroyed content
    /// unit claimed to still be deployed, a second teardown plan said there was something left to remove,
    /// and a bucket a stack had just created reported Ready before anything was ever uploaded to it.
    /// <c>UnitDriverContract</c> found all four at once, which is what it is for.</para>
    /// </remarks>
    public async Task<UnitPhase> PhaseAsync(UnitContext context)
    {
        var bucket = await BucketAsync(context);
        if (bucket is null) return UnitPhase.Missing;         // the stack that makes it is not deployed yet

        var deployed = await S3Objects.ListAsync(s3, bucket, context.Cancellation);
        return deployed is null || deployed.Count == 0 ? UnitPhase.Missing : UnitPhase.Ready;
    }

    /// <inheritdoc/>
    public Task CreateAsync(UnitContext context)
        => SyncAsync(context);

    /// <summary>
    /// Re-sync, and report whether anything actually moved.
    /// </summary>
    /// <remarks>
    /// <para>Files are compared by name and SIZE, never by content: comparing bodies means downloading them,
    /// and the operator asked for a deployment rather than a bandwidth bill. So an edit that preserves a
    /// file's byte count is not noticed — stated plainly, because the alternative is an operator discovering
    /// it. Reporting false here is what stops a redeploy of an unchanged build from invalidating a CDN for
    /// nothing.</para>
    /// <para><b>Both directions, and the second one was missing.</b> This used to ask only whether every
    /// local file was up there, which is satisfied by a bucket holding those files AND every page the build
    /// stopped producing. So deleting a page from a site never removed it: the update reported no change, a
    /// plan said the deployment was current, and the deleted page went on being served — for ever, since
    /// nothing else ever looks. Comparing the sets means an extra key is a change like any other.</para>
    /// </remarks>
    public async Task<bool> UpdateAsync(UnitContext context)
    {
        var bucket = await BucketAsync(context);
        var deployed = bucket is null ? null : await S3Objects.ListAsync(s3, bucket, context.Cancellation);
        var local = LocalFiles(context);

        // Set equality: same count, and every local file present at the same size. Keys are unique on both
        // sides, so that is enough — and the count is what notices a file the build no longer produces.
        if (deployed is not null
            && deployed.Count == local.Count
            && local.All(f => deployed.TryGetValue(f.Key, out var size) && size == f.Value))
            return false;

        // Handing over what was just read saves a second LIST, and is the listing the prune must diff
        // against: everything uploaded below is by definition wanted, so a pre-upload view is the right one.
        await SyncAsync(context, deployed);
        return true;
    }

    /// <summary>
    /// Empty the bucket. The BUCKET ITSELF is not deleted — it belongs to the stack that created it, and
    /// that stack's own removal takes it. A unit that deleted another unit's resource would break
    /// reverse-order teardown by reaching sideways.
    /// </summary>
    /// <remarks>
    /// It empties the whole bucket, not a prefix within it, because that is what this unit fills: a sync
    /// writes keys at the root. Stated because it matters if you point a content unit at a bucket holding
    /// anything else — do not; give it one of its own, which is what <c>bucketFrom</c> naming a stack's
    /// output produces.
    /// </remarks>
    public async Task RemoveAsync(UnitContext context)
    {
        var bucket = await BucketAsync(context);
        if (bucket is null) return;

        var deployed = await S3Objects.ListAsync(s3, bucket, context.Cancellation);
        if (deployed is null || deployed.Count == 0) return;

        await S3Objects.DeleteAsync(s3, bucket, deployed.Keys, context.Cancellation);
    }

    /// <summary>Nothing to wait for — see the note on this class about why there is no converging state.</summary>
    public Task AwaitSettledAsync(UnitContext context)
        => Task.CompletedTask;

    /// <summary>
    /// One resource: the bucket, fingerprinted by what is in it. The fingerprint is a count and a total size
    /// rather than a hash of every object, which is the honest trade — it catches a website that lost files
    /// or was replaced, and it does not catch an edit that happens to preserve the byte count.
    /// </summary>
    /// <remarks>
    /// An empty bucket owns NOTHING, for the same reason it reads as <see cref="UnitPhase.Missing"/>: the
    /// bucket belongs to the stack that made it, and this unit owns only what it uploaded. Reporting a
    /// resource for an emptied bucket left state claiming a destroyed unit still held something, which is
    /// the one question state exists to answer correctly.
    /// </remarks>
    public async Task<IReadOnlyList<ResourceState>> RefreshAsync(UnitContext context)
    {
        var bucket = await BucketAsync(context);
        if (bucket is null) return [];

        var deployed = await S3Objects.ListAsync(s3, bucket, context.Cancellation);
        if (deployed is null || deployed.Count == 0) return [];

        return [new ResourceState($"s3://{bucket}", "AWS::S3::Bucket",
            $"{deployed.Count} objects, {deployed.Values.Sum()} bytes")];
    }

    /// <summary>
    /// Resolve what a sync would resolve, without calling AWS. The bucket reference is checked for SHAPE
    /// only — whether the stack it names has actually been deployed is a question for a plan.
    /// </summary>
    /// <remarks>
    /// The reference check is <see cref="OutputReferences.Parse(UnitContext, string)"/>, which is the same
    /// parse <see cref="OutputReferences.ResolveAsync(StackUnit, UnitContext, string)"/> runs at apply time.
    /// It used to be a second copy living here, which is precisely the "two copies of a rule is two rules"
    /// that <c>docs/DECISIONS.md</c> D18 says makes an offline check and the real thing drift apart.
    /// </remarks>
    public Task<IReadOnlyList<string>> ValidateAsync(UnitContext context)
    {
        var problems = new UnitProblems()
            .Check(() => Source(context))
            // Both addresses, so both refuse a procedure-wide spelling — and the refusal arrives here for
            // free, because it is a DefinitionException and that is what Check collects.
            .Check(() => context.Address(AwsOptions.Bucket))
            .Check(() => OutputReferences.Parse(
                AwsOptions.BucketFrom, context.Name, context.Address(AwsOptions.BucketFrom)))
            .Check(() => OutputReferences.Parse(context, AwsOptions.InvalidateFrom))
            .Check(() => RefuseBoth(context));

        // No resolver raises this one: it is the absence of BOTH options together, so there is nothing to
        // call whose refusal would say it. Read with the SHARED reader deliberately, even though both are
        // addresses: an unscoped one is a bucket named in the wrong place, and the check above says exactly
        // that. Asking here whether it is an address too would answer "you named no bucket" to an operator
        // looking at the line where they named one.
        if (context.Option(AwsOptions.Bucket) is null && context.Option(AwsOptions.BucketFrom) is null)
            problems.Add(
                $"Names no destination bucket. Set '{AwsOptions.Bucket}', or '{AwsOptions.BucketFrom}' to " +
                "\"{unit}:{OutputKey}\" naming a stack that exports one.");

        return problems.Found();
    }

    /// <summary>
    /// Make the bucket be the build: upload everything, then remove whatever the build no longer produces.
    /// </summary>
    /// <param name="context">The unit.</param>
    /// <param name="deployed">
    /// What was in the bucket BEFORE this sync, when the caller has already read it. Null re-reads it.
    /// </param>
    private async Task SyncAsync(UnitContext context, Dictionary<string, long>? deployed = null)
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

        // A build that produced NOTHING is a build that did not run, and this is the last moment anyone can
        // say so cheaply. Converging on it would be defensible — an empty directory does describe an empty
        // site — but the prune below would then empty a live website because a build step failed quietly,
        // which is not a trade worth making silently.
        //
        // The local provider needs no such guard, and the difference is real rather than an inconsistency:
        // there each build lands in its OWN release directory and the marker only moves once the copy is
        // done, so an empty build costs one unused release and leaves what is serving untouched. Here there
        // is one namespace and it is the one being served.
        if (files.Count == 0)
            throw new AwsConfigurationException(
                $"Unit '{context.Name}' is content, and the artifact part it names is an empty directory " +
                $"('{source}'). Nothing would be uploaded and everything currently in '{bucket}' would be " +
                "removed as no longer produced. Build first.");

        var uploaded = 0;
        var wanted = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in files)
        {
            context.ThrowIfCancelled();
            var key = Path.GetRelativePath(source, file).Replace('\\', '/');
            wanted.Add(key);

            try
            {
                await s3.PutObjectAsync(new PutObjectRequest
                {
                    BucketName = bucket,
                    Key = key,
                    FilePath = file,
                    ContentType = ContentTypes.Of(file),
                }, context.Cancellation);
            }
            catch (AmazonS3Exception e) when (e.ErrorCode is "NoSuchBucket")
            {
                // This unit never CREATES its bucket — the stack declared before it does. So the bucket
                // being absent means that stack has not been deployed, and the raw SDK error says only
                // "the specified bucket does not exist", which sends an operator looking at S3 rather than
                // at their procedure. Most likely reached by narrowing a run to the content unit alone.
                throw new AwsConfigurationException(
                    $"Unit '{context.Name}' uploads to the bucket '{bucket}', which does not exist. This " +
                    "unit does not create it — the stack declared before it does. Deploy that stack first, " +
                    $"or check '{AwsOptions.BucketFrom}' names the right unit and output.");
            }

            // Percent is through THIS unit; the engine rescales it into the run.
            uploaded++;
            if (uploaded % 25 == 0 || uploaded == files.Count)
                context.Progress($"{context.Label}: uploaded {uploaded} of {files.Count} files…",
                    (int)(100.0 * uploaded / files.Count));
        }

        await PruneAsync(context, bucket, wanted, deployed);

        // Without this the files are up and the CDN keeps serving the old ones until they expire, which
        // looks exactly like a deployment that silently did nothing.
        if (await OutputReferences.ResolveAsync(stacks, context, AwsOptions.InvalidateFrom) is { } distribution)
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

    /// <summary>
    /// Remove what is in the bucket and no longer in the build.
    /// </summary>
    /// <param name="context">The unit.</param>
    /// <param name="bucket">The destination.</param>
    /// <param name="wanted">Every key this sync just uploaded.</param>
    /// <param name="deployed">What was there beforehand, or null to read it now.</param>
    /// <remarks>
    /// <para><b>After the upload, never before.</b> An interruption then leaves the site with both the old
    /// files and the new ones — stale, and serving. Pruning first would leave it with neither.</para>
    /// <para><b>It prunes the whole bucket, because that is what this unit fills.</b> A sync writes keys at
    /// the root, <see cref="RemoveAsync"/> empties the lot, and <see cref="PhaseAsync"/> reads an empty
    /// bucket as <see cref="UnitPhase.Missing"/> — all three already say this unit owns the bucket's
    /// contents. Point it at a bucket holding anything else and this takes that too; give it one of its own,
    /// which is what <c>bucketFrom</c> naming a stack's output produces.</para>
    /// </remarks>
    private async Task PruneAsync(
        UnitContext context, string bucket, HashSet<string> wanted, Dictionary<string, long>? deployed)
    {
        deployed ??= await S3Objects.ListAsync(s3, bucket, context.Cancellation);

        var stale = deployed?.Keys.Where(k => !wanted.Contains(k)).ToList();
        if (stale is null || stale.Count == 0) return;

        context.Progress($"{context.Label}: removing {stale.Count} files the build no longer produces…");
        await S3Objects.DeleteAsync(s3, bucket, stale, context.Cancellation);
    }

    private static Dictionary<string, long> LocalFiles(UnitContext context)
    {
        var source = Source(context);
        return Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(source, f).Replace('\\', '/'), f => new FileInfo(f).Length,
                StringComparer.Ordinal);
    }

    /// <summary>
    /// The directory of files this unit syncs. Core's resolution, not ours — see
    /// <see cref="UnitContext.RequirePart"/> for why the sentence an operator gets about a missing part
    /// belongs in one place.
    /// </summary>
    private static string Source(UnitContext context) =>
        context.RequirePart(AwsOptions.Source, ArtifactPart.Directory);

    /// <summary>
    /// The destination bucket, named outright or read out of a stack's outputs. Null when the stack that
    /// makes it is not deployed yet, which is a legitimate answer during a plan.
    /// </summary>
    /// <remarks>
    /// <para><b>Both are ADDRESSES, so both are read per unit</b> — <see cref="UnitContext.Address"/>. Read
    /// with the shared fallback, one unscoped <c>bucket</c> sends every content unit to the same place, and
    /// since a sync prunes whatever the build no longer produces, the second unit to deploy DELETES the
    /// first's website. Measured, not argued: two units, one unscoped bucket, and the site's
    /// <c>index.html</c> was gone after the docs unit ran. <c>docs/DECISIONS.md</c> D36.</para>
    /// <para><b>Set both ways it is refused rather than resolved</b>, which is D34's rule at the site D35
    /// did not reach. This one is not benign the way the staging bucket was: <c>bucket</c> winning over
    /// <c>bucketFrom</c> uploads a website to a bucket the operator is migrating AWAY from, while the stack's
    /// bucket — the one the CDN is in front of — stays empty. Nothing errors, and the site serves nothing.</para>
    /// </remarks>
    private async Task<string?> BucketAsync(UnitContext context)
    {
        RefuseBoth(context);

        return context.Address(AwsOptions.Bucket)
               ?? await OutputReferences.ResolveAsync(
                   stacks, context, AwsOptions.BucketFrom, context.Address(AwsOptions.BucketFrom));
    }

    /// <summary>Refuse a destination named twice — which was meant is not knowable, and picking one deploys
    /// a website to a bucket nobody chose.</summary>
    /// <param name="context">The unit.</param>
    /// <exception cref="AwsConfigurationException">Both are set.</exception>
    private static void RefuseBoth(UnitContext context)
    {
        if (context.Address(AwsOptions.Bucket) is not null && context.Address(AwsOptions.BucketFrom) is not null)
            throw new AwsConfigurationException(
                $"Unit '{context.Name}' names a destination bucket both with '{AwsOptions.Bucket}' and with " +
                $"'{AwsOptions.BucketFrom}'. Remove one — which was meant is not something this can guess.");
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
