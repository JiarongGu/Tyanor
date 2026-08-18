using Amazon;
using Amazon.CloudFormation;
using Amazon.CloudFormation.Model;
using Amazon.CloudFront;
using Amazon.CloudFront.Model;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SecurityToken;
using Amazon.SecurityToken.Model;

namespace Tyanor.Providers.Aws.Tests;

/// <summary>
/// Stand-in AWS clients that RECORD what this provider sent and REPLAY what a test says came back.
///
/// <para><b>What these are for, and what they are emphatically not for.</b> There are two different
/// questions about a cloud provider, and conflating them is how one of them goes untested:</para>
///
/// <list type="bullet">
/// <item><description><i>Does CloudFormation actually answer this way?</i> — only a real deployment can say.
/// A fake answers it by agreeing with whatever this code already believes, which is worth nothing. That
/// question stays behind <c>TYANOR_LIVE_AWS</c>.</description></item>
/// <item><description><i>Given that answer, does this provider do the right thing?</i> — that is OUR logic:
/// which request we build, whether we re-issue against a stack that is already gone, whether a throttle is
/// read as "no stack". A fake answers that one exactly, and nothing else can answer it cheaply.</description></item>
/// </list>
///
/// <para>The rule that keeps the second honest: <b>a fake never invents a VALUE this provider interprets.</b>
/// Every status string and error code handed back below is a real one, and the mapping from those strings to
/// a <see cref="UnitPhase"/> is pinned separately by <c>CloudFormationPhaseTests</c> against the SDK's own
/// enumeration. These tests are about control flow and request shape, never about vocabulary.</para>
///
/// <para>They subclass the real SDK clients rather than implementing the interfaces, because
/// <c>IAmazonCloudFormation</c> has some two hundred members and the generated client marks the operations
/// virtual precisely so they can be replaced. Constructing the base makes no network call.</para>
/// </summary>
internal sealed class FakeCloudFormation : AmazonCloudFormationClient
{
    private readonly Queue<string?> _statuses = new();

    public FakeCloudFormation() : base(Fake.Credentials, RegionEndpoint.USEast1) { }

    /// <summary>Every request this provider issued, in order, as the SDK object it built.</summary>
    public List<AmazonWebServiceRequest> Requests { get; } = [];

    /// <summary>Resource events <c>DescribeStackEvents</c> returns, newest LAST — the SDK returns them newest first.</summary>
    public List<StackEvent> Events { get; } = [];

    /// <summary>Resources <c>DescribeStackResources</c> returns.</summary>
    public List<StackResource> Resources { get; } = [];

    /// <summary>Outputs the stack exposes.</summary>
    public Dictionary<string, string> Outputs { get; } = [];

    /// <summary>Thrown by the next <c>DescribeStacks</c>, then cleared — for the throttle case.</summary>
    public Exception? DescribeThrows { get; set; }

    /// <summary>Thrown by <c>UpdateStack</c> — for the "no updates are to be performed" case.</summary>
    public Exception? UpdateThrows { get; set; }

    /// <summary>How many times the stack's status was read.</summary>
    public int StatusReads { get; private set; }

    /// <summary>The statuses to hand back, in order. The last one repeats once the queue empties.</summary>
    public FakeCloudFormation Returning(params string?[] statuses)
    {
        foreach (var s in statuses) _statuses.Enqueue(s);
        return this;
    }

    /// <summary>The one request of a kind this provider issued, or a failure if it issued none.</summary>
    public T Sent<T>() where T : AmazonWebServiceRequest => Requests.OfType<T>().Single();

    /// <summary>How many requests of a kind were issued.</summary>
    public int Count<T>() where T : AmazonWebServiceRequest => Requests.OfType<T>().Count();

    private string? Next()
    {
        StatusReads++;
        // The last scripted status repeats: a wait polls an unknown number of times, and a test should say
        // "it becomes CREATE_COMPLETE" rather than have to guess how many reads that takes.
        if (_statuses.Count == 0) return null;
        return _statuses.Count == 1 ? _statuses.Peek() : _statuses.Dequeue();
    }

    public override Task<DescribeStacksResponse> DescribeStacksAsync(
        DescribeStacksRequest request, CancellationToken ct = default)
    {
        Requests.Add(request);

        if (DescribeThrows is { } boom) { DescribeThrows = null; throw boom; }

        var status = Next();
        // CloudFormation reports "no such stack" by REFUSING to describe it, not by a status — so the fake
        // has to refuse too, or the code path that reads absence would never run here.
        if (status is null)
            throw new AmazonCloudFormationException($"Stack with id {request.StackName} does not exist")
            { ErrorCode = "ValidationError" };

        return Task.FromResult(new DescribeStacksResponse
        {
            Stacks =
            [
                new Stack
                {
                    StackName = request.StackName,
                    StackStatus = status,
                    Outputs = [.. Outputs.Select(o => new Output { OutputKey = o.Key, OutputValue = o.Value })],
                },
            ],
        });
    }

    public override Task<CreateStackResponse> CreateStackAsync(CreateStackRequest request, CancellationToken ct = default)
    {
        Requests.Add(request);
        return Task.FromResult(new CreateStackResponse { StackId = "arn:fake" });
    }

    public override Task<UpdateStackResponse> UpdateStackAsync(UpdateStackRequest request, CancellationToken ct = default)
    {
        Requests.Add(request);
        if (UpdateThrows is { } boom) throw boom;
        return Task.FromResult(new UpdateStackResponse { StackId = "arn:fake" });
    }

    public override Task<DeleteStackResponse> DeleteStackAsync(DeleteStackRequest request, CancellationToken ct = default)
    {
        Requests.Add(request);
        return Task.FromResult(new DeleteStackResponse());
    }

    public override Task<DescribeStackResourcesResponse> DescribeStackResourcesAsync(
        DescribeStackResourcesRequest request, CancellationToken ct = default)
    {
        Requests.Add(request);
        return Task.FromResult(new DescribeStackResourcesResponse { StackResources = [.. Resources] });
    }

    public override Task<DescribeStackEventsResponse> DescribeStackEventsAsync(
        DescribeStackEventsRequest request, CancellationToken ct = default)
    {
        Requests.Add(request);
        // Newest first, which is the order CloudFormation returns and the order the driver reverses.
        return Task.FromResult(new DescribeStackEventsResponse { StackEvents = [.. Enumerable.Reverse(Events)] });
    }
}

/// <summary>S3, recording uploads and deletions and holding an in-memory bucket.</summary>
internal sealed class FakeS3 : AmazonS3Client
{
    public FakeS3() : base(Fake.Credentials, RegionEndpoint.USEast1) { }

    /// <summary>Bucket name → (object key → size). A bucket absent here does not exist.</summary>
    public Dictionary<string, Dictionary<string, long>> Buckets { get; } = [];

    /// <summary>Every object written, key → the content type it was written with.</summary>
    public Dictionary<string, string?> Uploaded { get; } = [];

    /// <summary>Every key deleted, in order.</summary>
    public List<string> Deleted { get; } = [];

    /// <summary>Buckets this provider asked to create.</summary>
    public List<string> Created { get; } = [];

    /// <summary>How many <c>DeleteObjects</c> calls were made — the batching is what this checks.</summary>
    public int DeleteBatches { get; private set; }

    public override Task<PutBucketResponse> PutBucketAsync(PutBucketRequest request, CancellationToken ct = default)
    {
        Created.Add(request.BucketName);
        Buckets.TryAdd(request.BucketName, []);
        return Task.FromResult(new PutBucketResponse());
    }

    /// <remarks>
    /// Refuses a bucket that does not exist, which is what S3 does — and is the one piece of faithfulness
    /// that matters here, because this provider never creates the content bucket. The stack declared before
    /// it does. A fake that auto-created would hide the case where content is deployed before its stack.
    /// </remarks>
    public override Task<PutObjectResponse> PutObjectAsync(PutObjectRequest request, CancellationToken ct = default)
    {
        if (!Buckets.TryGetValue(request.BucketName, out var bucket))
            throw new AmazonS3Exception("The specified bucket does not exist") { ErrorCode = "NoSuchBucket" };

        Uploaded[request.Key] = request.ContentType;
        bucket[request.Key] = request.FilePath is null ? 0 : new FileInfo(request.FilePath).Length;
        return Task.FromResult(new PutObjectResponse());
    }

    public override Task<ListObjectsV2Response> ListObjectsV2Async(
        ListObjectsV2Request request, CancellationToken ct = default)
    {
        if (!Buckets.TryGetValue(request.BucketName, out var bucket))
            throw new AmazonS3Exception("The specified bucket does not exist") { ErrorCode = "NoSuchBucket" };

        return Task.FromResult(new ListObjectsV2Response
        {
            S3Objects = [.. bucket.Select(o => new S3Object { Key = o.Key, Size = o.Value })],
            IsTruncated = false,
        });
    }

    public override Task<DeleteObjectsResponse> DeleteObjectsAsync(
        DeleteObjectsRequest request, CancellationToken ct = default)
    {
        DeleteBatches++;
        foreach (var o in request.Objects)
        {
            Deleted.Add(o.Key);
            if (Buckets.TryGetValue(request.BucketName, out var bucket)) bucket.Remove(o.Key);
        }
        return Task.FromResult(new DeleteObjectsResponse());
    }
}

/// <summary>CloudFront, recording invalidations.</summary>
internal sealed class FakeCloudFront : AmazonCloudFrontClient
{
    public FakeCloudFront() : base(Fake.Credentials, RegionEndpoint.USEast1) { }

    /// <summary>Distribution ids this provider asked to invalidate, in order.</summary>
    public List<string> Invalidated { get; } = [];

    /// <summary>The caller references used, which CloudFront requires to be unique per request.</summary>
    public List<string> CallerReferences { get; } = [];

    public override Task<CreateInvalidationResponse> CreateInvalidationAsync(
        CreateInvalidationRequest request, CancellationToken ct = default)
    {
        Invalidated.Add(request.DistributionId);
        CallerReferences.Add(request.InvalidationBatch.CallerReference);
        return Task.FromResult(new CreateInvalidationResponse());
    }
}

/// <summary>STS, so the staging bucket's account suffix is knowable without a call.</summary>
internal sealed class FakeSts : AmazonSecurityTokenServiceClient
{
    public FakeSts() : base(Fake.Credentials, RegionEndpoint.USEast1) { }

    /// <summary>How many times the account was actually asked for — the memoization is what this checks.</summary>
    public int Calls { get; private set; }

    public override Task<GetCallerIdentityResponse> GetCallerIdentityAsync(
        GetCallerIdentityRequest request, CancellationToken ct = default)
    {
        Calls++;
        return Task.FromResult(new GetCallerIdentityResponse
        {
            Account = "123456789012",
            Arn = "arn:aws:iam::123456789012:user/deployer",
        });
    }
}

/// <summary>Shared scaffolding for the fakes and the tests that drive them.</summary>
internal static class Fake
{
    /// <summary>Credentials the fakes are constructed with. Never used — nothing here reaches a network.</summary>
    public static BasicAWSCredentials Credentials { get; } = new("AKIAFAKE", "fake");   // tyanor:allow-secret

    /// <summary>A resource event as CloudFormation reports one.</summary>
    public static StackEvent Event(string id, string logical, string status, string? reason = null) =>
        new() { EventId = id, LogicalResourceId = logical, ResourceStatus = status, ResourceStatusReason = reason };
}
