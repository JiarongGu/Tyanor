using Xunit;

namespace Tyanor.Tests;

/// <summary>
/// Narrowing a procedure to some of its units — Terraform's <c>-target</c>, and the answer to "just push
/// the website again".
///
/// <para>The deployer this was extracted from had a whole dedicated method for one case of this, because
/// pushing a website takes seconds and reconciling three stacks to do it takes minutes. That method did not
/// survive the port and the capability had nowhere to go.</para>
/// </summary>
public class TargetedApplyTests
{
    private static readonly Procedure Site = new("site",
    [
        new ProcedureUnit("db", "Database", Weight: 4),
        new ProcedureUnit("api", "API", Weight: 3),
        new ProcedureUnit("web", "Website"),
    ]);

    [Fact]
    public void Narrowing_keeps_only_what_it_names()
    {
        var only = Site.Only("web");

        Assert.Equal(["web"], only.Units.Select(u => u.Name));
        Assert.Equal("site", only.Name);          // the same procedure, so the same state and history
    }

    [Fact]
    public void Narrowing_keeps_the_PROCEDURES_order_not_the_callers()
    {
        // Narrowing must not silently reorder a deployment. Data before compute before edge is the whole
        // safety property of an ordered list, and it would be trivially lost here.
        var only = Site.Only("web", "db");

        Assert.Equal(["db", "web"], only.Units.Select(u => u.Name));
        Assert.Equal(["web", "db"], only.Reverse().Select(u => u.Name));
    }

    [Fact]
    public void Progress_is_scaled_to_what_is_actually_running()
    {
        // 5, not 8: a narrowed run's bar is about the narrowed run. Keeping the original total would leave a
        // targeted apply reporting 62% at completion.
        Assert.Equal(5, Site.Only("db", "web").TotalWeight);
    }

    [Fact]
    public void A_name_that_is_not_there_is_REFUSED_rather_than_ignored()
    {
        // A typo that quietly deploys nothing and reports success is the worst way for this to be wrong.
        var error = Assert.Throws<ArgumentException>(() => Site.Only("webb"));

        Assert.Contains("webb", error.Message);
        Assert.Contains("web", error.Message);            // …and says what it does have
    }

    [Fact]
    public void Narrowing_to_nothing_is_refused()
        // Same reason a procedure with no units is: it would apply successfully and deploy nothing.
        => Assert.Throws<ArgumentException>(() => Site.Only());

    [Fact]
    public void A_name_is_matched_however_it_is_cased()
        => Assert.Equal(["web"], Site.Only("WEB").Units.Select(u => u.Name));

    [Fact]
    public void Narrowing_the_whole_thing_is_the_whole_thing()
        => Assert.Equal(["db", "api", "web"], Site.Only("db", "api", "web").Units.Select(u => u.Name));

    [Fact]
    public void The_original_is_untouched()
    {
        // It is a record; narrowing returns a new one. Worth pinning, because a caller keeping the full
        // procedure around and finding it mutated would be a very confusing afternoon.
        _ = Site.Only("web");

        Assert.Equal(3, Site.Units.Count);
    }
}
