using Tyanor.Engine;
using Tyanor.Engine.State;
using Xunit;

namespace Tyanor.Providers.Aws.Tests;

/// <summary>
/// The one test that touches a real cloud, and therefore the one that is off by default.
///
/// <para><b>Why it is gated rather than mocked.</b> Everything a mock could verify here is already verified
/// without one — the phase table and the classifier are pure functions tested against real strings. What is
/// left is whether the SDK calls are wired correctly, and a mock of the SDK answers that question by
/// agreeing with whatever this code believes. So the honest options are a real deployment or nothing, and
/// nothing is the default because an ordinary <c>npm run doctor</c> must never spend money.</para>
///
/// <para>To run it:</para>
/// <code>
/// TYANOR_LIVE_AWS=1
/// TYANOR_LIVE_AWS_KEY=AKIA…
/// TYANOR_LIVE_AWS_SECRET=…
/// TYANOR_LIVE_AWS_REGION=ap-southeast-2
/// </code>
/// <para>It deploys one stack containing a single SSM parameter — a resource that is free, creates in
/// seconds, and deletes cleanly — then plans, re-applies, refreshes and tears down.</para>
/// </summary>
public class AwsLiveDeploymentTests
{
    // A stack with nothing billable in it. An S3 bucket would also be free but leaves a name reserved
    // globally; a parameter leaves nothing behind at all.
    private const string Template = """
        {
          "AWSTemplateFormatVersion": "2010-09-09",
          "Description": "Tyanor live provider test. Safe to delete.",
          "Resources": {
            "Marker": {
              "Type": "AWS::SSM::Parameter",
              "Properties": { "Type": "String", "Value": "tyanor-live-test" }
            }
          },
          "Outputs": {
            "markername": { "Value": { "Ref": "Marker" } }
          }
        }
        """;

    [Fact]
    public async Task A_real_stack_is_planned_created_re_applied_refreshed_and_torn_down()
    {
        if (Environment.GetEnvironmentVariable("TYANOR_LIVE_AWS") is null or "") return;   // vacuous pass

        // Gate on but credentials missing is a FAILURE, not a skip. Silently doing nothing when someone
        // deliberately switched this on is how a live test comes to be believed without ever having run.
        var credentials = new TargetCredentials(
            Required("TYANOR_LIVE_AWS_KEY"), Required("TYANOR_LIVE_AWS_SECRET"), Required("TYANOR_LIVE_AWS_REGION"));

        var work = Path.Combine(Path.GetTempPath(), "tyanor-live-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        var templatePath = Path.Combine(work, "marker.template.json");
        await File.WriteAllTextAsync(templatePath, Template);

        // A prefix nobody else's deployment could collide with, so a leftover from a failed run is obvious.
        var prefix = "tyanor-live-" + Guid.NewGuid().ToString("N")[..8];
        var procedure = new Procedure("live", [new ProcedureUnit("marker", "Marker")]);
        var request = new DeploymentRequest(prefix,
            new DeploymentArtifact(new Dictionary<string, string> { ["template"] = templatePath }),
            new Dictionary<string, string>
            {
                ["marker.kind"] = AwsOptions.StackKind,
                ["marker.template"] = "template",
                ["marker.capabilities"] = "",           // this stack creates no IAM
            },
            new Dictionary<string, string> { ["tyanor:test"] = "live" });

        using var target = new AwsTarget(credentials);
        var state = new FileStateStore(Path.Combine(work, "state.json"));
        var runner = new ProcedureRunner(target, new FileRunHistory(Path.Combine(work, "runs.json")), state);

        try
        {
            var identity = await target.ValidateAsync(null, CancellationToken.None);
            Assert.True(identity.Ok, identity.Error);
            Assert.NotNull(identity.Account);

            var before = await runner.PlanAsync(procedure, request);
            Assert.Equal(ReconcileAction.Create, Assert.Single(before.Steps).Action);

            Assert.True((await runner.ApplyAsync(procedure, request)).Ok);

            // State now records what CloudFormation says the stack holds.
            Assert.Single((await state.GetAsync("live", prefix)).For("marker"));

            // A second apply is the no-op case, and it is the one resume depends on: CloudFormation reports
            // "No updates are to be performed", which must read as success rather than as an error.
            var after = await runner.PlanAsync(procedure, request);
            Assert.Equal(ReconcileAction.Update, Assert.Single(after.Steps).Action);
            Assert.False(after.HasDrift);
            Assert.True((await runner.ApplyAsync(procedure, request)).Ok);
        }
        finally
        {
            // Always, even on failure: a leaked stack is a leaked bill, and this one is easy to forget.
            await runner.RemoveAsync(procedure, request);
            try { Directory.Delete(work, recursive: true); } catch (IOException) { /* temp */ }
        }

        Assert.Equal(UnitPhase.Missing,
            await target.Driver.PhaseAsync(new ProcedureUnit("marker", "Marker"), request, CancellationToken.None));
        Assert.Empty((await state.GetAsync("live", prefix)).Units);
    }

    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException(
            $"TYANOR_LIVE_AWS is set, so {name} must be too — otherwise this test passes without running.");
}
