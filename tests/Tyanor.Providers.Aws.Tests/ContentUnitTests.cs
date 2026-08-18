using Xunit;

namespace Tyanor.Providers.Aws.Tests;

/// <summary>
/// What the content driver DOES — the website half of the AWS provider, and the one with no control plane.
///
/// <para>An S3 sync is converged only by the process doing it, so everything here happens inside
/// <c>CreateAsync</c> and <c>UpdateAsync</c> rather than in a wait. That makes it exactly the shape a fake
/// can check completely: which objects went up, under which keys, with which content types, whether the CDN
/// was cleared, and whether a redeploy of an unchanged build correctly did nothing.</para>
/// </summary>
public class ContentUnitTests
{
    private static readonly ProcedureUnit Web = new("content", "Website files");

    private sealed record Rig(FakeCloudFormation Cfn, FakeS3 S3, FakeCloudFront Cdn, ContentUnit Unit, string Dir)
        : IDisposable
    {
        public void Dispose()
        {
            try { Directory.Delete(Dir, recursive: true); } catch (IOException) { /* temp */ }
        }
    }

    private static Rig Build(params (string Name, string Body)[] files)
    {
        var cfn = new FakeCloudFormation();
        var s3 = new FakeS3();
        var cdn = new FakeCloudFront();
        var stacks = new StackUnit(cfn, s3, new AwsAccount(new FakeSts()), "ap-southeast-2",
            TimeSpan.FromMilliseconds(1));

        var dir = Path.Combine(Path.GetTempPath(), "tyanor-content-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        foreach (var (name, body) in files)
        {
            var path = Path.Combine(dir, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, body);
        }

        return new Rig(cfn, s3, cdn, new ContentUnit(s3, cdn, stacks), dir);
    }

    private static UnitContext Context(Rig rig, Dictionary<string, string> options, Action<ProgressReport>? report = null)
    {
        options["content.kind"] = AwsOptions.ContentKind;
        options["content.source"] = "site";

        var request = new DeploymentRequest("mysite",
            new DeploymentArtifact(new Dictionary<string, string> { ["site"] = rig.Dir }), options);

        return new UnitContext(Web, request, report ?? (_ => { }), CancellationToken.None);
    }

    private static Dictionary<string, string> ToBucket(string bucket) =>
        new() { ["content.bucket"] = bucket };

    // ── phase ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task No_bucket_yet_is_Missing_because_the_stack_that_makes_it_is_not_deployed()
    {
        using var rig = Build(("index.html", "<h1>hi</h1>"));

        Assert.Equal(UnitPhase.Missing, await rig.Unit.PhaseAsync(Context(rig, ToBucket("site-bucket"))));
    }

    [Fact]
    public async Task A_bucket_with_content_is_Ready_even_when_that_content_is_stale()
    {
        // Deliberately not "Missing when a file differs", which was the first shape of this and made the plan
        // lie: Create renders as "create (nothing there now)", and reading that about a website that is up and
        // serving is worse than saying nothing. Whether the files are current is what UpdateAsync is for.
        using var rig = Build(("index.html", "new"));
        rig.S3.Buckets["site-bucket"] = new Dictionary<string, long> { ["index.html"] = 999 };

        Assert.Equal(UnitPhase.Ready, await rig.Unit.PhaseAsync(Context(rig, ToBucket("site-bucket"))));
    }

    [Fact]
    public async Task An_EMPTY_bucket_is_Missing_because_this_unit_has_deployed_nothing_into_it()
    {
        // The bucket belongs to the STACK that made it, not to this unit, and it outlives this unit's
        // teardown by design. Reading "the bucket exists" as "this unit is deployed" meant a destroyed
        // content unit still claimed to be there. UnitDriverContract found it.
        using var rig = Build(("index.html", "hi"));
        rig.S3.Buckets["site-bucket"] = [];                  // the stack made it; nothing uploaded yet

        Assert.Equal(UnitPhase.Missing, await rig.Unit.PhaseAsync(Context(rig, ToBucket("site-bucket"))));
    }

    [Fact]
    public async Task An_EMPTY_bucket_owns_nothing_so_a_destroyed_unit_stops_claiming_a_resource()
    {
        // The other half of the same defect, and the more expensive one: state is what answers "did we
        // create this?", so a phantom resource for an emptied bucket is a teardown that never finishes.
        using var rig = Build();
        rig.S3.Buckets["site-bucket"] = [];

        Assert.Empty(await rig.Unit.RefreshAsync(Context(rig, ToBucket("site-bucket"))));
    }

    [Fact]
    public async Task A_destroyed_content_unit_reads_as_gone_and_stays_gone()
    {
        // End to end over the pair, because that is the sequence the defect broke.
        using var rig = Build(("index.html", "hi"));
        rig.S3.Buckets["site-bucket"] = [];
        var context = Context(rig, ToBucket("site-bucket"));

        await rig.Unit.CreateAsync(context);
        Assert.Equal(UnitPhase.Ready, await rig.Unit.PhaseAsync(context));

        await rig.Unit.RemoveAsync(context);

        Assert.Equal(UnitPhase.Missing, await rig.Unit.PhaseAsync(context));
        Assert.Empty(await rig.Unit.RefreshAsync(context));
        Assert.True(rig.S3.Buckets.ContainsKey("site-bucket"));   // …and the stack's bucket is still there
    }

    [Fact]
    public async Task Uploading_to_a_bucket_that_does_not_exist_names_the_STACK_rather_than_blaming_S3()
    {
        // Reached by narrowing a run to the content unit before its stack has ever been deployed. The raw
        // SDK error says only "the specified bucket does not exist", which sends an operator to look at S3
        // instead of at their procedure.
        using var rig = Build(("index.html", "hi"));         // no bucket in the fake at all

        var thrown = await Assert.ThrowsAsync<AwsConfigurationException>(
            () => rig.Unit.CreateAsync(Context(rig, ToBucket("site-bucket"))));

        Assert.Contains("does not create it", thrown.Message);
        Assert.Contains("site-bucket", thrown.Message);
        Assert.IsAssignableFrom<DefinitionException>(thrown);
    }

    // ── syncing ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_sync_uploads_every_file_with_forward_slash_keys_and_a_real_content_type()
    {
        // S3 defaults every upload to application/octet-stream, which makes a browser DOWNLOAD a page rather
        // than render it. A site uploaded without this does not work at all.
        using var rig = Build(("index.html", "<h1>hi</h1>"), ("assets/app.js", "console.log(1)"), ("assets/app.css", "a{}"));
        rig.S3.Buckets["site-bucket"] = [];

        await rig.Unit.CreateAsync(Context(rig, ToBucket("site-bucket")));

        Assert.Equal("text/html", rig.S3.Uploaded["index.html"]);
        Assert.Equal("application/javascript", rig.S3.Uploaded["assets/app.js"]);
        Assert.Equal("text/css", rig.S3.Uploaded["assets/app.css"]);
    }

    [Fact]
    public async Task A_sync_narrates_its_progress_through_THIS_unit()
    {
        // A website is hundreds of small files and this is the slow part of the run. The engine cannot narrate
        // it — the work is inside a create, where a provider with a control plane would have nothing to do.
        using var rig = Build([.. Enumerable.Range(0, 30).Select(i => ($"file{i}.txt", "x"))]);
        rig.S3.Buckets["site-bucket"] = [];
        var seen = new List<ProgressReport>();

        await rig.Unit.CreateAsync(Context(rig, ToBucket("site-bucket"), seen.Add));

        Assert.NotEmpty(seen);
        Assert.All(seen.Where(p => p.Percent >= 0), p => Assert.InRange(p.Percent, 0, 100));
        Assert.Equal(100, seen.Last(p => p.Percent >= 0).Percent);
    }

    [Fact]
    public async Task Re_deploying_an_UNCHANGED_build_uploads_nothing()
    {
        // What stops a redeploy invalidating a CDN for nothing. Files are compared by name and SIZE — never by
        // content, because comparing bodies means downloading them and the operator asked for a deployment
        // rather than a bandwidth bill.
        using var rig = Build(("index.html", "<h1>hi</h1>"));
        rig.S3.Buckets["site-bucket"] = new Dictionary<string, long>
        {
            ["index.html"] = new FileInfo(Path.Combine(rig.Dir, "index.html")).Length,
        };

        Assert.False(await rig.Unit.UpdateAsync(Context(rig, ToBucket("site-bucket"))));
        Assert.Empty(rig.S3.Uploaded);
    }

    [Fact]
    public async Task A_build_whose_SIZE_moved_is_re_uploaded()
    {
        using var rig = Build(("index.html", "<h1>a much longer page than before</h1>"));
        rig.S3.Buckets["site-bucket"] = new Dictionary<string, long> { ["index.html"] = 4 };

        Assert.True(await rig.Unit.UpdateAsync(Context(rig, ToBucket("site-bucket"))));
        Assert.Contains("index.html", rig.S3.Uploaded.Keys);
    }

    [Fact]
    public async Task A_NEW_file_is_noticed_even_when_every_existing_one_matches()
    {
        using var rig = Build(("index.html", "hi"), ("about.html", "hi"));
        rig.S3.Buckets["site-bucket"] = new Dictionary<string, long> { ["index.html"] = 2 };

        Assert.True(await rig.Unit.UpdateAsync(Context(rig, ToBucket("site-bucket"))));
    }

    // ── the bucket comes from another unit ───────────────────────────────────────────────────────

    [Fact]
    public async Task The_bucket_is_read_out_of_the_stack_that_created_it()
    {
        // The dependency is expressed by declaring the stack FIRST and never by an edge — units-not-graphs
        // working on the provider it was written for.
        using var rig = Build(("index.html", "hi"));
        rig.Cfn.Returning("CREATE_COMPLETE");
        rig.Cfn.Outputs["webbucketname"] = "made-by-the-stack";
        rig.S3.Buckets["made-by-the-stack"] = [];

        await rig.Unit.CreateAsync(Context(rig, new Dictionary<string, string>
        {
            ["content.bucketFrom"] = "web:webbucketname",
        }));

        Assert.Contains("index.html", rig.S3.Uploaded.Keys);
        Assert.Contains("mysite-web", rig.Cfn.Sent<Amazon.CloudFormation.Model.DescribeStacksRequest>().StackName);
    }

    [Fact]
    public async Task A_bucketFrom_naming_a_stack_that_is_not_deployed_yet_is_Missing_rather_than_an_error()
    {
        // A legitimate answer during a plan of a deployment that does not exist.
        using var rig = Build(("index.html", "hi"));

        Assert.Equal(UnitPhase.Missing, await rig.Unit.PhaseAsync(Context(rig, new Dictionary<string, string>
        {
            ["content.bucketFrom"] = "web:webbucketname",
        })));
    }

    [Fact]
    public async Task A_sync_with_no_destination_bucket_at_all_is_a_DEFINITION_error()
    {
        using var rig = Build(("index.html", "hi"));

        var thrown = await Assert.ThrowsAsync<AwsConfigurationException>(
            () => rig.Unit.CreateAsync(Context(rig, [])));

        Assert.IsAssignableFrom<DefinitionException>(thrown);
    }

    // ── the CDN ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_CDN_is_cleared_after_a_sync_when_a_distribution_was_named()
    {
        // Without this the files are up and the CDN keeps serving the old ones until they expire, which looks
        // exactly like a deployment that silently did nothing.
        using var rig = Build(("index.html", "hi"));
        rig.Cfn.Returning("CREATE_COMPLETE");
        rig.Cfn.Outputs["distributionid"] = "E123ABC";
        rig.S3.Buckets["site-bucket"] = [];

        await rig.Unit.CreateAsync(Context(rig, new Dictionary<string, string>
        {
            ["content.bucket"] = "site-bucket",
            ["content.invalidateFrom"] = "web:distributionid",
        }));

        Assert.Equal("E123ABC", Assert.Single(rig.Cdn.Invalidated));
    }

    [Fact]
    public async Task No_distribution_named_means_no_invalidation_rather_than_a_guess()
    {
        using var rig = Build(("index.html", "hi"));
        rig.S3.Buckets["site-bucket"] = [];

        await rig.Unit.CreateAsync(Context(rig, ToBucket("site-bucket")));

        Assert.Empty(rig.Cdn.Invalidated);
    }

    [Fact]
    public async Task Two_syncs_use_DIFFERENT_caller_references_because_CloudFront_rejects_a_repeat()
    {
        using var rig = Build(("index.html", "hi"));
        rig.Cfn.Returning("CREATE_COMPLETE");
        rig.Cfn.Outputs["distributionid"] = "E123ABC";
        rig.S3.Buckets["site-bucket"] = [];
        var options = new Dictionary<string, string>
        {
            ["content.bucket"] = "site-bucket",
            ["content.invalidateFrom"] = "web:distributionid",
        };

        await rig.Unit.CreateAsync(Context(rig, options));
        await rig.Unit.CreateAsync(Context(rig, options));

        Assert.Equal(2, rig.Cdn.CallerReferences.Distinct().Count());
    }

    // ── removing ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Removing_empties_the_bucket_and_does_NOT_delete_it()
    {
        // The bucket belongs to the stack that created it, and that stack's own removal takes it. A unit that
        // deleted another unit's resource would break reverse-order teardown by reaching sideways.
        using var rig = Build();
        rig.S3.Buckets["site-bucket"] = new Dictionary<string, long> { ["index.html"] = 2, ["app.js"] = 5 };

        await rig.Unit.RemoveAsync(Context(rig, ToBucket("site-bucket")));

        Assert.Equal(["index.html", "app.js"], rig.S3.Deleted);
        Assert.True(rig.S3.Buckets.ContainsKey("site-bucket"));      // emptied, not removed
    }

    [Fact]
    public async Task Removing_batches_deletions_because_S3_takes_at_most_a_thousand_at_a_time()
    {
        using var rig = Build();
        rig.S3.Buckets["site-bucket"] = Enumerable.Range(0, 2_500).ToDictionary(i => $"f{i}", _ => 1L);

        await rig.Unit.RemoveAsync(Context(rig, ToBucket("site-bucket")));

        Assert.Equal(3, rig.S3.DeleteBatches);                       // 1000 + 1000 + 500
        Assert.Equal(2_500, rig.S3.Deleted.Count);
    }

    [Fact]
    public async Task Removing_a_bucket_that_is_not_there_does_not_throw()
    {
        // Teardown must be re-runnable: an interrupted one is resumed by running it again.
        using var rig = Build();

        await rig.Unit.RemoveAsync(Context(rig, ToBucket("never-created")));

        Assert.Empty(rig.S3.Deleted);
    }

    // ── refresh ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_refresh_fingerprints_the_bucket_by_count_and_total_size()
    {
        // The honest trade: it catches a website that lost files or was replaced, and it does not catch an
        // edit that happens to preserve the byte count.
        using var rig = Build();
        rig.S3.Buckets["site-bucket"] = new Dictionary<string, long> { ["a"] = 10, ["b"] = 32 };

        var resource = Assert.Single(await rig.Unit.RefreshAsync(Context(rig, ToBucket("site-bucket"))));

        Assert.Equal("s3://site-bucket", resource.Id);
        Assert.Equal("AWS::S3::Bucket", resource.Type);
        Assert.Equal("2 objects, 42 bytes", resource.Fingerprint);
    }

    [Fact]
    public async Task A_refresh_of_a_bucket_that_does_not_exist_reports_nothing()
    {
        using var rig = Build();

        Assert.Empty(await rig.Unit.RefreshAsync(Context(rig, ToBucket("never-created"))));
    }
}
