using System.Diagnostics;
using Xunit;

namespace Tyanor.Providers.Local.Tests;

/// <summary>
/// A self-hosted server, deployed for real: files unpacked from an artifact, a process started from them,
/// a health check on its port, and a teardown that leaves nothing behind.
///
/// <para><b>This is the second consumer shape</b> — a long-lived process on a machine — run against the
/// same engine as the static-site-plus-API shape the engine was extracted from. Nothing here mocks the
/// provider: these tests copy files and start processes, because the interesting failures of a provider
/// with no control plane are precisely the ones a mock would agree with.</para>
/// </summary>
public class SelfHostedServerTests
{
    private static readonly ProcedureUnit Runtime = new("runtime", "Application files");
    private static readonly ProcedureUnit Service = new("service", "Server", Weight: 3);

    /// <summary>The Daoris shape: unpack, then run what was unpacked.</summary>
    private static readonly Procedure Server = new("server", [Runtime, Service]);

    private static DeploymentRequest Request(Sandbox box, int? healthPort = null, int graceSeconds = 60)
    {
        var (command, arguments) = Sandbox.Sleeper;
        var options = new Dictionary<string, string>
        {
            ["runtime.kind"] = LocalOptions.DirectoryKind,
            ["runtime.source"] = "app",
            ["service.kind"] = LocalOptions.ProcessKind,
            ["service.command"] = command,
            ["service.args"] = arguments,
            ["service.watch"] = "runtime",              // a new build in `runtime` restarts the server
            ["service.health.seconds"] = graceSeconds.ToString(),
        };
        if (healthPort is not null) options["service.health.port"] = healthPort.Value.ToString();

        return new DeploymentRequest("acme",
            new DeploymentArtifact(new Dictionary<string, string> { ["app"] = box.Artifact }), options);
    }

    [Fact]
    public async Task A_server_deploys_end_to_end_and_the_plan_says_what_it_will_do_first()
    {
        using var box = new Sandbox();
        box.Publish("Server.dll", "v1");
        var request = Request(box);

        var before = await box.Runner.PlanAsync(Server, request);
        Assert.Equal([ReconcileAction.Create, ReconcileAction.Create], before.Steps.Select(s => s.Action));
        // Steps are UNITS and the counts are RESOURCES: nothing is recorded and nothing is deployed, so
        // there is no DRIFT even though two units will be created. Conflating the two would make one wrong.
        Assert.Equal("0 to add, 0 to change, 0 to destroy", before.Summary);
        Assert.False(before.IsNoOp);

        var outcome = await box.Runner.ApplyAsync(Server, request);

        Assert.True(outcome.Ok);
        Assert.Equal("v1", await File.ReadAllTextAsync(Path.Combine(box.Live("acme", "runtime"), "Server.dll")));
        Assert.NotNull(box.Pid("acme", "service"));
        Assert.False(Process.GetProcessById(box.Pid("acme", "service")!.Value).HasExited);
    }

    [Fact]
    public async Task State_records_what_Tyanor_owns_which_is_the_only_thing_that_can_answer_it_here()
    {
        // A machine cannot be asked "what did you create?" — there is no stack membership, no tag, no
        // grouping of any kind. Without this record a teardown could not tell the directory it deployed
        // from one that was already there, which is the difference between a safe destroy and a
        // destructive one. This is D12 arrived at from the provider that needs it most.
        using var box = new Sandbox();
        box.Publish("Server.dll", "v1");
        var request = Request(box);
        await box.Runner.ApplyAsync(Server, request);

        var state = await box.State.GetAsync("server", "acme");

        Assert.Equal(["runtime", "service"], state.Units.Select(u => u.Unit).Order());
        Assert.Equal(box.Deployed("acme", "runtime"), Assert.Single(state.For("runtime")).Id);
        Assert.Equal("local/process", Assert.Single(state.For("service")).Type);
    }

    [Fact]
    public async Task Applying_the_same_build_again_changes_nothing_and_does_not_restart_the_server()
    {
        using var box = new Sandbox();
        box.Publish("Server.dll", "v1");
        var request = Request(box);
        await box.Runner.ApplyAsync(Server, request);
        var pid = box.Pid("acme", "service");

        var plan = await box.Runner.PlanAsync(Server, request);
        var outcome = await box.Runner.ApplyAsync(Server, request);

        Assert.Equal([ReconcileAction.Update, ReconcileAction.Update], plan.Steps.Select(s => s.Action));
        Assert.False(plan.HasDrift);
        Assert.True(outcome.Ok);
        // The one that would be invisible in production: a redeploy that needlessly bounces the server
        // looks exactly like a redeploy that did not.
        Assert.Equal(pid, box.Pid("acme", "service"));
    }

    [Fact]
    public async Task A_new_build_recopies_the_files_and_restarts_the_server()
    {
        // Ordering doing its job, with no dependency graph anywhere: `runtime` is applied first, so by the
        // time `service` is reconciled the new content is already on disk and its fingerprint has moved.
        // "A needs B" is expressed by putting B first (D3).
        using var box = new Sandbox();
        box.Publish("Server.dll", "v1");
        var request = Request(box);
        await box.Runner.ApplyAsync(Server, request);
        var before = box.Pid("acme", "service");

        box.Publish("Server.dll", "v2");
        var outcome = await box.Runner.ApplyAsync(Server, request);

        Assert.True(outcome.Ok);
        Assert.Equal("v2", await File.ReadAllTextAsync(Path.Combine(box.Live("acme", "runtime"), "Server.dll")));
        Assert.NotEqual(before, box.Pid("acme", "service"));
    }

    [Fact]
    public async Task A_new_build_is_written_BESIDE_the_running_one_and_old_builds_are_pruned_later()
    {
        // The constraint that looked like it needed a dependency graph. To replace the files a server is
        // running out of, the server has to be down — so `service` would have to come both after `runtime`
        // (it needs the files) and before it (it must stop first). Ordering cannot express that.
        //
        // It does not have to. Writing the new build to a new directory means nothing ever conflicts, the
        // list stays a list, and the restart falls out of the fingerprint changing. What could not be
        // solved by reordering was solved by changing the operation (D13).
        using var box = new Sandbox();
        box.Publish("Server.dll", "v1");
        var request = Request(box);
        await box.Runner.ApplyAsync(Server, request);
        Assert.Equal(1, box.Releases("acme", "runtime"));

        box.Publish("Server.dll", "v2");
        await box.Runner.ApplyAsync(Server, request);

        // Two: the new one, and the one the server was still sitting in when it was written. Pruning is
        // deliberately best-effort — failing a deployment over disk tidiness would be the wrong trade.
        Assert.Equal(2, box.Releases("acme", "runtime"));

        box.Publish("Server.dll", "v3");
        await box.Runner.ApplyAsync(Server, request);

        // Still two, not three: v1 was free by now and went. The count stays bounded without anything
        // needing to track it.
        Assert.Equal(2, box.Releases("acme", "runtime"));
        Assert.Equal("v3", await File.ReadAllTextAsync(Path.Combine(box.Live("acme", "runtime"), "Server.dll")));
    }

    [Fact]
    public async Task An_edit_made_outside_Tyanor_shows_up_as_DRIFT_and_applying_repairs_it()
    {
        using var box = new Sandbox();
        box.Publish("Server.dll", "v1");
        var request = Request(box);
        await box.Runner.ApplyAsync(Server, request);

        var deployed = Path.Combine(box.Live("acme", "runtime"), "Server.dll");
        await File.WriteAllTextAsync(deployed, "someone patched this in place");

        var drifted = await box.Runner.PlanAsync(Server, request);
        Assert.True(drifted.HasDrift);
        Assert.Equal("0 to add, 1 to change, 0 to destroy", drifted.Summary);

        await box.Runner.ApplyAsync(Server, request);

        // Repaired by APPLYING, not by editing the tool's bookkeeping. That is the failure mode a
        // state-file tool is judged on, and the reason keeping state is affordable at all.
        Assert.Equal("v1", await File.ReadAllTextAsync(deployed));
        Assert.False((await box.Runner.PlanAsync(Server, request)).HasDrift);
    }

    [Fact]
    public async Task A_second_run_ATTACHES_to_a_starting_server_instead_of_launching_a_second_one()
    {
        // The doctrine's sharpest edge, on the provider least able to help with it. There is no control
        // plane here to reject a duplicate — starting a second server would simply WORK, produce two
        // processes racing for one port, and look like a slow deploy.
        using var box = new Sandbox();
        box.Publish("Server.dll", "v1");
        var port = Sandbox.FreePort();
        var request = Request(box, healthPort: port);

        // Started out of band: another operator, a pipeline, or a run whose process has since gone away.
        await box.Target.Driver.CreateAsync(Runtime, request, default);
        await box.Target.Driver.CreateAsync(Service, request, default);
        var started = box.Pid("acme", "service");

        var plan = await box.Runner.PlanAsync(Server, request);
        Assert.Equal(ReconcileAction.Attach, plan.Steps.Single(s => s.Unit.Name == "service").Action);
        Assert.True(plan.HasWorkInFlight);

        // The server finishes booting while the second run is watching it.
        var booting = Task.Run(async () => { await Task.Delay(300); return Sandbox.Listen(port); });
        var outcome = await box.Runner.ApplyAsync(Server, request);
        (await booting).Stop();

        Assert.True(outcome.Ok);
        Assert.Equal(started, box.Pid("acme", "service"));      // watched, never re-issued
    }

    [Fact]
    public async Task A_server_that_never_answers_PAUSES_the_run_rather_than_failing_it()
    {
        // Nothing about the desired state is wrong — it is slow, or it is wedged, and only a later phase
        // read can tell which. Pausing keeps the run live and keeps everything already deployed.
        using var box = new Sandbox();
        box.Publish("Server.dll", "v1");
        var request = Request(box, healthPort: Sandbox.FreePort(), graceSeconds: 2);

        var outcome = await box.Runner.ApplyAsync(Server, request);

        Assert.False(outcome.Ok);
        Assert.True(outcome.Resumable);
        Assert.Equal("transient", outcome.Reason?.Value);

        var live = await box.History.LiveAsync("server", "acme");
        Assert.NotNull(live);
        Assert.Equal(RunStatus.Paused, live.Status);
        // The unit that finished before the pause is still recorded — state is written per unit AS THE RUN
        // GOES, because a run that stops halfway has still created things.
        Assert.NotEmpty((await box.State.GetAsync("server", "acme")).For("runtime"));
    }

    [Fact]
    public async Task Teardown_runs_in_reverse_and_leaves_nothing_running_and_nothing_on_disk()
    {
        using var box = new Sandbox();
        box.Publish("Server.dll", "v1");
        var request = Request(box);
        await box.Runner.ApplyAsync(Server, request);
        var pid = box.Pid("acme", "service")!.Value;

        var outcome = await box.Runner.RemoveAsync(Server, request);

        Assert.True(outcome.Ok);
        // Reverse order is what makes this safe: the process is stopped before the files it is running
        // are deleted out from under it.
        Assert.Throws<ArgumentException>(() => Process.GetProcessById(pid));
        Assert.False(Directory.Exists(box.Deployed("acme", "runtime")));
        Assert.Empty((await box.State.GetAsync("server", "acme")).Units);
    }

    [Fact]
    public async Task Two_differently_shaped_procedures_run_on_ONE_engine()
    {
        // The acceptance test for proving the abstraction. One shape is homogeneous and static — every
        // unit the same kind of thing, nothing alive — which is the shape the engine was extracted from.
        // The other is heterogeneous and has a running process in it. Same runner, same reconcile, same
        // classifier; the only difference is configuration.
        using var box = new Sandbox();
        box.Publish("Server.dll", "v1");

        var site = new Procedure("site",
            [new ProcedureUnit("db", "Database"), new ProcedureUnit("api", "API"), new ProcedureUnit("web", "Website")]);
        var siteRequest = new DeploymentRequest("acme-site",
            new DeploymentArtifact(new Dictionary<string, string> { ["app"] = box.Artifact }),
            new Dictionary<string, string> { ["kind"] = LocalOptions.DirectoryKind, ["source"] = "app" });

        var siteOutcome = await box.Runner.ApplyAsync(site, siteRequest);
        var serverOutcome = await box.Runner.ApplyAsync(Server, Request(box));

        Assert.True(siteOutcome.Ok);
        Assert.True(serverOutcome.Ok);
        Assert.Equal(3, (await box.State.GetAsync("site", "acme-site")).Units.Count);
        Assert.Equal(2, (await box.State.GetAsync("server", "acme")).Units.Count);
    }

    [Fact]
    public async Task Settings_shared_by_every_unit_are_written_once_and_only_the_exceptions_are_named()
    {
        // The unscoped fallback, which is what keeps per-unit configuration from becoming three lines per
        // unit. `kind` and `source` are said once for all three units above; here one unit disagrees.
        using var box = new Sandbox();
        box.Publish("Server.dll", "v1");
        var elsewhere = Path.Combine(box.Artifact, "..", "other");
        Directory.CreateDirectory(elsewhere);
        await File.WriteAllTextAsync(Path.Combine(elsewhere, "static.html"), "hello");

        var procedure = new Procedure("site", [new ProcedureUnit("api", "API"), new ProcedureUnit("web", "Website")]);
        var request = new DeploymentRequest("acme",
            new DeploymentArtifact(new Dictionary<string, string> { ["app"] = box.Artifact, ["site"] = elsewhere }),
            new Dictionary<string, string>
            {
                ["kind"] = LocalOptions.DirectoryKind,      // both units
                ["source"] = "app",                         // …unless a unit says otherwise
                ["web.source"] = "site",
            });

        Assert.True((await box.Runner.ApplyAsync(procedure, request)).Ok);
        Assert.True(File.Exists(Path.Combine(box.Live("acme", "api"), "Server.dll")));
        Assert.True(File.Exists(Path.Combine(box.Live("acme", "web"), "static.html")));
    }
}
