using Tyanor.Engine;
using Tyanor.Engine.State;
using Tyanor.Testing;
using Xunit;

namespace Tyanor.Tests;

/// <summary>
/// A unit that pauses the run on purpose — waiting on a person, a DNS record, an approval.
///
/// <para><b>This closes a capability that was documented and unreachable.</b> <see cref="PauseReason"/> has
/// always said a provider or a procedure may introduce its own reason, and nothing could: the engine
/// produced <c>credentials</c> and <c>transient</c> from the three failure classes, and <c>external</c> only
/// when the caller cancelled. So the one shape a pipeline needs most — <i>stop here until somebody says
/// yes</i> — had no way to exist, and the deferred ACM/Route 53 unit, whose whole model is "manual DNS is a
/// pause that resumes", could not have been written outside this repository either.</para>
///
/// <para>The distinction being pinned below is that a pause is <b>not an error</b>. Nothing went wrong, the
/// work already done is correct, and the only thing missing is a person or the passage of time — so it is
/// not classified, not retried, and not turned into wording of the engine's own.</para>
/// </summary>
public class PausedUnitTests
{
    private static readonly PauseReason Approval = new("approval");

    private static readonly Procedure Site = new("site",
    [
        new ProcedureUnit("db", "Database"),
        new ProcedureUnit("gate", "Approval"),
        new ProcedureUnit("web", "Website"),
    ]);

    /// <summary>
    /// A unit that waits for somebody, then stops waiting once they have acted — and the shape a manual
    /// approval gate actually is. Two methods, via <see cref="StepUnitDriver"/>: the four a step never needs
    /// are what that base class exists to stop everyone writing.
    /// </summary>
    private sealed class ApprovalUnit : StepUnitDriver
    {
        public bool Approved { get; set; }

        public int Attempts { get; private set; }

        public override Task<UnitPhase> PhaseAsync(UnitContext context) =>
            Task.FromResult(Approved ? UnitPhase.Ready : UnitPhase.Missing);

        public override Task CreateAsync(UnitContext context)
        {
            Attempts++;
            if (!Approved)
                throw new UnitPausedException(Approval,
                    $"{context.Label}: waiting for someone to approve this release. Resume once they have.");

            return Task.CompletedTask;
        }
    }

    private static (ProcedureRunner Runner, MemoryTarget Target, ApprovalUnit Gate, IRunHistory History) Rig(
        int attempts = 5)
    {
        var gate = new ApprovalUnit();
        var target = new MemoryTarget(new CustomUnits { ["approval"] = gate });
        var history = new InMemoryRunHistory();

        return (new ProcedureRunner(target, history, null,
                new RetryPolicy(attempts, BaseDelay: TimeSpan.FromMilliseconds(1))),
            target, gate, history);
    }

    private static DeploymentRequest Request() =>
        new("acme", new DeploymentArtifact(new Dictionary<string, string>()),
            new Dictionary<string, string> { ["gate.kind"] = "approval" });

    [Fact]
    public async Task A_paused_unit_stops_the_run_RESUMABLY()
    {
        var (runner, _, _, _) = Rig();

        var outcome = await runner.ApplyAsync(Site, Request());

        Assert.False(outcome.Ok);
        Assert.True(outcome.Resumable);          // the whole point — a Resume button, not a failure screen
        Assert.Equal(Approval, outcome.Reason);
    }

    [Fact]
    public async Task The_driver_s_own_message_reaches_the_operator_unchanged()
    {
        // The message IS the instruction: "add these DNS records", "someone has to approve this". An engine
        // that replaced it with wording of its own would leave a pause nobody can act on, which is a stop
        // with extra steps.
        var (runner, _, _, _) = Rig();

        var outcome = await runner.ApplyAsync(Site, Request());

        Assert.Contains("waiting for someone to approve", outcome.Error);
    }

    [Fact]
    public async Task It_is_never_retried()
    {
        // A pause is not a transient error. Asking a person to approve something five times in four seconds
        // is worse than not asking — and this must hold whatever a provider's classifier makes of the type,
        // which is why the engine excludes it rather than trusting the classifier.
        var (runner, _, gate, _) = Rig(attempts: 5);

        await runner.ApplyAsync(Site, Request());

        Assert.Equal(1, gate.Attempts);
    }

    [Fact]
    public async Task Units_after_the_pause_do_not_run_and_the_ones_before_it_are_kept()
    {
        var (runner, target, _, _) = Rig();

        await runner.ApplyAsync(Site, Request());

        Assert.Equal(["db"], target.Deployed);        // db went; web did not
    }

    [Fact]
    public async Task The_run_stays_LIVE_with_the_reason_recorded()
    {
        // A pause is an open run — it is the operator's handle on work that is genuinely unfinished, and the
        // record has to say WHY or they cannot tell a deliberate wait from a process that died.
        var (runner, _, _, history) = Rig();

        await runner.ApplyAsync(Site, Request());

        var live = await history.LiveAsync("site", "acme");
        Assert.NotNull(live);
        Assert.Equal(RunStatus.Paused, live.Status);
        Assert.Equal(Approval, live.Reason);
        Assert.Null(live.FinishedAt);
    }

    [Fact]
    public async Task Resuming_after_the_person_acted_finishes_the_SAME_run()
    {
        // Applying again is the resume, exactly as it is for every other pause. Nothing about a
        // driver-initiated one needs a second code path — which is the property that makes this cheap.
        var (runner, target, gate, history) = Rig();

        var paused = await runner.ApplyAsync(Site, Request());
        var id = (await history.LiveAsync("site", "acme"))!.Id;

        gate.Approved = true;                          // somebody clicked approve
        var finished = await runner.ApplyAsync(Site, Request());

        Assert.True(paused.Resumable);
        Assert.True(finished.Ok);
        Assert.Equal(["db", "web"], target.Deployed);
        Assert.Null(await history.LiveAsync("site", "acme"));

        var runs = await history.RecentAsync();
        Assert.Single(runs);                           // one interrupted job, not two records
        Assert.Equal(id, runs[0].Id);
        Assert.Equal(RunStatus.Succeeded, runs[0].Status);
    }

    [Fact]
    public async Task An_unknown_reason_still_reads_as_a_pause_to_the_operator()
    {
        // PauseReason is open, so the engine's explanation has to keep the promise the CLASS makes rather
        // than telling somebody their intact deployment failed just because the table has no row for it.
        var (runner, _, _, _) = Rig();
        var said = new List<string>();

        await runner.ApplyAsync(Site, Request(), report: p => said.Add(p.Message));

        Assert.Contains(said, m => m.Contains("waiting for someone to approve"));
    }

    [Fact]
    public async Task A_pause_during_a_teardown_pauses_the_teardown()
    {
        // Nothing about the direction changes what a pause means. A unit that needs a person to detach
        // something before it can go should be able to say so.
        var gate = new ApprovalUnit { Approved = true };
        var target = new MemoryTarget(new CustomUnits { ["approval"] = gate });
        var runner = new ProcedureRunner(target, new InMemoryRunHistory());

        await runner.ApplyAsync(Site, Request());

        var removing = new MemoryTarget(new CustomUnits { ["approval"] = new PausingRemoval() });
        var teardown = new ProcedureRunner(removing, new InMemoryRunHistory());
        await teardown.ApplyAsync(Site, Request());

        var outcome = await teardown.DestroyAsync(Site, Request());

        Assert.True(outcome.Resumable);
        Assert.Equal(Approval, outcome.Reason);
    }

    /// <summary>
    /// A unit that will not be removed until somebody detaches it by hand — a latch, so it overrides the
    /// remove, which is the one pairing <see cref="StepUnitDriver"/> asks you to get right.
    /// </summary>
    private sealed class PausingRemoval : StepUnitDriver
    {
        private bool _made;

        public override Task<UnitPhase> PhaseAsync(UnitContext context) =>
            Task.FromResult(_made ? UnitPhase.Ready : UnitPhase.Missing);

        public override Task CreateAsync(UnitContext context)
        {
            _made = true;
            return Task.CompletedTask;
        }

        public override Task RemoveAsync(UnitContext context) =>
            throw new UnitPausedException(Approval, "Detach the volume by hand, then resume the teardown.");
    }
}
