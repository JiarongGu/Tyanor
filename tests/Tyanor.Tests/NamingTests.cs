using Xunit;

namespace Tyanor.Tests;

/// <summary>
/// The names a deployment is addressed by, and why they are checked at all.
///
/// <para>A prefix and a unit name are not labels. They become <c>Path.Combine(root, prefix, unit)</c> in a
/// machine deployment, a CloudFormation stack name, a component of a bucket name — and, on teardown, the
/// argument to a recursive delete. Unchecked, a name is a path, and a path can point anywhere.</para>
/// </summary>
public class NamingTests
{
    private static DeploymentArtifact Nothing => new(new Dictionary<string, string>());

    [Theory]
    [InlineData("acme")]
    [InlineData("my-site")]
    [InlineData("my_site")]
    [InlineData("site.v2")]
    [InlineData("a")]
    [InlineData("release-2026-08-09")]
    public void An_ordinary_name_is_accepted(string name)
    {
        // The check has to stay out of the way of every name a person would actually pick.
        Assert.Equal(name, new DeploymentRequest(name, Nothing).Prefix);
        Assert.Equal(name, new ProcedureUnit(name, "Label").Name);
    }

    [Theory]
    [InlineData("../../etc")]
    [InlineData("..")]
    [InlineData("a/../b")]
    public void A_name_that_climbs_out_of_its_directory_is_REFUSED(string escape)
    {
        // The one that mattered. `Path.Combine("/srv", "../../etc", "runtime")` resolves outside /srv, and
        // DirectoryUnit.RemoveAsync hands exactly that to a recursive delete — so an operator typing a path
        // fragment into a name field could have deleted something nobody named.
        Assert.Throws<ArgumentException>(() => new DeploymentRequest(escape, Nothing));
        Assert.Throws<ArgumentException>(() => new ProcedureUnit(escape, "Label"));
    }

    [Theory]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("C:name")]
    public void A_name_containing_a_path_separator_is_refused(string path)
        => Assert.Throws<ArgumentException>(() => new DeploymentRequest(path, Nothing));

    [Fact]
    public void A_name_starting_with_a_dot_is_refused()
    {
        // Beyond being hidden: the local provider keeps its bookkeeping in `.tyanor` beside the units, so a
        // unit allowed that name would deploy on top of the pid files that supervise it.
        Assert.Throws<ArgumentException>(() => new ProcedureUnit(".tyanor", "Sneaky"));
        Assert.Throws<ArgumentException>(() => new ProcedureUnit(".hidden", "Hidden"));
    }

    [Fact]
    public void Each_refusal_gives_the_reason_that_is_actually_TRUE_of_the_name()
    {
        // Which rule fires first is not cosmetic — it decides what the operator is told to fix, and every
        // one of these names is refused whichever order the rules run in, so nothing but the message says
        // the order is right.
        //
        // It was wrong. `..` matched "starts with a dot" and was told about hidden files and provider
        // bookkeeping, while the sentence written for a parent-directory reference only ever appeared for
        // names like `v1..2` — where it is not true.
        Assert.Contains("parent directory",
            Assert.Throws<ArgumentException>(() => new ProcedureUnit("..", "Up")).Message);

        Assert.Contains("starts with a dot",
            Assert.Throws<ArgumentException>(() => new ProcedureUnit(".hidden", "Hidden")).Message);

        Assert.Contains("Use letters, digits",
            Assert.Throws<ArgumentException>(() => new ProcedureUnit("a/b", "Slashed")).Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_name_is_refused(string blank)
    {
        Assert.Throws<ArgumentException>(() => new DeploymentRequest(blank, Nothing));
        Assert.Throws<ArgumentException>(() => new ProcedureUnit(blank, "Label"));
    }

    [Fact]
    public void A_name_longer_than_a_path_component_can_be_is_refused()
        // 255 bytes is the limit most filesystems put on a single component, and every one of these names
        // becomes one. Not a provider's limit — those are stricter and each provider says so itself.
        => Assert.Throws<ArgumentException>(() => new ProcedureUnit(new string('a', 256), "Long"));

    [Fact]
    public void A_control_character_is_refused_and_named_readably()
    {
        // Built rather than typed: a literal control character in source is invisible to the next reader.
        var withBell = "api" + (char)7 + "bell";

        var error = Assert.Throws<ArgumentException>(() => new ProcedureUnit(withBell, "Label"));

        Assert.Contains("U+0007", error.Message);      // rather than printing it and beeping the terminal
    }

    [Fact]
    public void The_LABEL_is_free_text_because_it_is_only_ever_shown()
        // The distinction worth keeping: Name addresses something, Label is read by a person. Checking the
        // label would refuse "Website (staging)" for no reason at all.
        => Assert.Equal("Website (staging) — 2 of 3", new ProcedureUnit("web", "Website (staging) — 2 of 3").Label);

    [Fact]
    public void Rewriting_a_name_with_WITH_is_checked_too()
    {
        // `with` copies backing fields, so a validating initializer alone would be bypassed here — the
        // property's init accessor has to do the checking.
        var request = new DeploymentRequest("acme", Nothing);
        var unit = new ProcedureUnit("web", "Website");

        Assert.Throws<ArgumentException>(() => request with { Prefix = "../escape" });
        Assert.Throws<ArgumentException>(() => unit with { Name = "../escape" });
    }
}

/// <summary>What a procedure refuses, and why each one would otherwise be quiet.</summary>
public class ProcedureShapeTests
{
    [Fact]
    public void Two_units_with_the_same_name_are_refused()
    {
        // A unit's name is its address: the stack {prefix}-{name}, the directory {root}/{prefix}/{name}, its
        // entry in state. Two of them deploy on top of each other, and the second silently overwrites the
        // first's state — which looks exactly like a unit that quietly stopped existing.
        var error = Assert.Throws<ArgumentException>(() => new Procedure("site",
            [new ProcedureUnit("api", "API"), new ProcedureUnit("api", "API again")]));

        Assert.Contains("api", error.Message);
    }

    [Fact]
    public void Names_that_differ_only_by_case_are_refused_too()
        // On Windows `Api` and `api` are the same directory. A pair that looks fine on the machine it was
        // written on collides on the machine it deploys to.
        => Assert.Throws<ArgumentException>(() => new Procedure("site",
            [new ProcedureUnit("api", "API"), new ProcedureUnit("API", "Api")]));

    [Fact]
    public void A_procedure_with_no_units_is_refused()
        // It would apply successfully, report 100%, and deploy nothing — the most confusing possible success.
        => Assert.Throws<ArgumentException>(() => new Procedure("site", []));

    [Fact]
    public void A_weight_below_one_is_refused()
    {
        // Zero makes a unit invisible while it runs; negative makes the progress bar go backwards.
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProcedureUnit("web", "Website", Weight: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProcedureUnit("web", "Website", Weight: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProcedureUnit("web", "Website") with { Weight = 0 });
    }

    [Fact]
    public void A_valid_procedure_keeps_its_order_both_ways()
    {
        var procedure = new Procedure("site",
            [new ProcedureUnit("db", "Database"), new ProcedureUnit("api", "API"), new ProcedureUnit("web", "Website")]);

        Assert.Equal(["db", "api", "web"], procedure.Forward().Select(u => u.Name));
        Assert.Equal(["web", "api", "db"], procedure.Reverse().Select(u => u.Name));
        Assert.Equal(3, procedure.TotalWeight);
    }
}
