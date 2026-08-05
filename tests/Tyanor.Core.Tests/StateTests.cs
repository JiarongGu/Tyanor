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
    public void Every_write_advances_the_serial()
    {
        // The handle a store with conditional writes uses to refuse clobbering someone else's state.
        var state = DeploymentState.Empty("site", "acme");

        Assert.Equal(0, state.Serial);
        Assert.Equal(1, state.With("db", [R("a")]).Serial);
        Assert.Equal(2, state.With("db", [R("a")]).With("api", [R("b")]).Serial);
    }

    [Fact]
    public void Units_are_kept_apart()
    {
        var state = DeploymentState.Empty("site", "acme").With("db", [R("a")]).With("api", [R("b")]);

        Assert.Equal(["a"], state.For("db").Select(r => r.Id));
        Assert.Equal(["b"], state.For("api").Select(r => r.Id));
    }
}
