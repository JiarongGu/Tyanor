using Tyanor.Engine;
using Tyanor.Engine.State;
using Xunit;

namespace Tyanor.Tests;

/// <summary>
/// Running the same deployment from more than one place — a laptop and a pipeline, two operators, a retry
/// job — is a supported situation, not a hazard to be locked out. What makes it safe is that it is
/// VISIBLE: the plan reports both what the provider is doing and what the shared history says anyone
/// claims to be doing, and the engine attaches to work in flight rather than competing with it.
///
/// <para><b>Scope: these cover CHECKING, not SYNCING</b> (see docs/DECISIONS.md D11). They assert that a
/// second machine can SEE what another recorded and behaves sensibly about it. They do not assert anything
/// about simultaneous writers — `FileRunHistory` is last-writer-wins with no cross-process lock, and making
/// concurrent writes safe is a property a real S3 or Postgres backend must add.</para>
///
/// <para>A file history in a temp directory stands in for shared state, exercised through the same
/// interface a remote backend would implement.</para>
/// </summary>
public sealed class CrossMachineTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tyanor-x-" + Guid.NewGuid().ToString("N"));

    private string SharedState => Path.Combine(_dir, "runs.json");

    private static readonly Procedure Site = new("site",
        [new ProcedureUnit("db", "Database"), new ProcedureUnit("api", "API")]);

    private static DeploymentRequest Request() =>
        new("acme", new DeploymentArtifact(new Dictionary<string, string>()));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>A second machine's view: its own runner, over the SAME history file.</summary>
    private ProcedureRunner Machine(FakeUnits units) =>
        new(new FakeTarget(units), new FileRunHistory(SharedState));

    [Fact]
    public async Task A_plan_sees_a_run_another_machine_started()
    {
        // Machine A starts and stalls (its process dies mid-run, leaving the record live).
        await new FileRunHistory(SharedState).UpsertAsync(
            new RunRecord("run-A", "site", "acme", RunKind.Apply, RunStatus.Running, DateTimeOffset.UnixEpoch));

        // Machine B plans. The provider looks idle, so without shared state B would see an empty field.
        var plan = await Machine(new FakeUnits()).PlanAsync(Site, Request());

        Assert.NotNull(plan.ActiveRun);
        Assert.Equal("run-A", plan.ActiveRun.Id);
        Assert.True(plan.HasStalledRun);          // recorded live, nothing converging → it stopped
        Assert.False(plan.InSync);                // and that disagreement is the point
        Assert.False(plan.IsNoOp);
    }

    [Fact]
    public async Task Applying_after_a_stall_CONTINUES_that_run_rather_than_opening_a_second()
    {
        // Two live records for one deployment is the out-of-sync state; adopting the id avoids creating it.
        await new FileRunHistory(SharedState).UpsertAsync(
            new RunRecord("run-A", "site", "acme", RunKind.Apply, RunStatus.Paused, DateTimeOffset.UnixEpoch,
                Reason: PauseReason.Credentials));

        await Machine(new FakeUnits()).ApplyAsync(Site, Request(), _ => { });

        var all = await new FileRunHistory(SharedState).RecentAsync();
        Assert.Single(all);
        Assert.Equal("run-A", all[0].Id);
        Assert.Equal(RunStatus.Succeeded, all[0].Status);
    }

    [Fact]
    public async Task A_plan_reports_work_genuinely_in_flight_and_calls_it_in_sync()
    {
        // The healthy concurrent case: another run is live AND the provider is converging. Applying is
        // safe — the engine attaches — and the operator should know they are watching someone else's work.
        await new FileRunHistory(SharedState).UpsertAsync(
            new RunRecord("run-A", "site", "acme", RunKind.Apply, RunStatus.Running, DateTimeOffset.UnixEpoch));

        var plan = await Machine(new FakeUnits { ["api"] = UnitPhase.Converging }).PlanAsync(Site, Request());

        Assert.True(plan.HasWorkInFlight);
        Assert.True(plan.InSync);                 // record and provider agree: something IS happening
        Assert.False(plan.HasStalledRun);
    }

    [Fact]
    public async Task With_nobody_else_here_a_settled_deployment_plans_as_no_op()
    {
        var plan = await Machine(new FakeUnits { ["db"] = UnitPhase.Ready, ["api"] = UnitPhase.Ready })
            .PlanAsync(Site, Request());

        Assert.Null(plan.ActiveRun);
        Assert.True(plan.InSync);
        Assert.False(plan.IsNoOp);                // Updates are still changes — only the provider knows
        Assert.Equal(2, plan.Changes.Count);
    }

    [Fact]
    public async Task Planning_never_writes_a_run_record()
    {
        // A plan that left a trace would itself create the out-of-sync state it exists to detect.
        await Machine(new FakeUnits()).PlanAsync(Site, Request());

        Assert.Empty(await new FileRunHistory(SharedState).RecentAsync());
    }

    // ── fakes ────────────────────────────────────────────────────────────────────────────────────
    private sealed class FakeUnits : Dictionary<string, UnitPhase>;

    private sealed class FakeTarget(FakeUnits phases) : IDeploymentTarget, IUnitDriver, IFailureClassifier
    {
        public string Id => "fake";
        public IUnitDriver Driver => this;
        public IFailureClassifier Classifier => this;
        public FailureClass? Classify(Exception error) => null;
        public Task<TargetIdentity> ValidateAsync(TargetCredentials? c, CancellationToken ct) => Task.FromResult(new TargetIdentity(true));

        public Task<UnitPhase> PhaseAsync(ProcedureUnit u, DeploymentRequest r, CancellationToken ct) =>
            Task.FromResult(phases.GetValueOrDefault(u.Name, UnitPhase.Missing));
        public Task CreateAsync(ProcedureUnit u, DeploymentRequest r, CancellationToken ct) => Task.CompletedTask;
        public Task<bool> UpdateAsync(ProcedureUnit u, DeploymentRequest r, CancellationToken ct) => Task.FromResult(true);
        public Task RemoveAsync(ProcedureUnit u, DeploymentRequest r, CancellationToken ct) => Task.CompletedTask;
        public Task AwaitSettledAsync(ProcedureUnit u, DeploymentRequest r, Action<ProgressReport> report, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<ResourceState>> RefreshAsync(ProcedureUnit u, DeploymentRequest r, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ResourceState>>([]);
    }
}
