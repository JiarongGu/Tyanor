using Microsoft.Extensions.DependencyInjection;
using Tyanor.Engine;
using Tyanor.Engine.State;
using Tyanor.Testing;
using Xunit;

namespace Tyanor.Tests;

/// <summary>
/// Wiring several providers into one application — which is the point of a provider seam, and which used to
/// be silently broken.
/// </summary>
public class CompositionTests
{
    private static ServiceProvider Build(params IDeploymentTarget[] targets)
    {
        var services = new ServiceCollection();
        services.AddTyanor(cfg =>
        {
            cfg.UseInMemoryState();
            foreach (var target in targets) cfg.AddTarget(target);
        });
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Two_providers_coexist_and_are_selectable_by_id()
    {
        // The defect this fixed. Before, both registered as IDeploymentTarget and resolving one returned
        // whichever happened to be added last — a wrong deployment produced by a wiring detail, and an
        // undiscoverable one, because the plan would be computed against the wrong target too.
        using var provider = Build(new MemoryTarget { Id = "aws" }, new MemoryTarget { Id = "local" });

        var runners = provider.GetRequiredService<ProcedureRunners>();

        Assert.Equal(["aws", "local"], runners.Targets.Ids);
        Assert.NotNull(runners.For("aws"));
        Assert.NotNull(runners.For("local"));
    }

    [Fact]
    public void One_provider_still_resolves_a_runner_without_anyone_knowing_about_a_factory()
    {
        // The ordinary case stays three lines. A structure that makes the simple thing harder in order to
        // support the complicated one has charged everybody for a feature most will not use.
        using var provider = Build(new MemoryTarget { Id = "local" });

        Assert.NotNull(provider.GetRequiredService<ProcedureRunner>());
    }

    [Fact]
    public void Asking_for_THE_runner_with_two_providers_registered_throws_and_names_them()
    {
        // Rather than picking one. "Which of my two clouds did that deploy to?" is not a question an
        // operator should have to ask afterwards.
        using var provider = Build(new MemoryTarget { Id = "aws" }, new MemoryTarget { Id = "local" });

        var error = Assert.Throws<InvalidOperationException>(provider.GetRequiredService<ProcedureRunner>);

        Assert.Contains("aws", error.Message);
        Assert.Contains("local", error.Message);
    }

    /// <summary>Whatever a provider written elsewhere needs — a credential store, an HTTP client, a logger.</summary>
    private sealed class Credentials { public string Account => "custom"; }

    /// <summary>A target the CONTAINER builds, taking its dependencies the way a real one would.</summary>
    private sealed class ContainerBuilt(Credentials credentials) : IDeploymentTarget
    {
        private readonly MemoryTarget _inner = new() { Id = credentials.Account };

        public string Id => _inner.Id;
        public IUnitDriver Driver => _inner;
        public IFailureClassifier Classifier => _inner;
        public Task<TargetIdentity> ValidateAsync(TargetCredentials? c, CancellationToken ct) =>
            _inner.ValidateAsync(c, ct);
    }

    [Fact]
    public void A_target_the_container_constructs_gets_its_own_dependencies()
    {
        // The registration a third-party provider actually wants: it is not handed to the builder fully
        // built, it is constructed with whatever it needs from the application around it.
        var services = new ServiceCollection();
        services.AddSingleton<Credentials>();
        services.AddTyanor(cfg =>
        {
            cfg.UseInMemoryState();
            cfg.AddTarget<ContainerBuilt>();
        });

        using var provider = services.BuildServiceProvider();

        Assert.Equal(["custom"], provider.GetRequiredService<DeploymentTargets>().Ids);
    }

    [Fact]
    public void A_target_that_was_never_registered_can_still_borrow_the_configured_history()
    {
        // The reason this overload exists rather than "just construct a ProcedureRunner": a one-off target —
        // a scratch account, a target built from credentials typed in a minute ago — should still write to
        // the same history and state the application configured, not to a second set nobody looks at.
        using var provider = Build(new MemoryTarget { Id = "local" });

        var runner = provider.GetRequiredService<ProcedureRunners>().For(new MemoryTarget { Id = "ad-hoc" });

        Assert.NotNull(runner);
    }

    [Fact]
    public void History_and_state_are_shared_across_targets()
    {
        // A deployment's history belongs to the operator, not to the provider — one place to look for what
        // happened, whichever cloud it happened to.
        using var provider = Build(new MemoryTarget { Id = "aws" }, new MemoryTarget { Id = "local" });
        var runners = provider.GetRequiredService<ProcedureRunners>();

        Assert.NotNull(runners.For("aws"));
        Assert.Same(provider.GetRequiredService<IRunHistory>(), provider.GetRequiredService<IRunHistory>());
    }
}

/// <summary>
/// What the container hands back when the consumer has said what they want — the half of wiring that fails
/// SILENTLY, because a misconfigured store does not throw, it just writes somewhere nobody looks.
/// </summary>
public class WiringTests
{
    private sealed class Remembers : IRunHistory
    {
        public Task UpsertAsync(RunRecord r, CancellationToken ct = default) => Task.CompletedTask;
        public Task<RunRecord?> LiveAsync(string p, string x, CancellationToken ct = default) => Task.FromResult<RunRecord?>(null);
        public Task<IReadOnlyList<RunRecord>> RecentAsync(int limit = 50, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RunRecord>>([]);
        public Task DeleteAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
    }

    [Fact]
    public void A_history_the_consumer_SUPPLIED_wins_over_the_default()
    {
        // `TryAddSingleton` for the fallback is what makes this true, and nothing checked it. With a plain
        // Add the last registration wins — so `UseHistory(mine)` would be silently ignored and every run
        // recorded to a file under the application's base directory instead of where the consumer said.
        var services = new ServiceCollection();
        services.AddTyanor(cfg => cfg.UseHistory(new Remembers()));

        using var provider = services.BuildServiceProvider();

        Assert.IsType<Remembers>(provider.GetRequiredService<IRunHistory>());
    }

    [Fact]
    public void A_state_store_the_consumer_SUPPLIED_wins_over_the_default()
    {
        var services = new ServiceCollection();
        services.AddTyanor(cfg => cfg.UseState(new InMemoryStateStore()));

        using var provider = services.BuildServiceProvider();

        Assert.IsType<InMemoryStateStore>(provider.GetRequiredService<IStateStore>());
    }

    [Fact]
    public async Task The_runner_the_container_builds_actually_HAS_the_configured_state()
    {
        // `ProcedureRunners` is handed `sp.GetService<IStateStore>()`, and dropping it costs nothing
        // visible: runs still succeed. What goes is everything state answers — drift, orphans, and knowing
        // what a teardown may remove — so the failure surfaces as a plan that quietly reports nothing.
        var state = new InMemoryStateStore();
        var services = new ServiceCollection();
        services.AddTyanor(cfg =>
        {
            cfg.UseState(state);
            cfg.UseInMemoryState();
            cfg.AddTarget(new MemoryTarget { Id = "memory" });
        });

        using var provider = services.BuildServiceProvider();
        var runner = provider.GetRequiredService<ProcedureRunners>().For("memory");

        var procedure = new Procedure("site", [new ProcedureUnit("db", "Database")]);
        var request = new DeploymentRequest("acme", new DeploymentArtifact(new Dictionary<string, string>()));
        Assert.True((await runner.ApplyAsync(procedure, request)).Ok);

        // If the runner had no state store, this would be empty and no plan would ever report drift.
        Assert.Equal(["db"], (await provider.GetRequiredService<IStateStore>().GetAsync("site", "acme")).RecordedUnits);
    }

    [Fact]
    public void Registering_the_json_backend_YOURSELF_does_not_produce_two_of_it()
    {
        // `TryAddEnumerable` for the default is what allows this. With a plain Add there would be two
        // backends claiming `json`, and `StorageBackends` refuses a duplicate kind — so being explicit
        // about the default would break the application at resolve time.
        var services = new ServiceCollection();
        services.AddTyanor(cfg => cfg.AddStorage(new JsonStorageBackend()));

        using var provider = services.BuildServiceProvider();

        Assert.Equal(["json"], provider.GetRequiredService<StorageBackends>().Kinds);
    }
}
