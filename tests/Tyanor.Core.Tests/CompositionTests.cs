using Microsoft.Extensions.DependencyInjection;
using Tyanor.Engine;
using Xunit;

namespace Tyanor.Tests;

/// <summary>
/// Wiring several providers into one application — which is the point of a provider seam, and which used to
/// be silently broken.
/// </summary>
public class CompositionTests
{
    private class Fake(string id) : IDeploymentTarget, IUnitDriver, IFailureClassifier
    {
        public virtual string Id => id;
        public IUnitDriver Driver => this;
        public IFailureClassifier Classifier => this;
        public FailureClass? Classify(Exception error) => null;
        public Task<TargetIdentity> ValidateAsync(TargetCredentials? c, CancellationToken ct) => Task.FromResult(new TargetIdentity(true));
        public Task<UnitPhase> PhaseAsync(UnitContext c) => Task.FromResult(UnitPhase.Missing);
        public Task CreateAsync(UnitContext c) => Task.CompletedTask;
        public Task<bool> UpdateAsync(UnitContext c) => Task.FromResult(false);
        public Task RemoveAsync(UnitContext c) => Task.CompletedTask;
        public Task AwaitSettledAsync(UnitContext c) => Task.CompletedTask;
        public Task<IReadOnlyList<ResourceState>> RefreshAsync(UnitContext c)
            => Task.FromResult<IReadOnlyList<ResourceState>>([]);
    }

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
        using var provider = Build(new Fake("aws"), new Fake("local"));

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
        using var provider = Build(new Fake("local"));

        Assert.NotNull(provider.GetRequiredService<ProcedureRunner>());
    }

    [Fact]
    public void Asking_for_THE_runner_with_two_providers_registered_throws_and_names_them()
    {
        // Rather than picking one. "Which of my two clouds did that deploy to?" is not a question an
        // operator should have to ask afterwards.
        using var provider = Build(new Fake("aws"), new Fake("local"));

        var error = Assert.Throws<InvalidOperationException>(provider.GetRequiredService<ProcedureRunner>);

        Assert.Contains("aws", error.Message);
        Assert.Contains("local", error.Message);
    }

    /// <summary>Whatever a provider written elsewhere needs — a credential store, an HTTP client, a logger.</summary>
    private sealed class Credentials { public string Account => "custom"; }

    private sealed class ContainerBuilt(Credentials credentials) : Fake(credentials.Account);

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
    public void History_and_state_are_shared_across_targets()
    {
        // A deployment's history belongs to the operator, not to the provider — one place to look for what
        // happened, whichever cloud it happened to.
        using var provider = Build(new Fake("aws"), new Fake("local"));
        var runners = provider.GetRequiredService<ProcedureRunners>();

        Assert.NotNull(runners.For("aws"));
        Assert.Same(provider.GetRequiredService<IRunHistory>(), provider.GetRequiredService<IRunHistory>());
    }
}
