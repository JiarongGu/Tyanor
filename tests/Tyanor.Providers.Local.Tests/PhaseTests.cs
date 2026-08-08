using System.Diagnostics;
using Xunit;

namespace Tyanor.Providers.Local.Tests;

/// <summary>
/// The phase table for a directory unit — the mapping that everything else is decided from.
/// </summary>
public class DirectoryPhaseTests
{
    private static readonly ProcedureUnit Runtime = new("runtime", "Application files");

    private static DeploymentRequest Request(Sandbox box) => new("acme",
        new DeploymentArtifact(new Dictionary<string, string> { ["app"] = box.Artifact }),
        new Dictionary<string, string>
        {
            ["runtime.kind"] = LocalOptions.DirectoryKind,
            ["runtime.source"] = "app",
        });

    [Fact]
    public async Task Nothing_there_is_Missing()
    {
        using var box = new Sandbox();

        Assert.Equal(UnitPhase.Missing, await box.Target.Driver.PhaseAsync(new UnitContext(Runtime, Request(box))));
    }

    [Fact]
    public async Task An_empty_directory_is_Missing_not_Ready()
    {
        // Somebody made the folder. That is not a deployment, and calling it Ready would skip the create.
        using var box = new Sandbox();
        Directory.CreateDirectory(box.Deployed("acme", "runtime"));

        Assert.Equal(UnitPhase.Missing, await box.Target.Driver.PhaseAsync(new UnitContext(Runtime, Request(box))));
    }

    [Fact]
    public async Task Files_with_no_marker_are_BROKEN_because_that_is_an_interrupted_copy()
    {
        // The marker is written last, so its absence beside real files means the copy did not finish. This
        // is the local equivalent of ROLLBACK_COMPLETE: settled, and not something to update in place.
        using var box = new Sandbox();
        var deployed = box.Deployed("acme", "runtime");
        Directory.CreateDirectory(deployed);
        await File.WriteAllTextAsync(Path.Combine(deployed, "half-copied.dll"), "…");

        Assert.Equal(UnitPhase.Broken, await box.Target.Driver.PhaseAsync(new UnitContext(Runtime, Request(box))));
    }

    [Fact]
    public async Task A_materialized_directory_is_Ready()
    {
        using var box = new Sandbox();
        box.Publish("app.dll", "v1");
        var request = Request(box);

        await box.Target.Driver.CreateAsync(new UnitContext(Runtime, request));

        Assert.Equal(UnitPhase.Ready, await box.Target.Driver.PhaseAsync(new UnitContext(Runtime, request)));
        Assert.Equal("v1", await File.ReadAllTextAsync(Path.Combine(box.Live("acme", "runtime"), "app.dll")));
    }

    [Fact]
    public async Task Update_reports_NO_CHANGE_when_neither_the_build_nor_the_deployment_moved()
    {
        // On a resume this is the ordinary answer, and it has to be a success rather than a no-op dressed
        // up as an error.
        using var box = new Sandbox();
        box.Publish("app.dll", "v1");
        var request = Request(box);
        await box.Target.Driver.CreateAsync(new UnitContext(Runtime, request));

        Assert.False(await box.Target.Driver.UpdateAsync(new UnitContext(Runtime, request)));
    }

    [Fact]
    public async Task Update_recopies_when_there_is_a_new_build()
    {
        using var box = new Sandbox();
        box.Publish("app.dll", "v1");
        var request = Request(box);
        await box.Target.Driver.CreateAsync(new UnitContext(Runtime, request));

        box.Publish("app.dll", "v2");

        Assert.True(await box.Target.Driver.UpdateAsync(new UnitContext(Runtime, request)));
        Assert.Equal("v2", await File.ReadAllTextAsync(Path.Combine(box.Live("acme", "runtime"), "app.dll")));
    }

    [Fact]
    public async Task Update_repairs_a_deployment_someone_edited_by_hand()
    {
        // The question people do not expect the tool to ask, and the one that makes it worth trusting: a
        // hand-patched server that survives every redeploy is how a machine drifts away from its recipe.
        using var box = new Sandbox();
        box.Publish("app.dll", "v1");
        var request = Request(box);
        await box.Target.Driver.CreateAsync(new UnitContext(Runtime, request));

        await File.WriteAllTextAsync(Path.Combine(box.Live("acme", "runtime"), "app.dll"), "hand-patched");

        Assert.True(await box.Target.Driver.UpdateAsync(new UnitContext(Runtime, request)));
        Assert.Equal("v1", await File.ReadAllTextAsync(Path.Combine(box.Live("acme", "runtime"), "app.dll")));
    }

    [Fact]
    public async Task An_interrupted_redeploy_leaves_the_PREVIOUS_build_serving()
    {
        // The ordering that makes this true: the new release is copied beside the old one, and only then
        // does the marker move. Until it does, the deployment still names a release that was never touched.
        // Get this backwards — clear the marker first, as an in-place copy has to — and an interrupted
        // redeploy takes down a version that was working perfectly.
        using var box = new Sandbox();
        box.Publish("app.dll", "v1");
        var request = Request(box);
        await box.Target.Driver.CreateAsync(new UnitContext(Runtime, request));

        box.Publish("app.dll", "v2");
        using var interrupted = new CancellationTokenSource();
        await interrupted.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => box.Target.Driver.UpdateAsync(
                new UnitContext(Runtime, request, _ => { }, interrupted.Token)));

        Assert.Equal(UnitPhase.Ready, await box.Target.Driver.PhaseAsync(new UnitContext(Runtime, request)));
        Assert.Equal("v1", await File.ReadAllTextAsync(Path.Combine(box.Live("acme", "runtime"), "app.dll")));
    }

    [Fact]
    public async Task Refresh_reports_what_is_ACTUALLY_there_not_what_was_recorded()
    {
        using var box = new Sandbox();
        box.Publish("app.dll", "v1");
        var request = Request(box);
        await box.Target.Driver.CreateAsync(new UnitContext(Runtime, request));

        var before = Assert.Single(await box.Target.Driver.RefreshAsync(new UnitContext(Runtime, request)));
        await File.WriteAllTextAsync(Path.Combine(box.Live("acme", "runtime"), "app.dll"), "edited");
        var after = Assert.Single(await box.Target.Driver.RefreshAsync(new UnitContext(Runtime, request)));

        Assert.Equal(box.Deployed("acme", "runtime"), before.Id);       // identity survives the edit
        Assert.Equal("local/directory", before.Type);
        Assert.NotEqual(before.Fingerprint, after.Fingerprint);         // …and the fingerprint does not
    }

    [Fact]
    public async Task Remove_deletes_it_and_a_second_remove_is_not_an_error()
    {
        using var box = new Sandbox();
        box.Publish("app.dll", "v1");
        var request = Request(box);
        await box.Target.Driver.CreateAsync(new UnitContext(Runtime, request));

        await box.Target.Driver.RemoveAsync(new UnitContext(Runtime, request));
        await box.Target.Driver.RemoveAsync(new UnitContext(Runtime, request));   // teardown must be re-runnable

        Assert.False(Directory.Exists(box.Deployed("acme", "runtime")));
        Assert.Empty(await box.Target.Driver.RefreshAsync(new UnitContext(Runtime, request)));
    }

    [Fact]
    public async Task An_artifact_part_that_is_not_there_fails_HARD_before_anything_is_touched()
    {
        using var box = new Sandbox();
        var request = new DeploymentRequest("acme",
            new DeploymentArtifact(new Dictionary<string, string> { ["app"] = box.Artifact }),
            new Dictionary<string, string>
            {
                ["runtime.kind"] = LocalOptions.DirectoryKind,
                ["runtime.source"] = "the-one-nobody-built",
            });

        // Core's check, shared with every other provider, so an operator gets the same sentence about the
        // same mistake wherever they deploy.
        var error = await Assert.ThrowsAsync<ArtifactException>(
            () => box.Target.Driver.CreateAsync(new UnitContext(Runtime, request)));

        Assert.Contains("app", error.Message);                          // says what the artifact DOES carry
        Assert.False(Directory.Exists(box.Deployed("acme", "runtime")));
    }

    [Fact]
    public async Task A_unit_that_does_not_say_what_it_is_fails_rather_than_being_guessed()
    {
        using var box = new Sandbox();
        var request = new DeploymentRequest("acme", new DeploymentArtifact(new Dictionary<string, string>()));

        var error = await Assert.ThrowsAsync<UnitKindException>(
            () => box.Target.Driver.PhaseAsync(new UnitContext(Runtime, request)));

        Assert.Contains(LocalOptions.DirectoryKind, error.Message);      // names the kinds that DO exist
        Assert.Contains(LocalOptions.ProcessKind, error.Message);
    }

    [Fact]
    public async Task Everything_wrong_with_a_DEFINITION_is_catchable_as_one_thing()
    {
        // A wrong definition and a server that would not start are different situations for whoever is
        // reading, and telling them apart must not require matching on message text.
        using var box = new Sandbox();
        var request = new DeploymentRequest("acme", new DeploymentArtifact(new Dictionary<string, string>()),
            new Dictionary<string, string> { ["runtime.kind"] = LocalOptions.DirectoryKind });

        Assert.IsAssignableFrom<DefinitionException>(await Record.ExceptionAsync(
            () => box.Target.Driver.CreateAsync(new UnitContext(Runtime, request))));

        // …and Core's exceptions need no help from the provider's classifier: null means Hard to the engine,
        // which is what a wrong definition is.
        Assert.Null(box.Target.Classifier.Classify(new LocalConfigurationException("runtime", "no source")));
    }
}

/// <summary>
/// The phase table for a process unit — where <see cref="UnitPhase.Converging"/> has to be built out of a
/// pid and a port, because nothing on a machine keeps converging on its own.
/// </summary>
public class ProcessPhaseTests
{
    private static readonly ProcedureUnit Service = new("service", "Server");

    private static DeploymentRequest Request(Sandbox box, int? healthPort = null, int? graceSeconds = null)
    {
        var (command, arguments) = Sandbox.Sleeper;
        var options = new Dictionary<string, string>
        {
            ["service.kind"] = LocalOptions.ProcessKind,
            ["service.command"] = command,
            ["service.args"] = arguments,
            ["service.workDir"] = box.Root,
        };
        if (healthPort is not null) options["service.health.port"] = healthPort.Value.ToString();
        if (graceSeconds is not null) options["service.health.seconds"] = graceSeconds.Value.ToString();

        return new DeploymentRequest("acme", new DeploymentArtifact(new Dictionary<string, string>()), options);
    }

    [Fact]
    public async Task Nothing_recorded_is_Missing()
    {
        using var box = new Sandbox();

        Assert.Equal(UnitPhase.Missing, await box.Target.Driver.PhaseAsync(new UnitContext(Service, Request(box))));
    }

    [Fact]
    public async Task A_running_process_with_no_health_check_is_Ready()
    {
        // Stated plainly rather than dressed up: with nothing to probe, "the process is alive" is the most
        // that can honestly be claimed, and it IS what the operator asked for.
        using var box = new Sandbox();
        var request = Request(box);

        await box.Target.Driver.CreateAsync(new UnitContext(Service, request));

        Assert.Equal(UnitPhase.Ready, await box.Target.Driver.PhaseAsync(new UnitContext(Service, request)));
        Assert.NotNull(box.Pid("acme", "service"));
    }

    [Fact]
    public async Task Alive_but_not_yet_answering_is_CONVERGING()
    {
        // The phase that makes Attach possible, and the one a machine gives away for free to nobody: it
        // has to be inferred from "the process is up" plus "the port is not".
        using var box = new Sandbox();
        var request = Request(box, healthPort: Sandbox.FreePort(), graceSeconds: 60);

        await box.Target.Driver.CreateAsync(new UnitContext(Service, request));

        Assert.Equal(UnitPhase.Converging, await box.Target.Driver.PhaseAsync(new UnitContext(Service, request)));
    }

    [Fact]
    public async Task Answering_on_its_port_is_READY()
    {
        using var box = new Sandbox();
        var port = Sandbox.FreePort();
        using var server = Sandbox.Listen(port);          // stands in for the server's own socket
        var request = Request(box, healthPort: port, graceSeconds: 60);

        await box.Target.Driver.CreateAsync(new UnitContext(Service, request));

        Assert.Equal(UnitPhase.Ready, await box.Target.Driver.PhaseAsync(new UnitContext(Service, request)));
    }

    [Fact]
    public async Task Still_not_answering_once_the_grace_window_closes_is_BROKEN()
    {
        // Not Converging forever. A server that is never going to boot must stop looking like one that is
        // about to, or the run hangs instead of telling anyone.
        using var box = new Sandbox();
        var request = Request(box, healthPort: Sandbox.FreePort(), graceSeconds: 1);

        await box.Target.Driver.CreateAsync(new UnitContext(Service, request));
        await Task.Delay(1_300);

        Assert.Equal(UnitPhase.Broken, await box.Target.Driver.PhaseAsync(new UnitContext(Service, request)));
    }

    [Fact]
    public async Task A_process_that_died_leaves_a_stale_record_and_reads_as_BROKEN()
    {
        using var box = new Sandbox();
        var request = Request(box);
        await box.Target.Driver.CreateAsync(new UnitContext(Service, request));

        using (var victim = Process.GetProcessById(box.Pid("acme", "service")!.Value))
        {
            victim.Kill(entireProcessTree: true);          // the server crashed; nothing tidied up
            await victim.WaitForExitAsync();
        }

        Assert.Equal(UnitPhase.Broken, await box.Target.Driver.PhaseAsync(new UnitContext(Service, request)));
        Assert.Empty(await box.Target.Driver.RefreshAsync(new UnitContext(Service, request)));   // it owns nothing now
    }

    [Fact]
    public async Task A_pid_the_OS_has_handed_to_someone_else_is_NOT_mistaken_for_ours()
    {
        // The hazard this provider has and a cloud provider does not: pids are reused. A tool that kills
        // whatever currently holds a remembered number eventually kills something that has nothing to do
        // with the deployment.
        //
        // The record below names THIS test process with a start time that is not its own. If the guard
        // regressed, the RemoveAsync here would kill the test host — a failure that is impossible to
        // misread.
        using var box = new Sandbox();
        var request = Request(box);
        Records.Write(
            Path.Combine(box.Root, "acme", ".tyanor", "service.pid.json"),
            new ProcessRecord(Environment.ProcessId, DateTimeOffset.UtcNow.AddDays(-1), "not-ours", "x"));

        Assert.Equal(UnitPhase.Broken, await box.Target.Driver.PhaseAsync(new UnitContext(Service, request)));

        await box.Target.Driver.RemoveAsync(new UnitContext(Service, request));

        Assert.False(Process.GetCurrentProcess().HasExited);
        Assert.Null(box.Pid("acme", "service"));           // the stale record is cleared, though
    }

    [Fact]
    public async Task Remove_stops_it_and_a_second_remove_is_not_an_error()
    {
        using var box = new Sandbox();
        var request = Request(box);
        await box.Target.Driver.CreateAsync(new UnitContext(Service, request));
        var pid = box.Pid("acme", "service")!.Value;

        await box.Target.Driver.RemoveAsync(new UnitContext(Service, request));
        await box.Target.Driver.RemoveAsync(new UnitContext(Service, request));

        Assert.Equal(UnitPhase.Missing, await box.Target.Driver.PhaseAsync(new UnitContext(Service, request)));
        Assert.Throws<ArgumentException>(() => Process.GetProcessById(pid));
    }

    [Fact]
    public async Task A_command_that_does_not_exist_fails_in_a_way_the_classifier_calls_HARD()
    {
        using var box = new Sandbox();
        var request = new DeploymentRequest("acme", new DeploymentArtifact(new Dictionary<string, string>()),
            new Dictionary<string, string>
            {
                ["service.kind"] = LocalOptions.ProcessKind,
                ["service.command"] = "tyanor-no-such-executable",
                ["service.workDir"] = box.Root,
            });

        var error = await Record.ExceptionAsync(() => box.Target.Driver.CreateAsync(new UnitContext(Service, request)));

        Assert.NotNull(error);
        Assert.Equal(FailureClass.Hard, box.Target.Classifier.Classify(error));
    }
}
