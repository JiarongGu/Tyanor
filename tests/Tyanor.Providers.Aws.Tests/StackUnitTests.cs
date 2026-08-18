using Amazon.CloudFormation;
using Amazon.CloudFormation.Model;
using Amazon.S3.Model;
using Xunit;

namespace Tyanor.Providers.Aws.Tests;

/// <summary>
/// What the stack driver DOES, as opposed to what CloudFormation says.
///
/// <para>These were left unwritten on the grounds that mocking the SDK proves nothing (D14). That is true of
/// the vocabulary — whether <c>UPDATE_ROLLBACK_COMPLETE</c> really means what the phase table says — and it
/// is not true of anything below. Which request this provider builds, whether it re-issues against a stack
/// that is already gone, whether a throttle is mistaken for absence: all of that is this repository's logic
/// and its bugs, and a real deployment is a slow and expensive way to find them. See D23.</para>
///
/// <para>Every status string and error code here is a real one. The fake replays; it never invents.</para>
/// </summary>
public class StackUnitTests
{
    private static readonly ProcedureUnit Api = new("api", "API");

    private static readonly TimeSpan NoWait = TimeSpan.FromMilliseconds(1);

    private sealed record Rig(FakeCloudFormation Cfn, FakeS3 S3, FakeSts Sts, StackUnit Unit);

    private static Rig Build()
    {
        var cfn = new FakeCloudFormation();
        var s3 = new FakeS3();
        var sts = new FakeSts();
        return new Rig(cfn, s3, sts, new StackUnit(cfn, s3, new AwsAccount(sts), "ap-southeast-2", NoWait));
    }

    /// <summary>A template on disk, because the driver resolves a real artifact part before it calls AWS.</summary>
    private static (DeploymentRequest Request, string Dir) Request(
        Dictionary<string, string>? extra = null, Dictionary<string, string>? tags = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "tyanor-stack-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "api.template.json"), "{}");

        var options = new Dictionary<string, string>
        {
            ["api.kind"] = AwsOptions.StackKind,
            ["api.template"] = "template",
        };
        foreach (var (k, v) in extra ?? []) options[k] = v;

        return (new DeploymentRequest("mysite",
            new DeploymentArtifact(new Dictionary<string, string>
            {
                ["template"] = Path.Combine(dir, "api.template.json"),
                ["lambda"] = dir,
            }),
            options, tags), dir);
    }

    private static UnitContext Context(DeploymentRequest request, Action<ProgressReport>? report = null) =>
        new(Api, request, report ?? (_ => { }), CancellationToken.None);

    // ── creating ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_create_sends_the_stack_CloudFormation_was_asked_for()
    {
        var rig = Build();
        var (request, dir) = Request(new Dictionary<string, string> { ["api.parameter.MemorySize"] = "512" },
            new Dictionary<string, string> { ["owner"] = "platform" });

        try
        {
            await rig.Unit.CreateAsync(Context(request));

            var sent = rig.Cfn.Sent<CreateStackRequest>();
            Assert.Equal("mysite-api", sent.StackName);
            Assert.Equal(OnFailure.ROLLBACK, sent.OnFailure);   // so a failed create leaves its events readable
            Assert.Equal(["CAPABILITY_IAM", "CAPABILITY_NAMED_IAM"], sent.Capabilities);
            Assert.Equal("512", Assert.Single(sent.Parameters).ParameterValue);
            Assert.Equal("MemorySize", sent.Parameters[0].ParameterKey);
            Assert.Equal("owner", Assert.Single(sent.Tags).Key);
            Assert.Equal($"https://mysite-deploy-123456789012.s3.ap-southeast-2.amazonaws.com/mysite-api/api.template.json",
                sent.TemplateURL);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task A_create_does_NOT_wait()
    {
        // The engine owns the wait, so that attaching to someone else's in-flight operation uses the
        // identical one. A driver that waited here would make Attach and Create two different code paths.
        var rig = Build();
        var (request, dir) = Request();

        try
        {
            await rig.Unit.CreateAsync(Context(request));

            Assert.Equal(0, rig.Cfn.Count<DescribeStackEventsRequest>());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Assets_are_uploaded_under_the_names_the_template_refers_to()
    {
        // Renaming one here produces a stack that cannot find its own Lambda code.
        var rig = Build();
        var (request, dir) = Request(new Dictionary<string, string> { ["api.assets"] = "lambda" });
        Directory.CreateDirectory(Path.Combine(dir, "nested"));
        await File.WriteAllTextAsync(Path.Combine(dir, "nested", "handler.zip"), "zip");

        try
        {
            await rig.Unit.CreateAsync(Context(request));

            Assert.Contains("nested/handler.zip", rig.S3.Uploaded.Keys);          // forward slashes, even on Windows
            Assert.Contains("mysite-api/api.template.json", rig.S3.Uploaded.Keys);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task The_staging_bucket_is_per_account_and_lowercase()
    {
        // Per account so two operators never collide; derived rather than configured so there is nothing to
        // get wrong. Lowercased because S3 refuses an uppercase bucket name outright.
        var rig = Build();
        var (request, dir) = Request();

        try
        {
            await rig.Unit.CreateAsync(Context(request with { Prefix = "MySite" }));

            Assert.Equal("mysite-deploy-123456789012", Assert.Single(rig.S3.Created));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task The_account_is_asked_for_ONCE_however_many_units_stage()
    {
        // Three stacks would otherwise call STS six times to compute the same string.
        var rig = Build();
        var (request, dir) = Request();

        try
        {
            await rig.Unit.CreateAsync(Context(request));
            await rig.Unit.CreateAsync(Context(request));

            Assert.Equal(1, rig.Sts.Calls);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ── updating ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_update_with_nothing_to_change_reports_no_change_rather_than_failing()
    {
        // The path resume depends on: on a re-run every finished unit answers this way, and reading it as an
        // error would fail a run that had nothing left to do.
        var rig = Build();
        var (request, dir) = Request();
        rig.Cfn.Returning("CREATE_COMPLETE");
        rig.Cfn.UpdateThrows = new AmazonCloudFormationException("No updates are to be performed.")
        { ErrorCode = "ValidationError" };

        try
        {
            Assert.False(await rig.Unit.UpdateAsync(Context(request)));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task A_GENUINE_validation_error_is_not_swallowed_as_no_change()
    {
        // It shares its error code with "no updates are to be performed", so the match is on message text and
        // is deliberately narrow. Reading a real template error as "already up to date" would report success
        // over a deployment that never shipped — the worse mistake by a wide margin.
        var rig = Build();
        var (request, dir) = Request();
        rig.Cfn.Returning("CREATE_COMPLETE");
        rig.Cfn.UpdateThrows = new AmazonCloudFormationException("Template format error: unsupported structure")
        { ErrorCode = "ValidationError" };

        try
        {
            await Assert.ThrowsAsync<AmazonCloudFormationException>(() => rig.Unit.UpdateAsync(Context(request)));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ── the defect D14 says was fixed in the port ────────────────────────────────────────────────

    [Fact]
    public async Task A_THROTTLE_while_reading_the_phase_propagates_instead_of_reading_as_absent()
    {
        // The defect found in code that had run in production: every CloudFormation exception was read as
        // "the stack does not exist", so a throttle read as absent and the create that followed hit a stack
        // that was there all along. Only a ValidationError that actually says "does not exist" counts now,
        // and everything else propagates to be classified — so a throttle is retried as the transient error
        // it is. D14 states this; nothing checked it.
        var rig = Build();
        var (request, dir) = Request();
        rig.Cfn.DescribeThrows = new AmazonCloudFormationException("Rate exceeded") { ErrorCode = "Throttling" };

        try
        {
            var thrown = await Assert.ThrowsAsync<AmazonCloudFormationException>(
                () => rig.Unit.PhaseAsync(Context(request)));

            Assert.Equal("Throttling", thrown.ErrorCode);
            Assert.Equal(FailureClass.Transient, new AwsFailureClassifier().Classify(thrown));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task A_stack_that_really_does_not_exist_reads_as_Missing()
    {
        var rig = Build();
        var (request, dir) = Request();

        try
        {
            Assert.Equal(UnitPhase.Missing, await rig.Unit.PhaseAsync(Context(request)));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ── waiting ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_wait_polls_until_the_stack_settles()
    {
        var rig = Build();
        var (request, dir) = Request();
        rig.Cfn.Returning("CREATE_IN_PROGRESS", "CREATE_IN_PROGRESS", "CREATE_COMPLETE");

        try
        {
            await rig.Unit.AwaitSettledAsync(Context(request));

            Assert.Equal(3, rig.Cfn.StatusReads);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task A_wait_that_settles_BADLY_throws_and_names_the_FIRST_resource_that_failed()
    {
        // The first failure is the cause; everything after it failed only because it was rolled back. Naming
        // the last would send an operator to look at a resource that was fine.
        var rig = Build();
        var (request, dir) = Request();
        rig.Cfn.Returning("CREATE_IN_PROGRESS", "ROLLBACK_COMPLETE");
        rig.Cfn.Events.Add(Fake.Event("1", "Role", "CREATE_FAILED", "Role name already taken"));
        rig.Cfn.Events.Add(Fake.Event("2", "Function", "CREATE_FAILED", "Resource creation cancelled"));

        try
        {
            var thrown = await Assert.ThrowsAsync<AwsDeploymentException>(
                () => rig.Unit.AwaitSettledAsync(Context(request)));

            Assert.Contains("Role name already taken", thrown.Message);
            Assert.DoesNotContain("cancelled", thrown.Message);
            Assert.Contains("ROLLBACK_COMPLETE", thrown.Message);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task A_reverted_UPDATE_is_reported_as_a_failure_even_though_the_stack_is_usable()
    {
        // UPDATE_ROLLBACK_COMPLETE leaves a perfectly good stack — at the PREVIOUS configuration. The phase
        // table calls it Ready, correctly, and the wait must still call it a failure: a run that reported
        // success here would be telling an operator their change shipped when it did not.
        var rig = Build();
        var (request, dir) = Request();
        rig.Cfn.Returning("UPDATE_ROLLBACK_COMPLETE");

        try
        {
            await Assert.ThrowsAsync<AwsDeploymentException>(() => rig.Unit.AwaitSettledAsync(Context(request)));
            Assert.Equal(UnitPhase.Ready, CloudFormationPhases.Of("UPDATE_ROLLBACK_COMPLETE"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Each_resource_event_is_narrated_ONCE_however_many_times_it_is_polled()
    {
        // The events endpoint returns the whole history every poll. Without the seen-set an operator watching
        // a twenty-minute deploy would get the same lines over and over.
        var rig = Build();
        var (request, dir) = Request();
        rig.Cfn.Returning("CREATE_IN_PROGRESS", "CREATE_IN_PROGRESS", "CREATE_COMPLETE");
        rig.Cfn.Events.Add(Fake.Event("1", "Bucket", "CREATE_COMPLETE"));
        var seen = new List<ProgressReport>();

        try
        {
            await rig.Unit.AwaitSettledAsync(Context(request, seen.Add));

            Assert.Single(seen, r => r.Message.StartsWith("Bucket:", StringComparison.Ordinal));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task An_IN_PROGRESS_event_is_not_narrated_because_its_completion_says_the_same_thing()
    {
        var rig = Build();
        var (request, dir) = Request();
        rig.Cfn.Returning("CREATE_COMPLETE");
        rig.Cfn.Events.Add(Fake.Event("1", "Bucket", "CREATE_IN_PROGRESS"));
        var seen = new List<ProgressReport>();

        try
        {
            await rig.Unit.AwaitSettledAsync(Context(request, seen.Add));

            Assert.DoesNotContain(seen, r => r.Message.StartsWith("Bucket:", StringComparison.Ordinal));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task A_failed_resource_event_is_narrated_as_an_ERROR_with_its_reason()
    {
        var rig = Build();
        var (request, dir) = Request();
        rig.Cfn.Returning("CREATE_COMPLETE");
        rig.Cfn.Events.Add(Fake.Event("1", "Role", "CREATE_FAILED", "already exists"));
        var seen = new List<ProgressReport>();

        try
        {
            await rig.Unit.AwaitSettledAsync(Context(request, seen.Add));

            var line = Assert.Single(seen, r => r.Status == ProgressStatus.Error);
            Assert.Contains("already exists", line.Message);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ── removing ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Removing_a_stack_that_is_already_gone_issues_NOTHING()
    {
        // A teardown is resumed by running it again, so it meets units it already removed. Issuing a delete
        // against nothing is the difference between a re-runnable teardown and one that fails halfway.
        var rig = Build();
        var (request, dir) = Request();

        try
        {
            await rig.Unit.RemoveAsync(Context(request));

            Assert.Equal(0, rig.Cfn.Count<DeleteStackRequest>());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Removing_waits_until_the_stack_is_actually_gone()
    {
        // Removal is the one operation the engine cannot attach to halfway, so the driver owns the wait —
        // and returning early would let the next unit's teardown start against a stack still holding it.
        var rig = Build();
        var (request, dir) = Request();
        rig.Cfn.Returning("CREATE_COMPLETE", "DELETE_IN_PROGRESS", "DELETE_COMPLETE");

        try
        {
            await rig.Unit.RemoveAsync(Context(request));

            Assert.Equal("mysite-api", rig.Cfn.Sent<DeleteStackRequest>().StackName);
            Assert.True(rig.Cfn.StatusReads >= 3);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task A_delete_that_FAILS_says_so_rather_than_polling_for_ever()
    {
        var rig = Build();
        var (request, dir) = Request();
        rig.Cfn.Returning("CREATE_COMPLETE", "DELETE_FAILED");
        rig.Cfn.Events.Add(Fake.Event("1", "Bucket", "DELETE_FAILED", "The bucket is not empty"));

        try
        {
            var thrown = await Assert.ThrowsAsync<AwsDeploymentException>(() => rig.Unit.RemoveAsync(Context(request)));

            Assert.Contains("The bucket is not empty", thrown.Message);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ── refreshing ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_refresh_of_an_absent_stack_reports_nothing_rather_than_throwing()
    {
        // Absent is a fact, not a failure — otherwise a plan of a deployment that does not exist is impossible,
        // and that is the plan people most want.
        var rig = Build();
        var (request, dir) = Request();

        try
        {
            Assert.Empty(await rig.Unit.RefreshAsync(Context(request)));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task A_refresh_reports_what_the_stack_holds_and_skips_resources_with_no_physical_id()
    {
        // A resource mid-create has no physical id yet. Including it would put a null-keyed entry into state,
        // which the diff matches on.
        var rig = Build();
        var (request, dir) = Request();
        rig.Cfn.Returning("CREATE_COMPLETE");
        rig.Cfn.Resources.Add(new StackResource
        {
            PhysicalResourceId = "my-bucket", ResourceType = "AWS::S3::Bucket", ResourceStatus = "CREATE_COMPLETE",
        });
        rig.Cfn.Resources.Add(new StackResource { ResourceType = "AWS::Lambda::Function" });

        try
        {
            var resource = Assert.Single(await rig.Unit.RefreshAsync(Context(request)));

            Assert.Equal("my-bucket", resource.Id);
            Assert.Equal("AWS::S3::Bucket", resource.Type);
            Assert.Equal("CREATE_COMPLETE", resource.Fingerprint);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ── outputs ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Outputs_of_a_stack_that_is_not_deployed_are_empty_rather_than_an_error()
    {
        var rig = Build();
        var (request, dir) = Request();

        try
        {
            Assert.Empty(await rig.Unit.OutputsAsync(Context(request)));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Outputs_come_back_as_the_stack_exposes_them()
    {
        var rig = Build();
        var (request, dir) = Request();
        rig.Cfn.Returning("CREATE_COMPLETE");
        rig.Cfn.Outputs["websiteurl"] = "https://example.cloudfront.net";

        try
        {
            var outputs = await rig.Unit.OutputsAsync(Context(request));

            Assert.Equal("https://example.cloudfront.net", outputs["websiteurl"]);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
