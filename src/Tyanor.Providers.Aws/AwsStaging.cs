using Amazon.S3;
using Amazon.S3.Model;

namespace Tyanor.Providers.Aws;

/// <summary>
/// The bucket this provider uploads templates and assets through, and the only place its name is decided.
///
/// <para><b>Provider-owned, not unit-owned, and that distinction is the whole reason this type exists.</b>
/// Every stack unit in a deployment stages through one bucket, so no unit can create it exclusively and no
/// unit can remove it — a unit deleting it would take away something the units either side of it still need,
/// which is precisely what removing in reverse order exists to prevent. It therefore belongs to the TARGET,
/// and is created on demand by the first stack that stages and removed by
/// <see cref="AwsTarget.SweepAsync"/> once the whole deployment is gone. See <c>docs/DECISIONS.md</c> D33.</para>
///
/// <para><b>The name was computed in one place and swept from another</b>, which is two copies of a
/// convention and the shape this repository keeps getting wrong. It is <see cref="BucketFor"/> now, and both
/// callers ask it.</para>
/// </summary>
internal static class AwsStaging
{
    /// <summary>
    /// The staging bucket for one deployment: <c>{prefix}-deploy-{account}</c>, lowercased.
    /// </summary>
    /// <param name="prefix">The deployment's prefix — what keeps two deployments in one account apart.</param>
    /// <param name="account">The 12-digit account id, so two operators never collide on a global namespace.</param>
    /// <remarks>
    /// Lowercased because S3 bucket names must be, while a Tyanor prefix may not be — and a prefix of
    /// <c>MySite</c> would otherwise produce a name S3 rejects with a message about DNS compliance that says
    /// nothing about the prefix that caused it.
    /// </remarks>
    public static string BucketFor(string prefix, string account) =>
        $"{prefix}-deploy-{account}".ToLowerInvariant();

    /// <summary>Create the bucket unless it already exists.</summary>
    /// <param name="s3">The S3 client.</param>
    /// <param name="bucket">The name from <see cref="BucketFor"/>.</param>
    /// <param name="ct">Cancellation.</param>
    /// <remarks>
    /// <b>The two codes swallowed here do not mean the same thing, and the difference is worth stating.</b>
    /// <c>BucketAlreadyOwnedByYou</c> is ours from a previous deployment — the ordinary case, every run after
    /// the first. <c>BucketAlreadyExists</c> means somebody ELSE holds that name in S3's global namespace,
    /// which the account id in <see cref="BucketFor"/> makes very unlikely and does not make impossible.
    /// Carrying on is still right: the upload that follows fails with an <c>AccessDenied</c> naming the
    /// bucket, which is a better error than one raised here could be, because by then we know the operation
    /// was genuinely refused rather than merely unnecessary.
    /// </remarks>
    public static async Task EnsureAsync(IAmazonS3 s3, string bucket, CancellationToken ct)
    {
        try { await s3.PutBucketAsync(new PutBucketRequest { BucketName = bucket, UseClientRegion = true }, ct); }
        catch (AmazonS3Exception e) when (e.ErrorCode is "BucketAlreadyOwnedByYou" or "BucketAlreadyExists")
        {
            // Already there. Whose it is decides what the next call does, and the next call says so better.
        }
    }

    /// <summary>
    /// Empty the deployment's staging bucket and delete it. Does nothing when it is not there.
    /// </summary>
    /// <param name="s3">The S3 client.</param>
    /// <param name="bucket">The name from <see cref="BucketFor"/>.</param>
    /// <param name="ct">Cancellation.</param>
    /// <remarks>
    /// <para><b>Emptied first, because S3 refuses to delete a bucket with anything in it</b> — and a
    /// deployment's staging bucket always has something in it, since every stack that ever deployed left its
    /// template there. A <c>DeleteBucket</c> on its own would fail with <c>BucketNotEmpty</c> every time,
    /// which is the sort of thing that only shows up against a real account.</para>
    /// <para>Paging and batching are <see cref="S3Objects"/>'s, shared with the content unit rather than
    /// written a second time. This method HAD a second copy, and it had already drifted: it deleted one
    /// listing page per call, which is only within <c>DeleteObjects</c>' limit because a page happens to be
    /// a thousand — a fact about an S3 default that nothing here had chosen.</para>
    /// <para><b>An absent bucket is success, not an error.</b> A teardown is re-runnable, so the second one
    /// reaches this having already removed it — and a deployment that never staged anything never had one.</para>
    /// </remarks>
    public static async Task SweepAsync(IAmazonS3 s3, string bucket, CancellationToken ct)
    {
        // Null means the bucket is not there, which is the ordinary answer on a re-run.
        if (await S3Objects.ListAsync(s3, bucket, ct) is not { } staged) return;

        await S3Objects.DeleteAsync(s3, bucket, staged.Keys, ct);

        try { await s3.DeleteBucketAsync(new DeleteBucketRequest { BucketName = bucket }, ct); }
        catch (AmazonS3Exception e) when (e.ErrorCode is "NoSuchBucket")
        {
            // Removed by something else between the listing and now. The outcome asked for either way.
        }
    }
}
