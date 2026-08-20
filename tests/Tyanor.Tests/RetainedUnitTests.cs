using Tyanor.Engine;
using Tyanor.Engine.State;
using Tyanor.Testing;
using Xunit;

namespace Tyanor.Tests;

/// <summary>
/// A unit that CANNOT be taken away — a published version, an audit record, a sent email.
///
/// <para><b>This was a named gap, and the note that named it predicted the answer.</b> <c>TASKS.md</c> item 4
/// said: a publish is irreversible, yet <see cref="IUnitDriver.RemoveAsync"/> is documented as "remove and
/// wait until it is gone" and <see cref="Reconcile.DecideDestroy"/> handed <see cref="ReconcileAction.Remove"/>
/// to every phase that was not <see cref="UnitPhase.Missing"/> — so such a unit could only lie or throw. The
/// predicted answer was a unit declaring itself unremovable and a destroy plan reporting it as RETAINED
/// rather than skipping it in silence. That is what these pin.</para>
///
/// <para><b>Both bad options are worth naming, because they are what an adopter would otherwise do.</b> A
/// remove that returns quietly makes a destroy report success over a version that is still published, and
/// state forget it. A remove that throws fails a teardown that had nothing wrong with it, every time,
/// forever. Saying so up front costs one method and is the only one of the three an operator can act on.</para>
/// </summary>
public class RetainedUnitTests
{
    private static readonly Procedure Pipeline = new("release",
    [
        new ProcedureUnit("build", "Build output"),
        new ProcedureUnit("publish", "Published package"),
    ]);

    /// <summary>A publish: it happens, it is recorded, and it does not come back.</summary>
    private sealed class PublishUnit : StepUnitDriver
    {
        public bool Published { get; private set; }

        public int RemoveCalls { get; private set; }

        public override Task<UnitPhase> PhaseAsync(UnitContext context) =>
            Task.FromResult(Published ? UnitPhase.Ready : UnitPhase.Missing);

        public override Task CreateAsync(UnitContext context)
        {
            Published = true;
            return Task.CompletedTask;
        }

        // The whole declaration. Everything else follows from it.
        public override bool IsRemovable(UnitContext context) => false;

        public override Task RemoveAsync(UnitContext context)
        {
            RemoveCalls++;
            return Task.CompletedTask;
        }

        public override Task<IReadOnlyList<ResourceState>> RefreshAsync(UnitContext context) =>
            Task.FromResult<IReadOnlyList<ResourceState>>(
                Published ? [new ResourceState("pkg://acme/1.0.0", "registry/version", "1.0.0")] : []);
    }

    private static DeploymentRequest Request() =>
        new("acme", new DeploymentArtifact(new Dictionary<string, string>()),
            new Dictionary<string, string> { ["publish.kind"] = "publish" });

    private static (ProcedureRunner Runner, MemoryTarget Target, PublishUnit Publish, IStateStore State) Rig()
    {
        var publish = new PublishUnit();
        var target = new MemoryTarget(new CustomUnits { ["publish"] = publish });
        var state = new InMemoryStateStore();

        return (new ProcedureRunner(target, new InMemoryRunHistory(), state), target, publish, state);
    }

    // ── the decision ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_teardown_now_has_THREE_answers()
    {
        // Take it away, notice it is already gone, or leave one that cannot go.
        Assert.Equal(ReconcileAction.Remove, Reconcile.DecideDestroy(UnitPhase.Ready));
        Assert.Equal(ReconcileAction.Nothing, Reconcile.DecideDestroy(UnitPhase.Missing));
        Assert.Equal(ReconcileAction.Retain, Reconcile.DecideDestroy(UnitPhase.Ready, removable: false));
    }

    [Fact]
    public void An_absent_irreversible_unit_is_NOTHING_rather_than_retained()
        // "Already gone" beats "cannot go": there is nothing left to leave behind, so nothing to warn about.
        => Assert.Equal(ReconcileAction.Nothing, Reconcile.DecideDestroy(UnitPhase.Missing, removable: false));

    [Fact]
    public void Retaining_mutates_nothing()
        // The point of the answer is that the run does not touch it.
        => Assert.False(Reconcile.Mutates(ReconcileAction.Retain));

    // ── the plan says so BEFORE anything runs ────────────────────────────────────────────────────

    [Fact]
    public async Task A_destroy_plan_reports_what_it_will_LEAVE()
    {
        var (runner, _, _, _) = Rig();
        await runner.ApplyAsync(Pipeline, Request());

        var plan = await runner.PlanAsync(Pipeline, Request(), RunKind.Destroy);

        Assert.True(plan.HasRetained);
        Assert.Equal(["publish"], plan.Retained.Select(s => s.Unit.Name));
        Assert.Contains("RETAINED", plan.Retained[0].ToString());
    }

    [Fact]
    public async Task What_is_retained_is_NOT_counted_as_something_the_run_will_destroy()
    {
        // The number lying in the dangerous direction: an operator confirming a teardown they believe is
        // complete. What will actually go is counted; what stays is reported separately.
        var (runner, _, _, _) = Rig();
        await runner.ApplyAsync(Pipeline, Request());

        var plan = await runner.PlanAsync(Pipeline, Request(), RunKind.Destroy);

        Assert.DoesNotContain(plan.Destroying, d => d.Unit == "publish");
        Assert.Contains(plan.Destroying, d => d.Unit == "build");
    }

    [Fact]
    public async Task A_teardown_of_ONLY_irreversible_units_is_not_destructive_and_still_says_something()
    {
        // Nothing will be taken away, so no confirmation is owed — but the operator still has to learn that
        // the thing they asked to remove is staying.
        var publish = new PublishUnit();
        var runner = new ProcedureRunner(
            new MemoryTarget(new CustomUnits { ["publish"] = publish }), new InMemoryRunHistory());

        var only = new Procedure("release", [new ProcedureUnit("publish", "Published package")]);
        await runner.ApplyAsync(only, Request());

        var plan = await runner.PlanAsync(only, Request(), RunKind.Destroy);

        Assert.False(plan.IsDestructive);
        Assert.True(plan.HasRetained);
    }

    // ── and the run behaves the way the plan said ────────────────────────────────────────────────

    [Fact]
    public async Task The_teardown_never_calls_remove_on_it()
    {
        // So an irreversible driver does not have to lie or throw in RemoveAsync at all.
        var (runner, _, publish, _) = Rig();
        await runner.ApplyAsync(Pipeline, Request());

        await runner.DestroyAsync(Pipeline, Request());

        Assert.Equal(0, publish.RemoveCalls);
        Assert.True(publish.Published);
    }

    [Fact]
    public async Task The_teardown_SUCCEEDS_rather_than_failing_over_something_it_cannot_undo()
    {
        var (runner, target, _, _) = Rig();
        await runner.ApplyAsync(Pipeline, Request());

        var outcome = await runner.DestroyAsync(Pipeline, Request());

        Assert.True(outcome.Ok);
        Assert.Empty(target.Deployed);          // the removable half did go
    }

    [Fact]
    public async Task It_is_said_OUT_LOUD_every_time_rather_than_skipped_quietly()
    {
        // The difference between a stated limit and a surprise. A destroy that printed nothing about it
        // would be indistinguishable from one that removed everything.
        var (runner, _, _, _) = Rig();
        await runner.ApplyAsync(Pipeline, Request());
        var said = new List<string>();

        await runner.DestroyAsync(Pipeline, Request(), report: p => said.Add(p.Message));

        Assert.Contains(said, m => m.Contains("RETAINED"));
    }

    [Fact]
    public async Task Its_state_is_KEPT_after_the_teardown()
    {
        // The one that would rot silently. Clearing it makes Tyanor forget it owns something still out
        // there — and D25's whole point is that an unowned resource is one no plan ever mentions again.
        var (runner, _, _, state) = Rig();
        await runner.ApplyAsync(Pipeline, Request());

        await runner.DestroyAsync(Pipeline, Request());

        var owned = await state.GetAsync("release", "acme");
        Assert.Equal(["publish"], owned.RecordedUnits);
        Assert.Equal("pkg://acme/1.0.0", owned.For("publish")[0].Id);
    }

    [Fact]
    public async Task A_second_teardown_still_reports_it_rather_than_forgetting()
    {
        // Teardowns are re-runnable, and the answer must not drift on the second pass.
        var (runner, _, _, _) = Rig();
        await runner.ApplyAsync(Pipeline, Request());
        await runner.DestroyAsync(Pipeline, Request());

        var plan = await runner.PlanAsync(Pipeline, Request(), RunKind.Destroy);

        Assert.True(plan.HasRetained);
        Assert.True((await runner.DestroyAsync(Pipeline, Request())).Ok);
    }

    // ── and it can prove itself with the same suite as everything else ───────────────────────────

    public static TheoryData<string> Checks() => Suites.Names(new UnitDriverContract(null!));

    [Theory]
    [MemberData(nameof(Checks))]
    public Task An_irreversible_unit_satisfies_the_driver_contract(string check) =>
        new UnitDriverContract(new PublishFixture()).AssertAsync(check);

    /// <summary>
    /// The fixture resets by reaching past the driver — which is the point: returning the TARGET to nothing
    /// is the fixture's job (drop the table, delete the scratch registry) and has nothing to do with what
    /// the driver is able to remove.
    /// </summary>
    private sealed class PublishFixture : IUnitDriverFixture
    {
        private PublishUnit _publish = new();

        public IUnitDriver Driver => _publish;

        public ProcedureUnit Unit { get; } = new("publish", "Published package");

        public DeploymentRequest Request { get; } =
            new("contract", new DeploymentArtifact(new Dictionary<string, string>()));

        public Task ResetAsync(CancellationToken ct)
        {
            _publish = new PublishUnit();          // a fresh registry, not an un-publish
            return Task.CompletedTask;
        }
    }
}
