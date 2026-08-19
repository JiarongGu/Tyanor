using Xunit;

namespace Tyanor.Tests;

/// <summary>
/// <see cref="UnitProblems"/> — the gather-every-refusal shape every unit kind in both shipped providers
/// wrote for itself before it was written once.
///
/// <para>The behaviour worth pinning is the part defined by ABSENCE: that only
/// <see cref="DefinitionException"/> is caught. Catching more would turn a driver reaching for the network
/// during an offline check — the one thing <see cref="IUnitDriver.ValidateAsync"/> forbids — into a silent
/// pass, and no signature says otherwise.</para>
/// </summary>
public class UnitProblemsTests
{
    /// <summary>A concrete <see cref="DefinitionException"/>: the base is abstract, as a marker should be.</summary>
    private static ArtifactException Refusal(string message) => new(message);

    [Fact]
    public async Task A_unit_with_nothing_wrong_reports_nothing()
        => Assert.Empty(await new UnitProblems().Found());

    [Fact]
    public async Task A_resolver_that_succeeds_adds_nothing()
        => Assert.Empty(await new UnitProblems().Check(() => { /* resolved fine */ }).Found());

    [Fact]
    public async Task A_refusal_is_reported_as_its_own_message()
    {
        // The message the operator reads is the resolver's, unedited — which is what makes validating and
        // applying give the same sentence about the same mistake.
        var problems = await new UnitProblems()
            .Check(() => throw Refusal("names no 'template'"))
            .Found();

        Assert.Equal(["names no 'template'"], problems);
    }

    [Fact]
    public async Task EVERY_check_runs_even_after_one_has_already_failed()
    {
        // The whole reason validation exists rather than letting the apply throw: an operator with two
        // things wrong should be told both, not told one and then the other on the next attempt.
        var problems = await new UnitProblems()
            .Check(() => throw Refusal("no command"))
            .Check(() => throw Refusal("port is not a number"))
            .Check(() => throw Refusal("no source part"))
            .Found();

        Assert.Equal(["no command", "port is not a number", "no source part"], problems);
    }

    [Fact]
    public async Task Problems_keep_the_order_they_were_found_in()
    {
        // So a driver can put the most important check first and have it read first.
        var problems = await new UnitProblems()
            .Check(() => throw Refusal("first"))
            .Add("second")
            .Check(() => throw Refusal("third"))
            .Found();

        Assert.Equal(["first", "second", "third"], problems);
    }

    [Fact]
    public void An_exception_that_is_NOT_a_definition_problem_propagates()
    {
        // Deliberately not caught. A resolver throwing an IOException is reaching for the world during a
        // check documented to touch nothing — swallowing it would report a clean procedure and hide the
        // defect, which is the failure mode this whole repository keeps finding.
        Assert.Throws<IOException>(() =>
            new UnitProblems().Check(() => throw new IOException("reached for the disk")));
    }

    [Fact]
    public async Task Add_records_a_problem_no_resolver_raises()
    {
        // For the check that has nothing to call — "names no destination at all" is true of two options
        // being absent together, so no single refusal states it.
        Assert.Equal(["names no destination bucket"],
            await new UnitProblems().Add("names no destination bucket").Found());
    }

    [Fact]
    public async Task What_was_handed_back_does_not_change_afterwards()
    {
        // Found() snapshots. Returning the live list would let a driver that kept the builder alter a
        // result it had already given away.
        var problems = new UnitProblems().Add("first");
        var found = await problems.Found();

        problems.Add("second");

        Assert.Equal(["first"], found);
    }

    [Fact]
    public void It_refuses_a_null_resolver_or_a_blank_problem()
    {
        Assert.Throws<ArgumentNullException>(() => new UnitProblems().Check(null!));
        Assert.Throws<ArgumentException>(() => new UnitProblems().Add(" "));
    }
}
