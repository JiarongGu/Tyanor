using Tyanor.Engine;
using Xunit;

namespace Tyanor.Providers.Local.Tests;

/// <summary>
/// A step the APPLICATION brings, sitting in the same procedure as the provider's own units.
///
/// <para>The case this exists for is real and was previously homeless: verify a database migration actually
/// applied, warm a cache, call a health endpoint that means something only to you. None of that is a
/// vendor's business, so it used to live outside the procedure as code that ran after it — and got none of
/// what the engine gives. No phase, so it re-ran work that was already done. No plan, so nobody saw it
/// coming. No classification, so a transient failure ended the deployment.</para>
///
/// <para>As a unit it gets all of it, and the tests below are that claim made specific.</para>
/// </summary>
public class CustomPhaseTests
{
    private static readonly ProcedureUnit Runtime = new("runtime", "Application files");
    private static readonly ProcedureUnit Migration = new("migration", "Database changes");
    private static readonly Procedure Server = new("server", [Runtime, Migration]);

    private static DeploymentRequest Request(Sandbox box) =>
        new("acme", new DeploymentArtifact(new Dictionary<string, string> { ["app"] = box.Artifact }),
            new Dictionary<string, string>
            {
                ["runtime.kind"] = LocalOptions.DirectoryKind,
                ["runtime.source"] = "app",
                ["migration.kind"] = "migration",          // the application's own kind
            });

    private static (LocalTarget Target, FakeMigration Unit) Build(Sandbox box, IFailureClassifier? classifier = null)
    {
        var unit = new FakeMigration();
        var target = new LocalTarget(box.Root, new CustomUnits
        {
            Classifier = classifier,
            ["migration"] = unit,
        });
        return (target, unit);
    }

    private static ProcedureRunner Runner(Sandbox box, LocalTarget target) =>
        new(target, box.History, box.State);

    [Fact]
    public async Task A_custom_phase_appears_in_a_plan_beside_the_providers_own_units()
    {
        // Nobody saw these coming before, because they were not in the procedure at all.
        using var box = new Sandbox();
        box.Publish("Server.dll", "v1");
        var (target, _) = Build(box);

        var plan = await Runner(box, target).PlanAsync(Server, Request(box));

        Assert.Equal(["runtime", "migration"], plan.Steps.Select(s => s.Unit.Name));
        Assert.Equal(ReconcileAction.Create, plan.Steps.Last().Action);
    }

    [Fact]
    public async Task A_custom_phase_runs_in_its_place_in_the_order()
    {
        using var box = new Sandbox();
        box.Publish("Server.dll", "v1");
        var (target, migration) = Build(box);

        Assert.True((await Runner(box, target).ApplyAsync(Server, Request(box))).Ok);

        Assert.Equal(1, migration.Applied);
    }

    [Fact]
    public async Task A_custom_phase_that_is_already_done_is_SKIPPED_on_a_re_run()
    {
        // The property it could not have outside the procedure. A step that ran as code after the deployment
        // ran again on every deploy, whether or not it needed to.
        using var box = new Sandbox();
        box.Publish("Server.dll", "v1");
        var (target, migration) = Build(box);
        var runner = Runner(box, target);

        await runner.ApplyAsync(Server, Request(box));
        await runner.ApplyAsync(Server, Request(box));

        Assert.Equal(1, migration.Applied);              // reconciled, not repeated
    }

    [Fact]
    public async Task A_custom_phases_TRANSIENT_failure_pauses_the_run_rather_than_ending_it()
    {
        // What the application's own classifier buys. Without one, an error from a custom unit falls through
        // the provider's classifier as unrecognised and the engine calls it Hard — correct, but it means the
        // step can never pause, so "the endpoint is not warm yet" would end a deployment that was fine.
        using var box = new Sandbox();
        box.Publish("Server.dll", "v1");
        var (target, migration) = Build(box, new MigrationClassifier());
        migration.Fail = new NotWarmYetException();

        var outcome = await Runner(box, target).ApplyAsync(Server, Request(box));

        Assert.False(outcome.Ok);
        Assert.True(outcome.Resumable);
        Assert.Equal("transient", outcome.Reason?.Value);
    }

    [Fact]
    public async Task Without_a_classifier_the_same_failure_is_terminal_which_is_the_safe_default()
    {
        using var box = new Sandbox();
        box.Publish("Server.dll", "v1");
        var (target, migration) = Build(box);          // no classifier supplied
        migration.Fail = new NotWarmYetException();

        var outcome = await Runner(box, target).ApplyAsync(Server, Request(box));

        Assert.False(outcome.Resumable);
    }

    [Fact]
    public async Task A_custom_phase_is_validated_offline_like_everything_else()
    {
        using var box = new Sandbox();
        var (target, migration) = Build(box);
        migration.Problem = "no endpoint configured";

        var validation = await Runner(box, target).ValidateAsync(Server, Request(box));

        Assert.Contains(validation.Problems, p => p.Unit == "migration" && p.Problem == "no endpoint configured");
    }

    [Fact]
    public async Task A_custom_kind_cannot_quietly_take_a_built_in_name()
    {
        // Registered after the provider's own, so a collision is refused rather than shadowing `directory`
        // and changing what every existing procedure means.
        using var box = new Sandbox();

        var error = Assert.Throws<ArgumentException>(() => new LocalTarget(box.Root,
            new CustomUnits { [LocalOptions.DirectoryKind] = new FakeMigration() }));

        Assert.Contains(LocalOptions.DirectoryKind, error.Message);
        await Task.CompletedTask;
    }

    // ── the application's own step ────────────────────────────────────────────────────────────────
    private sealed class NotWarmYetException : Exception;

    /// <summary>What an application's step looks like: a readable phase, and doing the thing.</summary>
    private sealed class FakeMigration : IUnitDriver
    {
        public int Applied { get; private set; }
        public Exception? Fail { get; set; }
        public string? Problem { get; set; }

        public Task<UnitPhase> PhaseAsync(UnitContext context) =>
            // The whole reason this can be a unit: the step can be ASKED whether it already happened.
            Task.FromResult(Applied > 0 ? UnitPhase.Ready : UnitPhase.Missing);

        public Task CreateAsync(UnitContext context)
        {
            if (Fail is not null) throw Fail;
            Applied++;
            context.Progress($"{context.Label}: applied.", 100, ProgressStatus.Success);
            return Task.CompletedTask;
        }

        public Task<bool> UpdateAsync(UnitContext context) => Task.FromResult(false);
        public Task RemoveAsync(UnitContext context) { Applied = 0; return Task.CompletedTask; }
        public Task AwaitSettledAsync(UnitContext context) => Task.CompletedTask;

        public Task<IReadOnlyList<ResourceState>> RefreshAsync(UnitContext context) =>
            Task.FromResult<IReadOnlyList<ResourceState>>(
                Applied > 0 ? [new ResourceState("migration", "app/migration", Applied.ToString())] : []);

        public Task<IReadOnlyList<string>> ValidateAsync(UnitContext context) =>
            Task.FromResult<IReadOnlyList<string>>(Problem is null ? [] : [Problem]);
    }

    private sealed class MigrationClassifier : IFailureClassifier
    {
        public FailureClass? Classify(Exception error)
        {
            for (Exception? e = error; e is not null; e = e.InnerException)
                if (e is NotWarmYetException) return FailureClass.Transient;

            return null;      // not mine — the provider's classifier gets its turn
        }
    }
}
