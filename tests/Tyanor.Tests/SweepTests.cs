using Tyanor.Engine;
using Tyanor.Engine.State;
using Tyanor.Testing;
using Tyanor.Tests.Support;
using Xunit;

namespace Tyanor.Tests;

/// <summary>
/// What a provider creates for ITSELF, and the one moment it can be taken away again.
///
/// <para><b>The gap this closes was structural rather than a missing call, which is why it needed a seam.</b>
/// Both shipped providers keep infrastructure per deployment that belongs to no unit — the AWS one a staging
/// bucket every stack uploads through, the local one a folder of pid files beside the units. Neither could be
/// removed by a unit: a unit deleting it would be reaching sideways to take away what the units either side
/// of it still need, which is exactly what removing in reverse order exists to prevent. So nothing removed
/// either, a destroy left them standing for ever, and <c>adoption.md</c> claimed a teardown left nothing.
/// See <c>docs/DECISIONS.md</c> D33.</para>
///
/// <para><b>Three of the checks below are about when a sweep must NOT happen</b>, and that is the balance of
/// risk: a sweep that does not run leaves a bucket, and a sweep that runs at the wrong moment removes the
/// scaffolding out from under a deployment that is still there.</para>
/// </summary>
public class SweepTests
{
    private static readonly Procedure Site = new("site",
    [
        new ProcedureUnit("db", "Database"),
        new ProcedureUnit("web", "Website"),
    ]);

    /// <summary>
    /// A target that records its sweeps, and can be told to fail one.
    /// </summary>
    /// <remarks>
    /// Wraps <see cref="MemoryTarget"/> rather than replacing it: the units still have to deploy and be
    /// removed for real, because what is being tested is WHEN the sweep happens relative to them.
    /// </remarks>
    private sealed class SweepingTarget(MemoryTarget inner) : IDeploymentTarget
    {
        public SweepingTarget() : this(new MemoryTarget()) { }

        /// <summary>Every sweep, as (procedure, prefix) — so a test can check what it was scoped to.</summary>
        public List<(string Procedure, string Prefix)> Sweeps { get; } = [];

        /// <summary>Thrown by the sweep, if a test wants one that fails.</summary>
        public Exception? SweepThrows { get; set; }

        /// <summary>Units still deployed in the wrapped target, so "swept AFTER" can be asserted.</summary>
        public List<string> DeployedWhenSwept { get; } = [];

        public MemoryTarget Inner => inner;

        public string Id => "sweeping";

        public IUnitDriver Driver => inner;

        public IFailureClassifier Classifier => inner;

        public Task<TargetIdentity> ValidateAsync(TargetCredentials? credentials, CancellationToken ct) =>
            inner.ValidateAsync(credentials, ct);

        public Task SweepAsync(SweepContext context)
        {
            Sweeps.Add((context.Procedure, context.Prefix));
            DeployedWhenSwept.AddRange(inner.Deployed);
            context.Progress("sweeping");
            return SweepThrows is { } boom ? Task.FromException(boom) : Task.CompletedTask;
        }
    }

    private static (ProcedureRunner Runner, SweepingTarget Target, IRunHistory History) Rig(
        SweepingTarget? target = null)
    {
        target ??= new SweepingTarget();
        var history = new InMemoryRunHistory();
        return (new ProcedureRunner(target, history, new InMemoryStateStore()), target, history);
    }

    // ── when it happens ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_full_destroy_sweeps_once_after_every_unit_is_gone()
    {
        var (runner, target, _) = Rig();
        await runner.ApplyAsync(Site, Requests.Bare());

        var outcome = await runner.DestroyAsync(Site, Requests.Bare());

        Assert.True(outcome.Ok);
        Assert.Equal([("site", "acme")], target.Sweeps);
        // AFTER, not during: a sweep that ran while a unit still stood would remove what that unit needs.
        Assert.Empty(target.DeployedWhenSwept);
    }

    [Fact]
    public async Task An_apply_never_sweeps()
    {
        // There is nothing to sweep BECAUSE the deployment is arriving rather than leaving, and a provider
        // whose staging was removed mid-apply would fail every unit after the first.
        var (runner, target, _) = Rig();

        await runner.ApplyAsync(Site, Requests.Bare());

        Assert.Empty(target.Sweeps);
    }

    [Fact]
    public async Task A_NARROWED_destroy_does_not_sweep()
    {
        // The dangerous one. `Only("web")` is a partial teardown by request: `db` is still deployed and still
        // needs whatever the provider keeps for this deployment. Sweeping here would break a deployment on
        // behalf of an operator who asked to remove one unit of it.
        var (runner, target, _) = Rig();
        await runner.ApplyAsync(Site, Requests.Bare());

        await runner.DestroyAsync(Site.Only("web"), Requests.Bare());

        Assert.Empty(target.Sweeps);
        Assert.Contains("db", target.Inner.Deployed);
    }

    [Fact]
    public async Task Narrowing_to_EVERY_unit_still_does_not_sweep()
    {
        // Deliberate, and worth pinning because the alternative is tempting: `Only` naming every unit leaves
        // the same units as the whole procedure, so a sweep would arguably be safe. It is refused on the
        // narrowing rather than on what the narrowing happens to contain — the same rule `Plan.Orphaned`
        // uses, and one that cannot be got wrong by a caller adding a unit later.
        var (runner, target, _) = Rig();
        await runner.ApplyAsync(Site, Requests.Bare());

        await runner.DestroyAsync(Site.Only("db", "web"), Requests.Bare());

        Assert.Empty(target.Sweeps);
    }

    [Fact]
    public async Task A_destroy_that_stops_part_way_does_not_sweep()
    {
        // `db` is removed second (reverse order), so failing it leaves it deployed — and its staging is
        // exactly what a resumed teardown will need.
        var target = new SweepingTarget(new MemoryTarget());
        var (runner, _, _) = Rig(target);
        await runner.ApplyAsync(Site, Requests.Bare());
        target.Inner.Fails("db", FailureClass.Credentials, "the token expired");

        var outcome = await runner.DestroyAsync(Site, Requests.Bare());

        Assert.True(outcome.Resumable);
        Assert.Empty(target.Sweeps);
    }

    [Fact]
    public async Task A_RETAINED_unit_does_not_stop_the_sweep()
    {
        // A unit that can never be removed would otherwise mean never sweeping at all — the teardown is as
        // complete as it will ever get, so this is the moment or there is none.
        var publish = new PublishUnit();
        var inner = new MemoryTarget(new CustomUnits { ["publish"] = publish });
        var target = new SweepingTarget(inner);
        var (runner, _, _) = Rig(target);
        var procedure = new Procedure("release",
            [new ProcedureUnit("build", "Build"), new ProcedureUnit("publish", "Published package")]);
        var request = Requests.With(new Dictionary<string, string> { ["publish.kind"] = "publish" });

        await runner.ApplyAsync(procedure, request);
        var outcome = await runner.DestroyAsync(procedure, request);

        Assert.True(outcome.Ok);
        Assert.Equal([("release", "acme")], target.Sweeps);
        Assert.True(publish.Published);              // …and it really was retained rather than removed
    }

    /// <summary>A publish: irreversible, so a teardown reports it as retained. See RetainedUnitTests.</summary>
    private sealed class PublishUnit : StepUnitDriver
    {
        public bool Published { get; private set; }

        public override Task<UnitPhase> PhaseAsync(UnitContext context) =>
            Task.FromResult(Published ? UnitPhase.Ready : UnitPhase.Missing);

        public override Task CreateAsync(UnitContext context)
        {
            Published = true;
            return Task.CompletedTask;
        }

        public override bool IsRemovable(UnitContext context) => false;
    }

    // ── when it goes wrong ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_sweep_that_fails_does_NOT_fail_the_teardown()
    {
        // Every unit is gone: the destroy did what it said. Failing the run would send an operator to re-run
        // a teardown with nothing left to remove, and would classify a leftover bucket as a deployment still
        // standing.
        var target = new SweepingTarget { SweepThrows = new InvalidOperationException("s3 said no") };
        var (runner, _, history) = Rig(target);
        await runner.ApplyAsync(Site, Requests.Bare());

        var outcome = await runner.DestroyAsync(Site, Requests.Bare());

        Assert.True(outcome.Ok);
        var destroy = (await history.RecentAsync()).Single(r => r.Kind == RunKind.Destroy);
        Assert.Equal(RunStatus.Succeeded, destroy.Status);
    }

    [Fact]
    public async Task A_sweep_that_fails_is_said_out_loud()
    {
        // The half that makes swallowing it honest. Silence here is the thing D32 refuses: a teardown
        // reporting success over something still out there.
        var target = new SweepingTarget { SweepThrows = new InvalidOperationException("s3 said no") };
        var (runner, _, _) = Rig(target);
        await runner.ApplyAsync(Site, Requests.Bare());

        var lines = new List<ProgressReport>();
        await runner.DestroyAsync(Site, Requests.Bare(), lines.Add);

        var complaint = Assert.Single(lines, l => l.Status == ProgressStatus.Error);
        Assert.Contains("s3 said no", complaint.Message);          // the provider's own words, not ours
        Assert.Contains("sweeping", complaint.Message);            // …and the target's id, so it is findable
    }

    [Fact]
    public async Task A_sweep_reports_progress_against_the_procedure()
    {
        // A sweep has no unit, so its lines are run-level ones — which is what `ProgressReport.Unit` says a
        // procedure name in that position means.
        var (runner, _, _) = Rig();
        await runner.ApplyAsync(Site, Requests.Bare());

        var lines = new List<ProgressReport>();
        await runner.DestroyAsync(Site, Requests.Bare(), lines.Add);

        Assert.Contains(lines, l => l is { Unit: "site", Message: "sweeping" });
    }

    // ── the default ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_target_that_sweeps_nothing_is_still_correct()
    {
        // The D18 growth pattern: the method arrived meaning "I do not do that", so a provider written
        // before it — or one that genuinely creates nothing of its own — needed no change at all.
        var runner = new ProcedureRunner(new MemoryTarget(), new InMemoryRunHistory(), new InMemoryStateStore());
        await runner.ApplyAsync(Site, Requests.Bare());

        Assert.True((await runner.DestroyAsync(Site, Requests.Bare())).Ok);
    }
}
