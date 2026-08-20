using Tyanor.Testing;
using Xunit;

namespace Tyanor.Providers.Local.Tests;

/// <summary>
/// What this provider leaves on the machine after a teardown — which, until D33, was a folder per deployment
/// and a <c>.tyanor</c> folder inside it, for ever.
///
/// <para><b>The same gap the AWS provider had, wearing different clothes.</b> Pid files live outside the
/// unit directories deliberately, so that removing a unit removes exactly what was deployed and none of
/// ours — which left nobody able to remove ours. Real files here, as everywhere in this project: a mocked
/// filesystem would agree with whatever the target believed, which is the opposite of a test.</para>
/// </summary>
public class LocalSweepTests : IDisposable
{
    private readonly Sandbox _box = new();

    private static readonly Procedure Server = new("server",
    [
        new ProcedureUnit("runtime", "Application files"),
    ]);

    private DeploymentRequest Request(string prefix = "acme") =>
        new(prefix, new DeploymentArtifact(new Dictionary<string, string> { ["app"] = _box.Artifact }),
            new Dictionary<string, string>
            {
                ["runtime.kind"] = LocalOptions.DirectoryKind,
                ["runtime.source"] = "app",
            });

    public void Dispose() => _box.Dispose();

    // ── the contract ─────────────────────────────────────────────────────────────────────────────

    public static TheoryData<string> Checks() =>
        Suites.Names(new DeploymentTargetContract(null!, "", new DeploymentRequest("x", new(new Dictionary<string, string>()))));

    [Theory]
    [MemberData(nameof(Checks))]
    public Task The_local_target_satisfies(string check) =>
        new DeploymentTargetContract(_box.Target, "server", Request()).AssertAsync(check);

    // ── what a full teardown actually leaves ─────────────────────────────────────────────────────

    [Fact]
    public async Task A_destroy_leaves_nothing_on_the_machine()
    {
        // The claim `adoption.md` makes out loud, checked against a real directory rather than believed.
        _box.Publish("app.dll", "v1");
        await _box.Runner.ApplyAsync(Server, Request());
        Assert.True(Directory.Exists(Path.Combine(_box.Root, "acme")));

        await _box.Runner.DestroyAsync(Server, Request());

        Assert.False(Directory.Exists(Path.Combine(_box.Root, "acme")));
        Assert.True(Directory.Exists(_box.Root));            // …the ROOT is the operator's, not ours
    }

    [Fact]
    public async Task The_bookkeeping_folder_goes_even_though_no_unit_owns_it()
    {
        // `.tyanor` holds pid files and belongs to the provider rather than to any unit — which is exactly
        // why it survived every teardown before there was a sweep.
        _box.Publish("app.dll", "v1");
        var bookkeeping = Path.Combine(_box.Root, "acme", ".tyanor");
        await _box.Runner.ApplyAsync(Server, Request());
        Directory.CreateDirectory(bookkeeping);

        await _box.Runner.DestroyAsync(Server, Request());

        Assert.False(Directory.Exists(bookkeeping));
    }

    [Fact]
    public async Task A_second_destroy_is_fine()
    {
        // A teardown is re-runnable — that is how an interrupted one is finished — so the second sweep meets
        // what the first one already took away.
        _box.Publish("app.dll", "v1");
        await _box.Runner.ApplyAsync(Server, Request());

        await _box.Runner.DestroyAsync(Server, Request());
        var outcome = await _box.Runner.DestroyAsync(Server, Request());

        Assert.True(outcome.Ok);
    }

    // ── what it must NOT take ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_deployment_folder_holding_anything_else_is_LEFT()
    {
        // The safety property, and the reason this is "delete if empty" rather than "delete recursively".
        // Anything still in there means something is still deployed — a unit pointed elsewhere by `path`, a
        // retained one, or a file the operator put there — and a sweep taking it would remove what the
        // teardown deliberately did not.
        _box.Publish("app.dll", "v1");
        await _box.Runner.ApplyAsync(Server, Request());
        var deployment = Path.Combine(_box.Root, "acme");
        await File.WriteAllTextAsync(Path.Combine(deployment, "operator-notes.txt"), "do not delete");

        var lines = new List<ProgressReport>();
        await _box.Runner.DestroyAsync(Server, Request(), lines.Add);

        Assert.True(Directory.Exists(deployment));
        Assert.True(File.Exists(Path.Combine(deployment, "operator-notes.txt")));

        // Left DELIBERATELY, not because the delete threw. Without this the check passes either way — the
        // engine swallows a failing sweep, so a target that tried to remove a non-empty folder and was
        // refused by the OS looks identical to one that correctly declined to try. That is the shape of
        // defect this repository keeps finding: behaviour defined by absence, guarded by a check that
        // cannot go red.
        Assert.DoesNotContain(lines, l => l.Status == ProgressStatus.Error);
    }

    [Fact]
    public async Task Another_deployment_under_the_same_root_is_untouched()
    {
        // Two prefixes are two deployments on one machine — the whole reason a prefix exists. A sweep scoped
        // to the root rather than to the prefix would destroy the other one.
        _box.Publish("app.dll", "v1");
        await _box.Runner.ApplyAsync(Server, Request("acme"));
        await _box.Runner.ApplyAsync(Server, Request("beta"));

        await _box.Runner.DestroyAsync(Server, Request("acme"));

        Assert.False(Directory.Exists(Path.Combine(_box.Root, "acme")));
        Assert.True(Directory.Exists(Path.Combine(_box.Root, "beta", "runtime")));
    }

    [Fact]
    public async Task A_narrowed_destroy_leaves_the_deployment_folder_standing()
    {
        // The engine's promise rather than this target's, checked here because this is where it would be
        // felt: narrowing away one unit of a two-unit deployment must not remove the folder the other lives
        // in.
        var two = new Procedure("server",
        [
            new ProcedureUnit("runtime", "Application files"),
            new ProcedureUnit("extra", "More files"),
        ]);
        var request = new DeploymentRequest("acme",
            new DeploymentArtifact(new Dictionary<string, string> { ["app"] = _box.Artifact }),
            new Dictionary<string, string>
            {
                ["kind"] = LocalOptions.DirectoryKind,
                ["source"] = "app",
            });
        _box.Publish("app.dll", "v1");
        await _box.Runner.ApplyAsync(two, request);

        await _box.Runner.DestroyAsync(two.Only("extra"), request);

        Assert.True(Directory.Exists(Path.Combine(_box.Root, "acme", "runtime")));
        Assert.False(Directory.Exists(Path.Combine(_box.Root, "acme", "extra")));
    }
}
