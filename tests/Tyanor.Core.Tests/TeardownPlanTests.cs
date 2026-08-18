using Tyanor.Engine;
using Tyanor.Engine.State;
using Tyanor.Testing;
using Xunit;

namespace Tyanor.Tests;

/// <summary>
/// The plan for a TEARDOWN — the gate in front of the only operation that destroys anything.
///
/// <para>An apply is recoverable by running it again. A destroy is not recoverable at all, so a plan that
/// covered only the recoverable direction was a safety gate on the wrong door.</para>
/// </summary>
public class TeardownPlanTests
{
    private static readonly Procedure Site = new("site",
    [
        new ProcedureUnit("db", "Database"),
        new ProcedureUnit("api", "API"),
        new ProcedureUnit("web", "Website"),
    ]);

    private static DeploymentRequest Request() =>
        new("acme", new DeploymentArtifact(new Dictionary<string, string>()));

    private static ProcedureRunner Runner(MemoryTarget target, IStateStore? state = null) =>
        new(target, new InMemoryRunHistory(), state);

    [Fact]
    public void Removal_decides_only_two_things()
    {
        // Take it away, or notice it is already gone. There is no third answer, and in particular there is
        // no Attach: a unit mid-create is a unit that will exist in a minute, and waiting for someone else's
        // creation to finish before destroying it is a longer teardown with the same ending.
        Assert.Equal(ReconcileAction.Nothing, Reconcile.DecideDestroy(UnitPhase.Missing));

        foreach (var phase in Enum.GetValues<UnitPhase>().Where(p => p != UnitPhase.Missing))
            Assert.Equal(ReconcileAction.Remove, Reconcile.DecideDestroy(phase));
    }

    [Fact]
    public async Task A_teardown_plan_lists_the_units_in_REVERSE_order()
    {
        // The order they will actually go in, so what an operator reads is what will happen. Edge before
        // compute before data, so whatever imports from a unit is gone before the unit itself.
        var target = new MemoryTarget().AlreadyDeployed("db", "api", "web");

        var plan = await Runner(target).PlanAsync(Site, Request(), RunKind.Destroy);

        Assert.Equal(["web", "api", "db"], plan.Steps.Select(s => s.Unit.Name));
        Assert.Equal(RunKind.Destroy, plan.Kind);
    }

    [Fact]
    public async Task A_teardown_plan_says_which_units_are_already_gone()
    {
        // A re-run of an interrupted teardown, which is how teardowns usually finish.
        var target = new MemoryTarget().AlreadyDeployed("db");

        var plan = await Runner(target).PlanAsync(Site, Request(), RunKind.Destroy);

        Assert.Equal([ReconcileAction.Nothing, ReconcileAction.Nothing, ReconcileAction.Remove],
            plan.Steps.Select(s => s.Action));
        Assert.Single(plan.Changes);                        // only the one still standing
    }

    [Fact]
    public async Task A_teardown_plan_counts_what_it_will_destroy()
    {
        // The number the operator is actually deciding on.
        var target = new MemoryTarget().AlreadyDeployed("db", "api");
        target.Resources["db"] = [new ResourceState("db-1", "T", "v1"), new ResourceState("db-2", "T", "v1")];
        target.Resources["api"] = [new ResourceState("api-1", "T", "v1")];

        var plan = await Runner(target, new InMemoryStateStore()).PlanAsync(Site, Request(), RunKind.Destroy);

        Assert.Equal(3, plan.ToDestroy);
        Assert.Equal("0 to add, 0 to change, 3 to destroy", plan.Summary);
        Assert.True(plan.IsDestructive);
    }

    [Fact]
    public async Task A_teardown_counts_what_is_THERE_not_what_was_once_recorded()
    {
        // A resource someone already deleted by hand is not something this run is about to take away.
        // Counting it would inflate the one number the whole decision rests on.
        var state = new InMemoryStateStore();
        await state.SaveAsync(DeploymentState.Empty("site", "acme")
            .With("db", [new ResourceState("db-1", "T", "v1"), new ResourceState("gone-already", "T", "v1")]));

        var target = new MemoryTarget().AlreadyDeployed("db");
        target.Resources["db"] = [new ResourceState("db-1", "T", "v1")];

        var plan = await Runner(target, state).PlanAsync(
            new Procedure("site", [new ProcedureUnit("db", "Database")]), Request(), RunKind.Destroy);

        Assert.Equal(1, plan.ToDestroy);
    }

    [Fact]
    public async Task An_apply_plan_destroys_nothing_on_purpose()
    {
        // Destroying stays empty for an apply. A destroy count on an apply means DRIFT — something already
        // gone — which is a different sentence and should not be confused with intent.
        var target = new MemoryTarget().AlreadyDeployed("db");
        target.Resources["db"] = [new ResourceState("db-1", "T", "v1")];

        var plan = await Runner(target, new InMemoryStateStore()).PlanAsync(
            new Procedure("site", [new ProcedureUnit("db", "Database")]), Request());

        Assert.Empty(plan.Destroying);
        Assert.Equal(RunKind.Apply, plan.Kind);
        Assert.False(plan.IsDestructive);
    }

    [Fact]
    public async Task A_teardown_of_something_already_gone_destroys_nothing()
    {
        var target = new MemoryTarget();

        var plan = await Runner(target, new InMemoryStateStore()).PlanAsync(Site, Request(), RunKind.Destroy);

        Assert.Equal(0, plan.ToDestroy);
        Assert.False(plan.IsDestructive);
        Assert.Empty(plan.Changes);
    }

    [Fact]
    public async Task A_teardown_plan_still_notices_the_provider_is_busy()
    {
        // Work in flight is read from the PHASE, not from the action. Reading the action was equivalent on
        // an apply — only Converging produces Attach — and wrong here, because a teardown never attaches: a
        // removal plan reported an idle provider however busy it was, which made every teardown plan claim
        // a live run had stalled and that nothing was in sync.
        var target = new MemoryTarget { Phases = { ["api"] = UnitPhase.Converging } }.AlreadyDeployed("db");
        var history = new InMemoryRunHistory();
        await history.UpsertAsync(new RunRecord(
            "run-A", "site", "acme", RunKind.Apply, RunStatus.Running, DateTimeOffset.UnixEpoch));

        var plan = await new ProcedureRunner(target, history).PlanAsync(Site, Request(), RunKind.Destroy);

        Assert.True(plan.HasWorkInFlight);
        Assert.False(plan.HasStalledRun);
        Assert.True(plan.InSync);
    }

    [Fact]
    public async Task A_replacement_is_destructive_even_on_an_apply()
    {
        // Replacing usually means losing whatever the unit was holding, which is the other thing worth a
        // confirmation.
        var target = new MemoryTarget().Reports("db", UnitPhase.Broken);

        var plan = await Runner(target).PlanAsync(
            new Procedure("site", [new ProcedureUnit("db", "Database")]), Request());

        Assert.True(plan.IsDestructive);
    }

    [Fact]
    public async Task A_teardown_plan_counts_what_it_will_destroy_WITHOUT_a_state_store()
    {
        // A state store is optional, and gating the destroy count on one made a teardown plan built without
        // it report "0 to destroy" and IsDestructive FALSE — for a run that was about to take everything
        // away. That is the confirmation gate the README tells operators to put in front of the one
        // irreversible direction, silently open.
        //
        // Drift genuinely needs state (it is state-versus-reality). What a teardown will destroy does not:
        // it is read entirely from the provider.
        var target = new MemoryTarget().AlreadyDeployed("db", "api");
        target.Resources["db"] = [new ResourceState("db-1", "T", "v1")];
        target.Resources["api"] = [new ResourceState("api-1", "T", "v1")];

        var plan = await Runner(target).PlanAsync(Site, Request(), RunKind.Destroy);

        Assert.Equal(2, plan.ToDestroy);
        Assert.True(plan.IsDestructive);
        Assert.Equal("0 to add, 0 to change, 2 to destroy", plan.Summary);
    }

    [Fact]
    public async Task An_apply_plan_without_a_state_store_still_reports_no_drift()
    {
        // The other half of the same line: drift is state-versus-reality, so with no state there is nothing
        // to compare and reporting everything as an ADD would be an invention.
        var target = new MemoryTarget().AlreadyDeployed("db");
        target.Resources["db"] = [new ResourceState("db-1", "T", "v1")];

        var plan = await Runner(target).PlanAsync(
            new Procedure("site", [new ProcedureUnit("db", "Database")]), Request());

        Assert.False(plan.HasDrift);
        Assert.Equal("0 to add, 0 to change, 0 to destroy", plan.Summary);
    }
}
