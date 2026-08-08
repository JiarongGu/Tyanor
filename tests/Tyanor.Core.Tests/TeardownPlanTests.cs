using Tyanor.Engine;
using Tyanor.Engine.State;
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

    private static ProcedureRunner Runner(FakeTarget target, IStateStore? state = null) =>
        new(target, new InMemoryRunHistory(), state);

    [Fact]
    public void Removal_decides_only_two_things()
    {
        // Take it away, or notice it is already gone. There is no third answer, and in particular there is
        // no Attach: a unit mid-create is a unit that will exist in a minute, and waiting for someone else's
        // creation to finish before destroying it is a longer teardown with the same ending.
        Assert.Equal(ReconcileAction.Nothing, Reconcile.DecideRemoval(UnitPhase.Missing));

        foreach (var phase in Enum.GetValues<UnitPhase>().Where(p => p != UnitPhase.Missing))
            Assert.Equal(ReconcileAction.Remove, Reconcile.DecideRemoval(phase));
    }

    [Fact]
    public async Task A_teardown_plan_lists_the_units_in_REVERSE_order()
    {
        // The order they will actually go in, so what an operator reads is what will happen. Edge before
        // compute before data, so whatever imports from a unit is gone before the unit itself.
        var target = new FakeTarget { ["db"] = UnitPhase.Ready, ["api"] = UnitPhase.Ready, ["web"] = UnitPhase.Ready };

        var plan = await Runner(target).PlanAsync(Site, Request(), RunKind.Remove);

        Assert.Equal(["web", "api", "db"], plan.Steps.Select(s => s.Unit.Name));
        Assert.Equal(RunKind.Remove, plan.Kind);
    }

    [Fact]
    public async Task A_teardown_plan_says_which_units_are_already_gone()
    {
        // A re-run of an interrupted teardown, which is how teardowns usually finish.
        var target = new FakeTarget { ["db"] = UnitPhase.Ready, ["api"] = UnitPhase.Missing, ["web"] = UnitPhase.Missing };

        var plan = await Runner(target).PlanAsync(Site, Request(), RunKind.Remove);

        Assert.Equal([ReconcileAction.Nothing, ReconcileAction.Nothing, ReconcileAction.Remove],
            plan.Steps.Select(s => s.Action));
        Assert.Single(plan.Changes);                        // only the one still standing
    }

    [Fact]
    public async Task A_teardown_plan_counts_what_it_will_destroy()
    {
        // The number the operator is actually deciding on.
        var target = new FakeTarget { ["db"] = UnitPhase.Ready, ["api"] = UnitPhase.Ready, ["web"] = UnitPhase.Missing };
        target.Resources["db"] = [new ResourceState("db-1", "T", "v1"), new ResourceState("db-2", "T", "v1")];
        target.Resources["api"] = [new ResourceState("api-1", "T", "v1")];

        var plan = await Runner(target, new InMemoryStateStore()).PlanAsync(Site, Request(), RunKind.Remove);

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

        var target = new FakeTarget { ["db"] = UnitPhase.Ready };
        target.Resources["db"] = [new ResourceState("db-1", "T", "v1")];

        var plan = await Runner(target, state).PlanAsync(
            new Procedure("site", [new ProcedureUnit("db", "Database")]), Request(), RunKind.Remove);

        Assert.Equal(1, plan.ToDestroy);
    }

    [Fact]
    public async Task An_apply_plan_destroys_nothing_on_purpose()
    {
        // Destroying stays empty for an apply. A destroy count on an apply means DRIFT — something already
        // gone — which is a different sentence and should not be confused with intent.
        var target = new FakeTarget { ["db"] = UnitPhase.Ready };
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
        var target = new FakeTarget { ["db"] = UnitPhase.Missing, ["api"] = UnitPhase.Missing, ["web"] = UnitPhase.Missing };

        var plan = await Runner(target, new InMemoryStateStore()).PlanAsync(Site, Request(), RunKind.Remove);

        Assert.Equal(0, plan.ToDestroy);
        Assert.False(plan.IsDestructive);
        Assert.Empty(plan.Changes);
    }

    [Fact]
    public async Task A_replacement_is_destructive_even_on_an_apply()
    {
        // Replacing usually means losing whatever the unit was holding, which is the other thing worth a
        // confirmation.
        var target = new FakeTarget { ["db"] = UnitPhase.Broken };

        var plan = await Runner(target).PlanAsync(
            new Procedure("site", [new ProcedureUnit("db", "Database")]), Request());

        Assert.True(plan.IsDestructive);
    }

    // ── fakes ────────────────────────────────────────────────────────────────────────────────────
    private sealed class FakeTarget : IDeploymentTarget, IUnitDriver, IFailureClassifier
    {
        private readonly Dictionary<string, UnitPhase> _phases = [];

        public UnitPhase this[string unit] { set => _phases[unit] = value; }

        public Dictionary<string, IReadOnlyList<ResourceState>> Resources { get; } = [];

        public string Id => "fake";
        public IUnitDriver Driver => this;
        public IFailureClassifier Classifier => this;
        public FailureClass? Classify(Exception error) => null;
        public Task<TargetIdentity> ValidateAsync(TargetCredentials? c, CancellationToken ct) => Task.FromResult(new TargetIdentity(true));

        public Task<UnitPhase> PhaseAsync(UnitContext c) =>
            Task.FromResult(_phases.GetValueOrDefault(c.Name, UnitPhase.Missing));

        public Task CreateAsync(UnitContext c) => Task.CompletedTask;
        public Task<bool> UpdateAsync(UnitContext c) => Task.FromResult(false);
        public Task RemoveAsync(UnitContext c) => Task.CompletedTask;
        public Task AwaitSettledAsync(UnitContext c) => Task.CompletedTask;

        public Task<IReadOnlyList<ResourceState>> RefreshAsync(UnitContext c) =>
            Task.FromResult(Resources.GetValueOrDefault(c.Name, []));
    }

    private sealed class InMemoryStateStore : IStateStore
    {
        private readonly Dictionary<string, DeploymentState> _states = [];

        public Task<DeploymentState> GetAsync(string procedure, string prefix, CancellationToken ct = default) =>
            Task.FromResult(_states.GetValueOrDefault($"{procedure}/{prefix}") ?? DeploymentState.Empty(procedure, prefix));

        public Task SaveAsync(DeploymentState state, CancellationToken ct = default)
        {
            _states[$"{state.Procedure}/{state.Prefix}"] = state;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string procedure, string prefix, CancellationToken ct = default)
        {
            _states.Remove($"{procedure}/{prefix}");
            return Task.CompletedTask;
        }
    }
}
