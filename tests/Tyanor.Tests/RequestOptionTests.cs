using Xunit;

namespace Tyanor.Tests;

/// <summary>
/// How a request's options are read.
///
/// <para>Both scoped readers exist because a provider with HETEROGENEOUS units cannot be configured by one
/// flat map — a directory and a process are different things and take different settings. The fallback is
/// what stops that costing three lines per unit, and getting the precedence backwards would silently apply
/// a procedure-wide default over a setting the operator wrote for one unit on purpose.</para>
/// </summary>
public class RequestOptionTests
{
    private static DeploymentRequest Request(Dictionary<string, string> options) =>
        new("acme", new DeploymentArtifact(new Dictionary<string, string>()), options);

    [Fact]
    public void A_unit_scoped_option_beats_the_procedure_wide_one()
    {
        var request = Request(new Dictionary<string, string> { ["kind"] = "directory", ["web.kind"] = "process" });

        Assert.Equal("process", request.Option("web", "kind"));
        Assert.Equal("directory", request.Option("db", "kind"));      // no exception named, so the shared one
    }

    [Fact]
    public void An_option_nobody_set_is_null_rather_than_empty()
        // Empty is a value an operator can mean. Absent is not, and conflating them makes "unset" and
        // "deliberately blank" indistinguishable to a provider deciding whether to apply a default.
        => Assert.Null(Request([]).Option("web", "kind"));

    [Fact]
    public void A_unit_name_is_not_confused_with_a_prefix_of_another()
    {
        var request = Request(new Dictionary<string, string> { ["web.kind"] = "process" });

        Assert.Null(request.Option("web-content", "kind"));
        Assert.Null(request.Option("we", "kind"));
    }

    [Fact]
    public void An_option_SET_gathers_a_group_and_strips_the_prefix()
    {
        // For settings whose keys a provider cannot know in advance — CloudFormation parameters, Kubernetes
        // labels, environment variables. Reading them one at a time is impossible.
        var request = Request(new Dictionary<string, string>
        {
            ["api.parameter.MemorySize"] = "512",
            ["api.parameter.Timeout"] = "30",
            ["api.template"] = "not-a-parameter",
        });

        var parameters = request.OptionSet("api", "parameter");

        Assert.Equal(2, parameters.Count);
        Assert.Equal("512", parameters["MemorySize"]);
        Assert.Equal("30", parameters["Timeout"]);
    }

    [Fact]
    public void An_option_SET_merges_the_shared_group_with_the_units_own_and_the_unit_wins()
    {
        var request = Request(new Dictionary<string, string>
        {
            ["parameter.Stage"] = "prod",              // every unit
            ["parameter.Region"] = "ap-southeast-2",   // every unit
            ["api.parameter.Stage"] = "canary",        // …except this one
            ["web.parameter.Stage"] = "beta",          // another unit's, must not leak in
        });

        var parameters = request.OptionSet("api", "parameter");

        Assert.Equal(2, parameters.Count);
        Assert.Equal("canary", parameters["Stage"]);
        Assert.Equal("ap-southeast-2", parameters["Region"]);
    }

    [Fact]
    public void An_option_SET_with_nothing_in_it_is_empty_rather_than_null()
        // So a provider can hand it straight to a request builder without a null check that would quietly
        // mean "no parameters" either way.
        => Assert.Empty(Request(new Dictionary<string, string> { ["api.template"] = "x" }).OptionSet("api", "parameter"));

    [Fact]
    public void An_option_SET_ignores_a_bare_prefix_with_no_key_after_it()
    {
        // "parameter." names nothing. Including it would put an empty-string key in a map a provider is
        // about to turn into API arguments.
        var request = Request(new Dictionary<string, string> { ["parameter."] = "orphan", ["parameter.Real"] = "yes" });

        Assert.Equal(["Real"], request.OptionSet("api", "parameter").Keys);
    }

    [Fact]
    public void A_request_with_no_options_at_all_reads_as_empty_everywhere()
    {
        var request = new DeploymentRequest("acme", new DeploymentArtifact(new Dictionary<string, string>()));

        Assert.Null(request.Option("web", "kind"));
        Assert.Null(request.OwnOption("web", "kind"));
        Assert.Empty(request.OptionSet("web", "parameter"));
    }

    [Fact]
    public void An_OWN_option_does_not_inherit_the_procedure_wide_one()
    {
        // For a setting that IS the unit's identity — where it lives on disk, which bucket it fills. The
        // fallback that makes Option(unit, key) convenient is exactly wrong there: a procedure-wide "path"
        // does not mean "every unit defaults to this directory", it means every unit deploys ON TOP of every
        // other, silently, and removing one removes them all.
        var request = Request(new Dictionary<string, string> { ["path"] = "/srv/shared", ["web.path"] = "/srv/web" });

        Assert.Equal("/srv/web", request.OwnOption("web", "path"));
        Assert.Null(request.OwnOption("db", "path"));            // …rather than inheriting /srv/shared

        // The convenient reader still inherits, which is why both exist.
        Assert.Equal("/srv/shared", request.Option("db", "path"));
    }

    // ── an ADDRESS: OwnOption with the silence taken out ─────────────────────────────────────────

    [Fact]
    public void An_ADDRESS_reads_the_units_own_setting()
    {
        var request = Request(new Dictionary<string, string> { ["web.path"] = "/srv/web" });

        Assert.Equal("/srv/web", request.Address("web", "path"));
    }

    [Fact]
    public void An_ADDRESS_nobody_set_is_null_so_the_caller_can_have_a_default()
        // LocalPaths falls back to {root}/{prefix}/{unit}; a content unit treats it as "no bucket named".
        // Neither is this method's decision to make.
        => Assert.Null(Request([]).Address("web", "path"));

    [Fact]
    public void An_ADDRESS_written_procedure_wide_is_REFUSED_rather_than_shared_or_dropped()
    {
        // The two ways this went wrong before, which is why the method exists: read with the convenient
        // reader it is SHARED — every unit deploys on top of every other — and read with OwnOption it is
        // silently DROPPED, so the line the operator wrote is read by nothing at all. Both shipped providers
        // had one of the two (docs/DECISIONS.md D36).
        var request = Request(new Dictionary<string, string> { ["path"] = "/srv/shared" });

        var thrown = Assert.Throws<OptionException>(() => request.Address("web", "path"));

        Assert.Contains("\"web.path\"", thrown.Message);          // …and says the spelling that would work
    }

    [Fact]
    public void An_ADDRESS_refusal_is_a_DEFINITION_error_so_ValidateAsync_collects_it()
    {
        // The whole reason it throws rather than returning a result: UnitProblems.Check catches
        // DefinitionException, so ONE call gives a driver both the offline report and the apply-time
        // refusal, and the two cannot drift apart.
        var request = Request(new Dictionary<string, string> { ["bucket"] = "shared" });

        Assert.IsAssignableFrom<DefinitionException>(
            Assert.Throws<OptionException>(() => request.Address("web", "bucket")));
    }

    [Fact]
    public void A_units_OWN_address_wins_over_a_stray_procedure_wide_one_rather_than_being_refused_too()
    {
        // Deliberate: the refusal fires exactly where a value would otherwise be used or dropped in silence.
        // A unit that named its own address has nothing silently wrong with it, and reporting the stray line
        // once per unit would name every unit except the one line to delete.
        var request = Request(new Dictionary<string, string> { ["path"] = "/srv/shared", ["web.path"] = "/srv/web" });

        Assert.Equal("/srv/web", request.Address("web", "path"));
        Assert.Throws<OptionException>(() => request.Address("db", "path"));    // …but this one IS silent
    }

    [Fact]
    public void A_context_reads_an_address_for_ITS_unit()
    {
        // The shorthand a driver actually uses, so it does not thread the unit name through every call.
        var request = Request(new Dictionary<string, string> { ["web.bucket"] = "web-files" });
        var context = new UnitContext(new ProcedureUnit("web", "Website"), request);

        Assert.Equal("web-files", context.Address("bucket"));
        Assert.Null(new UnitContext(new ProcedureUnit("db", "Database"), request).Address("bucket"));
    }
}
