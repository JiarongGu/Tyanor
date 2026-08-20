using Amazon.S3.Model;
using Xunit;

namespace Tyanor.Providers.Aws.Tests;

/// <summary>
/// The staging bucket: the one piece of AWS infrastructure this provider creates for ITSELF, and the one no
/// unit could ever remove.
///
/// <para><b>Nothing removed it until D33, and a destroy therefore left it standing</b> — holding every
/// template and Lambda asset the deployment had ever uploaded, in an account the operator believed was
/// clean, while <c>adoption.md</c> promised a teardown left nothing. The deployer this was ported from
/// emptied and deleted it, so it was a regression as well as a gap.</para>
///
/// <para><b>Every check below is about OUR logic, not about S3's.</b> Which bucket is named, that it is
/// emptied before it is deleted, that a bucket already gone is not an error, that paging does not stop after
/// the first thousand objects. What S3 does with the calls stays behind <c>TYANOR_LIVE_AWS</c> — and the two
/// error codes the fake replays (<c>NoSuchBucket</c>, <c>BucketNotEmpty</c>) are real ones rather than
/// invented, which is the rule <c>docs/DECISIONS.md</c> D23 draws.</para>
/// </summary>
public class AwsStagingTests
{
    private const string Account = "123456789012";

    private static SweepContext Context(string prefix = "mysite") =>
        new("site", new DeploymentRequest(prefix, new DeploymentArtifact(new Dictionary<string, string>())));

    private static AwsTarget Target(FakeS3 s3) =>
        new(new StatefulCloudFormation(), s3, new FakeCloudFront(), new FakeSts(), "ap-southeast-2");

    /// <summary>Fill a bucket with <paramref name="count"/> staged objects, as a deployment would.</summary>
    private static FakeS3 Staged(string bucket, int count)
    {
        var s3 = new FakeS3();
        s3.Buckets[bucket] = [];
        for (var i = 0; i < count; i++) s3.Buckets[bucket][$"asset-{i:D5}.zip"] = 10;
        return s3;
    }

    // ── the name, in one place ───────────────────────────────────────────────────────────────────

    [Fact]
    public void The_bucket_is_named_for_the_deployment_and_the_account()
        // Per account so two operators never collide on S3's global namespace; per PREFIX so tearing down a
        // scratch deployment cannot reach the one production stages through.
        => Assert.Equal("mysite-deploy-123456789012", AwsStaging.BucketFor("mysite", Account));

    [Fact]
    public void The_name_is_lowercased_because_a_prefix_may_not_be()
        // S3 requires it and Tyanor's own prefix rule does not, so a prefix of "MySite" would otherwise be
        // refused by S3 with a message about DNS compliance that never mentions the prefix.
        => Assert.Equal("mysite-deploy-123456789012", AwsStaging.BucketFor("MySite", Account));

    [Fact]
    public async Task Staging_and_sweeping_agree_about_which_bucket_that_is()
    {
        // The convention was computed in one place and would have been swept from another. Two copies of a
        // rule is two rules — this is the check that they are one.
        var s3 = new FakeS3();
        using var target = Target(s3);
        var dir = Temp();
        var template = Path.Combine(dir, "site.template.json");
        await File.WriteAllTextAsync(template, "{}");

        var request = new DeploymentRequest("mysite",
            new DeploymentArtifact(new Dictionary<string, string> { ["t"] = template }),
            new Dictionary<string, string> { ["api.kind"] = AwsOptions.StackKind, ["api.template"] = "t" });

        await target.Driver.CreateAsync(new UnitContext(new ProcedureUnit("api", "API"), request));
        var staged = Assert.Single(s3.Created);

        await target.SweepAsync(new SweepContext("site", request));

        Assert.Equal([staged], s3.DeletedBuckets);
    }

    // ── the sweep ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_sweep_empties_the_bucket_before_deleting_it()
    {
        // S3 refuses to delete a bucket with anything in it, and a staging bucket always has something in
        // it. A bare DeleteBucket would fail on every teardown that ever happened.
        var bucket = AwsStaging.BucketFor("mysite", Account);
        var s3 = Staged(bucket, 3);
        using var target = Target(s3);

        await target.SweepAsync(Context());

        Assert.Equal(3, s3.Deleted.Count);
        Assert.Equal([bucket], s3.DeletedBuckets);
        Assert.DoesNotContain(bucket, s3.Buckets.Keys);
    }

    [Fact]
    public async Task A_sweep_of_a_bucket_that_is_not_there_is_not_an_error()
    {
        // A teardown is re-runnable, so the second one arrives with the first one's work done — and a
        // deployment that never staged anything never had a bucket at all.
        var s3 = new FakeS3();
        using var target = Target(s3);

        await target.SweepAsync(Context());
        await target.SweepAsync(Context());

        Assert.Empty(s3.DeletedBuckets);
    }

    [Fact]
    public async Task A_sweep_pages_through_a_bucket_bigger_than_one_listing()
    {
        // A listing returns a page at a time, and a deployment with per-build Lambda assets outgrows one.
        // Stopping after the first page would leave a bucket that could never be emptied and therefore
        // never deleted — and S3 refuses to delete a bucket with anything left in it.
        //
        // The count is what proves the paging: 25 objects behind a 10-key page can only all be deleted by a
        // caller that asked for the second and third. How they are BATCHED is `S3Objects`' business and is
        // pinned once, by the content unit's thousand-key test, now that both share it.
        var bucket = AwsStaging.BucketFor("mysite", Account);
        var s3 = Staged(bucket, 25);
        s3.PageSize = 10;
        using var target = Target(s3);

        await target.SweepAsync(Context());

        Assert.Equal(25, s3.Deleted.Count);
        Assert.Equal([bucket], s3.DeletedBuckets);
        Assert.DoesNotContain(bucket, s3.Buckets.Keys);
    }

    [Fact]
    public async Task A_sweep_touches_only_THIS_deployments_bucket()
    {
        // The dangerous failure, and the one no contract suite can see: a sweep scoped to the account rather
        // than to the prefix tears down a deployment nobody asked it to touch.
        var mine = AwsStaging.BucketFor("mysite-test", Account);
        var theirs = AwsStaging.BucketFor("mysite", Account);
        var s3 = Staged(mine, 2);
        s3.Buckets[theirs] = new Dictionary<string, long> { ["production.template.json"] = 40 };
        using var target = Target(s3);

        await target.SweepAsync(Context("mysite-test"));

        Assert.Equal([mine], s3.DeletedBuckets);
        Assert.Contains(theirs, s3.Buckets.Keys);
        Assert.Contains("production.template.json", s3.Buckets[theirs].Keys);
    }

    [Fact]
    public async Task A_sweep_says_which_bucket_it_is_removing()
    {
        // The operator's only handle on what a teardown did to infrastructure that was never in a plan.
        var bucket = AwsStaging.BucketFor("mysite", Account);
        var s3 = Staged(bucket, 1);
        using var target = Target(s3);

        var lines = new List<ProgressReport>();
        await target.SweepAsync(new SweepContext("site", Context().Request, lines.Add, CancellationToken.None));

        Assert.Contains(lines, l => l.Message.Contains(bucket) && l.Unit == "site");
    }

    private static string Temp()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tyanor-stg-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }
}
