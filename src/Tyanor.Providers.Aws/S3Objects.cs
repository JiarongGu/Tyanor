using Amazon.S3;
using Amazon.S3.Model;

namespace Tyanor.Providers.Aws;

/// <summary>
/// Reading and emptying a bucket — the two S3 operations that are easy to get almost right and impossible
/// to notice getting almost right until a bucket is large.
///
/// <para><b>Extracted at the third copy, and they had already diverged.</b> The content unit paged a listing
/// and chunked its deletions at a thousand; the staging sweep paged the same way and deleted a page at a
/// time, which is only safe because a page happens to be a thousand — a fact about S3's default that nothing
/// stated and nobody had chosen. That is exactly the shape <c>CLAUDE.md</c> predicts: by the third time, the
/// copies have already disagreed about one of the things they all do.</para>
///
/// <para>The two things they all do, in one place: <b>pagination</b>, because a listing returns a page and a
/// caller that reads only the first silently ignores everything after it; and <b>batching</b>, because
/// <c>DeleteObjects</c> takes at most a thousand keys and a website with more than that is ordinary.</para>
/// </summary>
internal static class S3Objects
{
    /// <summary>What <c>DeleteObjects</c> accepts in one call.</summary>
    private const int Batch = 1000;

    /// <summary>
    /// Every object in a bucket, key → size. Null when the bucket itself is not there.
    /// </summary>
    /// <param name="s3">The S3 client.</param>
    /// <param name="bucket">The bucket to read.</param>
    /// <param name="ct">Cancellation.</param>
    /// <remarks>
    /// <b>Absent is null, not an exception</b>, because both callers have a legitimate reason to meet a
    /// bucket that is not there: a content unit whose stack has not been deployed yet, and a sweep re-running
    /// after a teardown that already removed it.
    /// </remarks>
    public static async Task<Dictionary<string, long>?> ListAsync(
        IAmazonS3 s3, string bucket, CancellationToken ct)
    {
        var found = new Dictionary<string, long>(StringComparer.Ordinal);
        string? token = null;

        try
        {
            do
            {
                ct.ThrowIfCancellationRequested();
                var page = await s3.ListObjectsV2Async(
                    new ListObjectsV2Request { BucketName = bucket, ContinuationToken = token }, ct);

                foreach (var o in page.S3Objects ?? []) found[o.Key] = o.Size ?? 0;
                token = page.IsTruncated == true ? page.NextContinuationToken : null;
            }
            while (token is not null);

            return found;
        }
        catch (AmazonS3Exception e) when (e.ErrorCode is "NoSuchBucket") { return null; }
    }

    /// <summary>
    /// Delete keys, in the batches S3 accepts.
    /// </summary>
    /// <param name="s3">The S3 client.</param>
    /// <param name="bucket">The bucket to empty.</param>
    /// <param name="keys">What to remove. Nothing happens for an empty sequence.</param>
    /// <param name="ct">Cancellation, checked between batches — a large site is many round trips.</param>
    public static async Task DeleteAsync(
        IAmazonS3 s3, string bucket, IEnumerable<string> keys, CancellationToken ct)
    {
        foreach (var batch in keys.Chunk(Batch))
        {
            ct.ThrowIfCancellationRequested();
            await s3.DeleteObjectsAsync(new DeleteObjectsRequest
            {
                BucketName = bucket,
                Objects = [.. batch.Select(k => new KeyVersion { Key = k })],
            }, ct);
        }
    }
}
