using Tyanor.Engine;
using Xunit;

namespace Tyanor.Providers.Local.Tests;

/// <summary>
/// The whole operator workflow, end to end, with no cloud and no account: validate → plan → apply →
/// outputs → refresh → plan again → change → plan the diff → apply → plan the teardown → destroy → plan
/// again.
///
/// <para><b>This is the test that says the library WORKS</b>, as opposed to the ones that say a particular
/// decision is right. Every other test here checks one behaviour in isolation; this one walks the sequence a
/// person actually performs and asserts the answer at every step, on a real filesystem with a real process.
/// If Tyanor is going to be Terraform-shaped, this is the shape.</para>
/// </summary>
public class LifecycleTests
{
    private static readonly ProcedureUnit Runtime = new("runtime", "Application files");
    private static readonly ProcedureUnit Service = new("service", "Server", Weight: 3);
    private static readonly Procedure Server = new("server", [Runtime, Service]);

    private static DeploymentRequest Request(Sandbox box)
    {
        var (command, arguments) = Sandbox.Sleeper;
        return new DeploymentRequest("acme",
            new DeploymentArtifact(new Dictionary<string, string> { ["app"] = box.Artifact }),
            new Dictionary<string, string>
            {
                ["runtime.kind"] = LocalOptions.DirectoryKind,
                ["runtime.source"] = "app",
                ["service.kind"] = LocalOptions.ProcessKind,
                ["service.command"] = command,
                ["service.args"] = arguments,
                ["service.watch"] = "runtime",
            });
    }

    [Fact]
    public async Task The_whole_lifecycle_runs_without_a_cloud()
    {
        using var box = new Sandbox();
        box.Publish("Server.dll", "v1");
        var request = Request(box);
        var runner = box.Runner;

        // ── validate ── nothing has been touched, and nothing needs to be.
        Assert.True((await runner.ValidateAsync(Server, request)).Ok);

        // ── plan ── two units to create. Steps are UNITS; the counts are RESOURCES, and there are none yet.
        var first = await runner.PlanAsync(Server, request);
        Assert.Equal([ReconcileAction.Create, ReconcileAction.Create], first.Steps.Select(s => s.Action));
        Assert.False(first.IsDestructive);
        Assert.Equal("0 to add, 0 to change, 0 to destroy", first.Summary);

        // ── apply ──
        var progress = new List<ProgressReport>();
        Assert.True((await runner.ApplyAsync(Server, request, progress.Add)).Ok);

        // The run narrated itself, and every percentage it reported was inside the run's own scale.
        Assert.NotEmpty(progress);
        Assert.All(progress.Where(p => p.Percent >= 0), p => Assert.InRange(p.Percent, 0, 100));

        // ── outputs ── the question an operator has the moment an apply finishes.
        var outputs = await runner.OutputsAsync(Server, request);
        Assert.Equal(box.Live("acme", "runtime"), outputs["runtime.path"]);
        Assert.Equal(box.Pid("acme", "service")!.Value.ToString(), outputs["service.pid"]);

        // ── state ── what Tyanor owns, which is the only thing that can answer a safe teardown.
        Assert.Equal(["runtime", "service"], (await box.State.GetAsync("server", "acme")).Units.Select(u => u.Unit).Order());

        // ── refresh ── re-reading reality changes nothing when reality agrees.
        var refreshed = await runner.RefreshAsync(Server, request);
        Assert.Equal(2, refreshed.Units.Count);

        // ── plan again ── settled: updates that will be no-ops, and no drift.
        var settled = await runner.PlanAsync(Server, request);
        Assert.Equal([ReconcileAction.Update, ReconcileAction.Update], settled.Steps.Select(s => s.Action));
        Assert.False(settled.HasDrift);

        // ── a new build ── the plan cannot see it (a plan compares state to reality, not to intent), so the
        // honest signal is the step saying "apply configuration, may be a no-op" — and then the apply.
        var pidBefore = box.Pid("acme", "service");
        box.Publish("Server.dll", "v2");
        Assert.True((await runner.ApplyAsync(Server, request)).Ok);

        Assert.Equal("v2", await File.ReadAllTextAsync(Path.Combine(box.Live("acme", "runtime"), "Server.dll")));
        Assert.NotEqual(pidBefore, box.Pid("acme", "service"));      // the server restarted into it

        // ── drift ── someone edits the deployment by hand; a plan says so, and applying repairs it.
        var deployed = Path.Combine(box.Live("acme", "runtime"), "Server.dll");
        await File.WriteAllTextAsync(deployed, "patched in place");

        var drifted = await runner.PlanAsync(Server, request);
        Assert.Equal("0 to add, 1 to change, 0 to destroy", drifted.Summary);

        Assert.True((await runner.ApplyAsync(Server, request)).Ok);
        Assert.Equal("v2", await File.ReadAllTextAsync(deployed));
        Assert.False((await runner.PlanAsync(Server, request)).HasDrift);

        // ── plan the teardown ── the gate in front of the only irreversible direction.
        var teardown = await runner.PlanAsync(Server, request, RunKind.Destroy);
        Assert.Equal(RunKind.Destroy, teardown.Kind);
        Assert.Equal(["service", "runtime"], teardown.Steps.Select(s => s.Unit.Name));   // reverse order
        Assert.True(teardown.IsDestructive);
        Assert.Equal(2, teardown.ToDestroy);

        // ── destroy ──
        Assert.True((await runner.DestroyAsync(Server, request)).Ok);

        // ── and afterwards, nothing: no files, no process, no state, no outputs.
        Assert.False(Directory.Exists(box.Deployed("acme", "runtime")));
        Assert.Null(box.Pid("acme", "service"));
        Assert.Empty((await box.State.GetAsync("server", "acme")).Units);
        Assert.Empty(await runner.OutputsAsync(Server, request));

        // ── plan once more ── back to where it started, which is what makes the whole thing re-runnable.
        var last = await runner.PlanAsync(Server, request);
        Assert.Equal([ReconcileAction.Create, ReconcileAction.Create], last.Steps.Select(s => s.Action));
        Assert.Equal(0, last.ToDestroy);
    }

    [Fact]
    public async Task Applying_ONE_unit_leaves_the_others_and_their_state_alone()
    {
        // The case the source deployer had a dedicated method for: push the website again without reconciling
        // everything else. The property that makes it safe is that a narrowed run touches only its own units'
        // state — if it rewrote the whole set, a targeted apply would quietly forget what it did not look at,
        // and the next teardown would not know those resources existed.
        using var box = new Sandbox();
        box.Publish("Server.dll", "v1");
        var request = Request(box);
        var runner = box.Runner;

        await runner.ApplyAsync(Server, request);
        var pid = box.Pid("acme", "service");

        // A new build, then narrow the run to the files only — the server is deliberately left running.
        box.Publish("Server.dll", "v2");
        var outcome = await runner.ApplyAsync(Server.Only("runtime"), request);

        Assert.True(outcome.Ok);
        Assert.Equal("v2", await File.ReadAllTextAsync(Path.Combine(box.Live("acme", "runtime"), "Server.dll")));
        Assert.Equal(pid, box.Pid("acme", "service"));            // not restarted: it was not in the run

        // And the service's state survived a run that never mentioned it.
        var state = await box.State.GetAsync("server", "acme");
        Assert.Equal(["runtime", "service"], state.Units.Select(u => u.Unit).Order());

        // A plan of the narrow procedure shows one step; the full one still shows two.
        Assert.Single((await runner.PlanAsync(Server.Only("runtime"), request)).Steps);
        Assert.Equal(2, (await runner.PlanAsync(Server, request)).Steps.Count);
    }

    [Fact]
    public async Task A_narrowed_DESTROY_takes_only_what_it_names()
    {
        // Worth pinning because it is the sharp end of narrowing. It does what it says — and the plan says it
        // first, which is the whole reason a teardown plan exists.
        using var box = new Sandbox();
        box.Publish("Server.dll", "v1");
        var request = Request(box);
        var runner = box.Runner;
        await runner.ApplyAsync(Server, request);

        var plan = await runner.PlanAsync(Server.Only("service"), request, RunKind.Destroy);
        Assert.Equal(["service"], plan.Steps.Select(s => s.Unit.Name));

        Assert.True((await runner.DestroyAsync(Server.Only("service"), request)).Ok);

        Assert.Null(box.Pid("acme", "service"));                          // gone
        Assert.True(Directory.Exists(box.Deployed("acme", "runtime")));   // untouched
    }

    [Fact]
    public async Task Validation_finds_every_problem_at_once_and_touches_nothing()
    {
        // The point of an offline check: a whole procedure's worth of mistakes in one pass, before an account
        // exists. Discovering them one at a time from a run means each fix costs another partial deployment.
        using var box = new Sandbox();
        var broken = new DeploymentRequest("acme",
            new DeploymentArtifact(new Dictionary<string, string> { ["app"] = box.Artifact }),
            new Dictionary<string, string>
            {
                ["runtime.kind"] = LocalOptions.DirectoryKind,
                ["runtime.source"] = "never-built",                  // no such artifact part
                ["service.kind"] = LocalOptions.ProcessKind,
                ["service.health.port"] = "not-a-port",              // …and two more on one unit
                ["service.health.seconds"] = "soon",
            });                                                      // …and no command at all

        var validation = await box.Runner.ValidateAsync(Server, broken);

        Assert.False(validation.Ok);
        Assert.Equal(4, validation.Problems.Count);
        Assert.Single(validation.Problems, p => p.Unit == "runtime");
        Assert.Equal(3, validation.Problems.Count(p => p.Unit == "service"));

        // Nothing was created while finding all that out.
        Assert.False(Directory.Exists(box.Deployed("acme", "runtime")));
        Assert.Null(box.Pid("acme", "service"));
    }

    [Fact]
    public async Task A_unit_that_does_not_say_what_it_is_is_REPORTED_rather_than_thrown()
    {
        // Validation exists to return the whole list. Throwing on the first unconfigured unit would be the
        // behaviour it replaces.
        using var box = new Sandbox();
        var request = new DeploymentRequest("acme",
            new DeploymentArtifact(new Dictionary<string, string>()));

        var validation = await box.Runner.ValidateAsync(Server, request);

        Assert.Equal(2, validation.Problems.Count);
        Assert.All(validation.Problems, p => Assert.Contains(LocalOptions.DirectoryKind, p.Problem));
    }

    [Fact]
    public async Task Outputs_of_something_not_deployed_are_empty_rather_than_an_error()
    {
        // Asking is reasonable at any time, including before the first apply — a UI showing "your site is
        // at …" renders once and should not have to guard the call.
        using var box = new Sandbox();

        Assert.Empty(await box.Runner.OutputsAsync(Server, Request(box)));
    }

    [Fact]
    public async Task A_procedure_wide_path_is_REFUSED_rather_than_collapsing_every_unit_into_one_directory()
    {
        // `path` is the unit's ADDRESS, so unlike every other setting here it does not inherit the unscoped
        // value. It used to: `["path"] = …` put both directory units in the same folder, where the second to
        // deploy pruned the first's releases and removing either removed both — silent data loss, and the
        // same collision `Procedure` refuses when two units share a name, reached through a different door.
        //
        // Not inheriting fixed that and left a quieter fault: the line was then read by NOTHING, so an
        // operator who wrote one had it dropped without a word and their units went to the default location.
        // It is now refused, offline and again at apply, and the refusal says which spelling would work (D36).
        using var box = new Sandbox();
        box.Publish("Server.dll", "v1");

        var two = new Procedure("server",
            [new ProcedureUnit("runtime", "Application files"), new ProcedureUnit("assets", "Static files")]);

        var shared = Path.Combine(box.Root, "shared");
        var request = new DeploymentRequest("acme",
            new DeploymentArtifact(new Dictionary<string, string> { ["app"] = box.Artifact }),
            new Dictionary<string, string>
            {
                ["kind"] = LocalOptions.DirectoryKind,
                ["source"] = "app",
                ["path"] = shared,                               // meant for one unit; binds to none
            });

        // Offline first: no files have moved and no directory exists yet.
        var validation = await box.Runner.ValidateAsync(two, request);
        Assert.False(validation.Ok);
        // Each unit is told the spelling that would work for IT, which is the whole value of refusing here
        // rather than letting a shared value through.
        Assert.Contains(validation.Problems, p => p.Problem.Contains("\"runtime.path\""));
        Assert.Contains(validation.Problems, p => p.Problem.Contains("\"assets.path\""));

        // …and the apply refuses too, rather than quietly deploying somewhere else.
        Assert.False((await box.Runner.ApplyAsync(two, request)).Ok);
        Assert.False(Directory.Exists(shared));
        Assert.False(Directory.Exists(box.Deployed("acme", "runtime")));
    }

    [Fact]
    public async Task Two_units_with_their_OWN_paths_do_not_touch_each_other()
    {
        // The property the refusal above protects, stated positively: addressed separately, removing one
        // leaves the other standing.
        using var box = new Sandbox();
        box.Publish("Server.dll", "v1");

        var two = new Procedure("server",
            [new ProcedureUnit("runtime", "Application files"), new ProcedureUnit("assets", "Static files")]);

        var request = new DeploymentRequest("acme",
            new DeploymentArtifact(new Dictionary<string, string> { ["app"] = box.Artifact }),
            new Dictionary<string, string>
            {
                ["kind"] = LocalOptions.DirectoryKind,
                ["source"] = "app",
                ["runtime.path"] = Path.Combine(box.Root, "runtime-here"),
                ["assets.path"] = Path.Combine(box.Root, "assets-there"),
            });

        Assert.True((await box.Runner.ApplyAsync(two, request)).Ok);
        Assert.True(Directory.Exists(Path.Combine(box.Root, "runtime-here")));
        Assert.True(Directory.Exists(Path.Combine(box.Root, "assets-there")));

        Assert.True((await box.Runner.DestroyAsync(two.Only("assets"), request)).Ok);
        Assert.False(Directory.Exists(Path.Combine(box.Root, "assets-there")));
        Assert.True(Directory.Exists(Path.Combine(box.Root, "runtime-here")));
    }

    [Fact]
    public async Task A_unit_scoped_path_is_still_honoured()
    {
        // The capability itself is not being taken away — only the inheritance.
        using var box = new Sandbox();
        box.Publish("Server.dll", "v1");

        var one = new Procedure("server", [new ProcedureUnit("runtime", "Application files")]);
        var elsewhere = Path.Combine(box.Root, "somewhere-else");

        var request = new DeploymentRequest("acme",
            new DeploymentArtifact(new Dictionary<string, string> { ["app"] = box.Artifact }),
            new Dictionary<string, string>
            {
                ["runtime.kind"] = LocalOptions.DirectoryKind,
                ["runtime.source"] = "app",
                ["runtime.path"] = elsewhere,
            });

        Assert.True((await box.Runner.ApplyAsync(one, request)).Ok);

        Assert.True(Directory.Exists(Path.Combine(elsewhere, "releases")));
        Assert.False(Directory.Exists(box.Deployed("acme", "runtime")));
    }
}
