using Tyanor.Engine;
using Tyanor.Engine.State;
using Tyanor.Testing;
using Xunit;

namespace Tyanor.Tests;

/// <summary>
/// The state diff — "is what I recorded still what is out there?". Pure, and worth testing directly
/// because both ways of being wrong are quiet: a missed drift means the operator acts on a number that is
/// false, and an invented one teaches them to ignore the number entirely.
/// </summary>
public class StateDiffTests
{
    private static ResourceState R(string id, string? fingerprint = "v1") => new(id, "Test::Thing", fingerprint);

    [Fact]
    public void Matching_resources_produce_no_drift()
        => Assert.Empty(StateDiff.ForUnit("db", [R("a"), R("b")], [R("a"), R("b")]));

    [Fact]
    public void A_resource_deleted_outside_Tyanor_reads_as_destroy()
    {
        // Recorded, but gone. Someone removed it by hand, or it never really existed.
        var drift = Assert.Single(StateDiff.ForUnit("db", [R("a")], []));

        Assert.Equal(ResourceChange.Destroy, drift.Change);
        Assert.Equal("a", drift.Resource.Id);
        Assert.Equal("db", drift.Unit);
    }

    [Fact]
    public void A_resource_created_outside_Tyanor_reads_as_add()
    {
        // Present but unrecorded — created by hand, or state was lost. It gets adopted on the next apply,
        // which is what makes re-syncing an existing deployment possible at all.
        var drift = Assert.Single(StateDiff.ForUnit("db", [], [R("a")]));

        Assert.Equal(ResourceChange.Add, drift.Change);
    }

    [Fact]
    public void A_different_fingerprint_reads_as_change()
    {
        var drift = Assert.Single(StateDiff.ForUnit("db", [R("a", "v1")], [R("a", "v2")]));

        Assert.Equal(ResourceChange.Change, drift.Change);
        Assert.Equal("v2", drift.Resource.Fingerprint);      // the drift carries what is ACTUALLY there
    }

    [Theory]
    [InlineData(null, "v1")]
    [InlineData("v1", null)]
    [InlineData(null, null)]
    public void An_unknowable_fingerprint_reads_as_change_rather_than_assumed_equal(string? recorded, string? actual)
    {
        // The conservative direction on purpose: an unnoticed change is worse than one flagged for a look,
        // and the operator can see the fingerprint is unknown.
        Assert.Single(StateDiff.ForUnit("db", [R("a", recorded)], [R("a", actual)]));
        Assert.False(StateDiff.Unchanged(recorded, actual));
    }

    [Fact]
    public void A_mixed_unit_reports_each_difference_once()
    {
        var drift = StateDiff.ForUnit("db",
            recorded: [R("keep"), R("changed", "v1"), R("deleted")],
            actual: [R("keep"), R("changed", "v2"), R("appeared")]);

        Assert.Equal(3, drift.Count);
        Assert.Single(drift, d => d.Change == ResourceChange.Change && d.Resource.Id == "changed");
        Assert.Single(drift, d => d.Change == ResourceChange.Destroy && d.Resource.Id == "deleted");
        Assert.Single(drift, d => d.Change == ResourceChange.Add && d.Resource.Id == "appeared");
    }
}

/// <summary>The one set of state — what it records, and what it forgets.</summary>
public class DeploymentStateTests
{
    private static ResourceState R(string id) => new(id, "Test::Thing", "v1");

    [Fact]
    public void An_empty_state_knows_nothing_and_says_so_without_a_null()
    {
        var state = DeploymentState.Empty("site", "acme");

        Assert.Empty(state.Units);
        Assert.Empty(state.For("db"));                       // "no record" and "no resources" are one thing
    }

    [Fact]
    public void Recording_a_unit_replaces_rather_than_appends()
    {
        var state = DeploymentState.Empty("site", "acme")
            .With("db", [R("a")])
            .With("db", [R("b"), R("c")]);

        Assert.Single(state.Units);
        Assert.Equal(["b", "c"], state.For("db").Select(r => r.Id));
    }

    [Fact]
    public void A_unit_recorded_as_empty_is_dropped_from_state()
    {
        // After a teardown the unit owns nothing, and state should not keep an empty shell implying it does.
        var state = DeploymentState.Empty("site", "acme").With("db", [R("a")]).With("db", []);

        Assert.Empty(state.Units);
    }

    [Fact]
    public void EDITING_state_leaves_the_serial_alone_because_the_STORE_owns_it()
    {
        // The serial is the version this snapshot was READ at, not a count of edits — that is what lets a
        // store with conditional writes compare it against what it holds.
        //
        // It used to increment here, which made the property unable to do its one job: a backend checking
        // it as documented would see a serial one ahead of what it stored and refuse every save. A Refresh
        // was worse — it edits every unit before saving once, so the number ran ahead by the unit count.
        var state = DeploymentState.Empty("site", "acme");

        Assert.Equal(0, state.Serial);
        Assert.Equal(0, state.With("db", [R("a")]).Serial);
        Assert.Equal(0, state.With("db", [R("a")]).With("api", [R("b")]).Serial);
    }

    [Fact]
    public void A_serial_read_from_a_store_survives_an_edit_so_it_can_be_handed_back()
    {
        var read = new DeploymentState("site", "acme", [], DateTimeOffset.UtcNow, Serial: 7);

        Assert.Equal(7, read.With("db", [R("a")]).Serial);
    }

    [Fact]
    public void Units_are_kept_apart()
    {
        var state = DeploymentState.Empty("site", "acme").With("db", [R("a")]).With("api", [R("b")]);

        Assert.Equal(["a"], state.For("db").Select(r => r.Id));
        Assert.Equal(["b"], state.For("api").Select(r => r.Id));
    }
}

/// <summary>
/// The question a reconcile loop structurally cannot ask: does STATE hold a unit the CONFIG no longer has?
///
/// <para>Every other pass walks the procedure's units — the phase read, the drift comparison, the teardown —
/// so deleting a unit from the C# removed it from everything that looks, and whatever it deployed went on
/// existing, paid for and unmentioned by any plan. Terraform catches this because it diffs config against
/// state; Tyanor diffs config against reality and state against reality, and this was the third edge.</para>
/// </summary>
public class OrphanedUnitTests
{
    private static readonly ProcedureUnit Db = new("db", "Database");
    private static readonly ProcedureUnit Api = new("api", "API");
    private static readonly ProcedureUnit Cache = new("cache", "Cache");

    private static DeploymentRequest Request() =>
        new("acme", new DeploymentArtifact(new Dictionary<string, string>()));

    /// <summary>State from a run of the procedure BEFORE someone deleted a unit from it.</summary>
    private static async Task<IStateStore> StateWith(params string[] units)
    {
        var store = new InMemoryStateStore();
        var state = DeploymentState.Empty("site", "acme");
        foreach (var unit in units)
            state = state.With(unit, [new ResourceState($"{unit}-1", "T", "v1")]);

        await store.SaveAsync(state);
        return store;
    }

    [Fact]
    public async Task A_unit_deleted_from_the_procedure_is_REPORTED_rather_than_silently_stranded()
    {
        // The code used to have a cache. Someone removed the unit and left the infrastructure running.
        var runner = new ProcedureRunner(new MemoryTarget(), new InMemoryRunHistory(), await StateWith("db", "api", "cache"));

        var plan = await runner.PlanAsync(new Procedure("site", [Db, Api]), Request());

        Assert.True(plan.HasOrphans);
        Assert.Equal("cache", Assert.Single(plan.Orphaned).Unit);
        Assert.Single(Assert.Single(plan.Orphaned).Resources);
        Assert.False(plan.IsNoOp);                       // …and it is not "nothing to do"
    }

    [Fact]
    public async Task A_procedure_that_still_declares_everything_has_no_orphans()
    {
        var runner = new ProcedureRunner(new MemoryTarget(), new InMemoryRunHistory(), await StateWith("db", "api"));

        var plan = await runner.PlanAsync(new Procedure("site", [Db, Api]), Request());

        Assert.False(plan.HasOrphans);
    }

    [Fact]
    public async Task A_NARROWED_run_does_not_cry_orphan_over_the_units_it_was_told_to_skip()
    {
        // The trap this feature walks into if nobody thinks about it: `Only("db")` deliberately leaves api
        // out, and reporting that as stranded would make every targeted run noisy — which is how a real
        // orphan comes to be ignored.
        var runner = new ProcedureRunner(new MemoryTarget(), new InMemoryRunHistory(), await StateWith("db", "api"));

        var plan = await runner.PlanAsync(new Procedure("site", [Db, Api]).Only("db"), Request());

        Assert.False(plan.HasOrphans);
        Assert.True(new Procedure("site", [Db, Api]).Only("db").IsNarrowed);
        Assert.False(new Procedure("site", [Db, Api]).IsNarrowed);
    }

    [Fact]
    public async Task A_TEARDOWN_reports_them_too_because_that_is_the_run_after_which_they_are_all_that_is_left()
    {
        var runner = new ProcedureRunner(new MemoryTarget(), new InMemoryRunHistory(), await StateWith("db", "cache"));

        var plan = await runner.PlanAsync(new Procedure("site", [Db]), Request(), RunKind.Destroy);

        Assert.Equal("cache", Assert.Single(plan.Orphaned).Unit);
    }

    [Fact]
    public async Task With_no_state_store_nothing_can_be_orphaned_because_nothing_is_recorded()
    {
        var runner = new ProcedureRunner(new MemoryTarget(), new InMemoryRunHistory());

        Assert.False((await runner.PlanAsync(new Procedure("site", [Db]), Request())).HasOrphans);
    }

    [Fact]
    public async Task Putting_the_unit_BACK_and_destroying_it_is_the_way_out()
    {
        // The remedy the report points at. Tyanor cannot destroy a unit it no longer has — the kind, the
        // options and the artifact parts went with it — so the operator restores the declaration, runs a
        // narrowed destroy, and only then deletes the code.
        var state = await StateWith("db", "cache");
        var target = new MemoryTarget().AlreadyDeployed("db", "cache");
        var runner = new ProcedureRunner(target, new InMemoryRunHistory(), state);
        var full = new Procedure("site", [Db, Cache]);

        Assert.True((await runner.DestroyAsync(full.Only("cache"), Request())).Ok);

        var plan = await runner.PlanAsync(new Procedure("site", [Db]), Request());
        Assert.False(plan.HasOrphans);
    }
}
