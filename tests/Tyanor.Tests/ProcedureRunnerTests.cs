using Tyanor.Engine;
using Tyanor.Testing;
using Xunit;

namespace Tyanor.Tests;

/// <summary>
/// The engine, driven by a fake provider. These cover the behaviours that make a run resumable — and they
/// exist because each one fails SILENTLY in production: a re-issued operation looks like a slow deploy, a
/// credential error classified as terminal looks like a legitimate failure, and a skipped unit looks like
/// a fast one.
/// </summary>
public class ProcedureRunnerTests
{
    private static readonly Procedure Site = new("site",
    [
        new ProcedureUnit("db", "Database"),
        new ProcedureUnit("api", "API"),
        new ProcedureUnit("web", "Website"),
    ]);

    private static DeploymentRequest Request() =>
        new("acme", new DeploymentArtifact(new Dictionary<string, string> { ["infrastructure"] = "/tmp/x" }));

    [Fact]
    public async Task A_clean_run_creates_every_unit_in_order()
    {
        var target = new MemoryTarget();                     // everything Missing
        var runner = new ProcedureRunner(target, new TestHistory());

        var outcome = await runner.ApplyAsync(Site, Request(), _ => { });

        Assert.True(outcome.Ok);
        Assert.Equal(["db:create", "api:create", "web:create"], target.Calls);
    }

    [Fact]
    public async Task Resuming_skips_what_is_done_attaches_to_what_is_running_and_creates_the_rest()
    {
        // The exact shape of an interrupted run: one unit finished, one was mid-flight when the process
        // died, one never started. A single re-run has to do the right thing for all three.
        var target = new MemoryTarget { Phases = { ["api"] = UnitPhase.Converging } }
            .AlreadyDeployed("db");
        var runner = new ProcedureRunner(target, new TestHistory());

        var outcome = await runner.ApplyAsync(Site, Request(), _ => { });

        Assert.True(outcome.Ok);
        // db: an update that reports no change. api: WAITED ON, never re-issued. web: created.
        Assert.Equal(["db:update", "api:await", "web:create"], target.Calls);
        Assert.DoesNotContain("api:create", target.Calls);
        Assert.DoesNotContain("api:update", target.Calls);
    }

    [Fact]
    public async Task A_broken_unit_is_removed_and_remade()
    {
        var target = new MemoryTarget().Reports("db", UnitPhase.Broken);
        var runner = new ProcedureRunner(target, new TestHistory());

        await runner.ApplyAsync(new Procedure("one", [new ProcedureUnit("db", "Database")]), Request(), _ => { });

        Assert.Equal(["db:remove", "db:create"], target.Calls);
    }

    [Fact]
    public async Task An_expired_credential_pauses_the_run_and_keeps_it_live()
    {
        // The defining behaviour. A tool that fails here throws away everything already provisioned.
        var target = new MemoryTarget().Fails("api", FailureClass.Credentials);
        var history = new TestHistory();
        var runner = new ProcedureRunner(target, history);

        var outcome = await runner.ApplyAsync(Site, Request(), _ => { });

        Assert.False(outcome.Ok);
        Assert.True(outcome.Resumable);
        Assert.Equal("credentials", outcome.Reason?.Value);
        Assert.Equal(RunStatus.Paused, history.Last!.Status);
        Assert.True(history.Last.IsLive);                    // still protected from deletion
        Assert.Null(history.Last.FinishedAt);                // a pause has not finished
    }

    [Fact]
    public async Task A_hard_failure_fails_the_run_terminally()
    {
        var target = new MemoryTarget().Throws("api", new InvalidOperationException("malformed"));
        var history = new TestHistory();
        var runner = new ProcedureRunner(target, history);

        var outcome = await runner.ApplyAsync(Site, Request(), _ => { });

        Assert.False(outcome.Resumable);
        Assert.Equal(RunStatus.Failed, history.Last!.Status);
        Assert.NotNull(history.Last.FinishedAt);
    }

    [Fact]
    public async Task A_transient_error_is_retried_rather_than_surfaced()
    {
        var target = new MemoryTarget().FailsOnce("api");
        var runner = new ProcedureRunner(target, new TestHistory(),
            retry: new RetryPolicy(Attempts: 3, BaseDelay: TimeSpan.FromMilliseconds(1)));

        var outcome = await runner.ApplyAsync(Site, Request(), _ => { });

        Assert.True(outcome.Ok);
        Assert.Equal(2, target.Attempts["api"]);             // failed once, succeeded on the retry
    }

    [Fact]
    public async Task Teardown_runs_in_reverse_and_tolerates_what_is_already_gone()
    {
        // db + api are really there; web was already removed (a re-run of an interrupted teardown).
        var target = new MemoryTarget().AlreadyDeployed("db", "api");
        var runner = new ProcedureRunner(target, new TestHistory());

        var outcome = await runner.DestroyAsync(Site, Request(), _ => { });

        Assert.True(outcome.Ok);
        // web was already gone; edge-first order otherwise, so importers die before what they import.
        Assert.Equal(["api:remove", "db:remove"], target.Calls);
    }

    [Fact]
    public async Task A_resume_continues_the_SAME_run_rather_than_starting_another()
    {
        var history = new TestHistory();
        var runner = new ProcedureRunner(new MemoryTarget(), history);

        await runner.ApplyAsync(Site, Request(), _ => { }, runId: "run-1");

        Assert.All(history.All, r => Assert.Equal("run-1", r.Id));
    }

    [Fact]
    public async Task A_resume_keeps_the_moment_the_run_BEGAN()
    {
        // A resume continues a run, so the record has to keep describing the attempt rather than the retry.
        // Stamping "now" over StartedAt made one interrupted three-hour job report as however long its last
        // resume took — the same defect as giving a resume a fresh id, just in a different field.
        var began = DateTimeOffset.UtcNow.AddHours(-3);
        var history = new TestHistory
        {
            Live = new RunRecord("run-A", "site", "acme", RunKind.Apply, RunStatus.Paused, began,
                Reason: PauseReason.Credentials),
        };

        await new ProcedureRunner(new MemoryTarget(), history).ApplyAsync(Site, Request(), _ => { });

        Assert.All(history.All, r => Assert.Equal("run-A", r.Id));
        Assert.All(history.All, r => Assert.Equal(began, r.StartedAt));
    }

    [Fact]
    public async Task A_run_that_is_genuinely_NEW_starts_now()
    {
        var history = new TestHistory();                     // nothing live to adopt

        await new ProcedureRunner(new MemoryTarget(), history).ApplyAsync(Site, Request(), _ => { });

        Assert.All(history.All, r => Assert.InRange(
            r.StartedAt, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1)));
    }

    [Theory]
    [InlineData(FailureClass.Credentials, "credentials")]
    [InlineData(FailureClass.Transient, "temporary")]
    public async Task A_pause_TELLS_the_operator_the_work_is_kept(FailureClass failure, string word)
    {
        // The sentence a pause is for. `error-classification.md` names this method as the reference for it,
        // so the wording is a behaviour rather than a detail.
        var target = new MemoryTarget().Fails("db", failure);
        var seen = new List<ProgressReport>();

        await new ProcedureRunner(target, new TestHistory(),
                retry: new RetryPolicy(Attempts: 1)).ApplyAsync(Site, Request(), seen.Add);

        var line = Assert.Single(seen, r => r.Status == ProgressStatus.Error);
        Assert.Contains(word, line.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("resume", line.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("kept", line.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("failed", line.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_terminal_failure_says_so_rather_than_offering_a_resume()
    {
        var target = new MemoryTarget().Throws("db", new InvalidOperationException("malformed template"));
        var seen = new List<ProgressReport>();

        await new ProcedureRunner(target, new TestHistory()).ApplyAsync(Site, Request(), seen.Add);

        var line = Assert.Single(seen, r => r.Status == ProgressStatus.Error);
        Assert.Contains("failed", line.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("kept", line.Message, StringComparison.OrdinalIgnoreCase);
    }
}
