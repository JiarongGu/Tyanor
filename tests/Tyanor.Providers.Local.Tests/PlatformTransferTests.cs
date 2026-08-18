using Tyanor.Engine;
using Tyanor.Engine.State;
using Tyanor.Testing;
using Xunit;

namespace Tyanor.Providers.Local.Tests;

/// <summary>
/// A service Tyanor does not support, registered by the ADOPTER, surviving a move between platforms.
///
/// <para><b>This is the growth path, and it is the claim worth proving rather than asserting.</b> Tyanor
/// ships four unit kinds across two providers. A real application will need a fifth that is nobody's vendor's
/// business — verify a migration applied, warm a cache, register a service in a discovery system, call an
/// endpoint that means something only to them. D19 says they write it against <c>IUnitDriver</c> and register
/// it as a kind inside whichever provider they are using.</para>
///
/// <para>The half nobody had tested is what happens when the platform changes underneath it. The promise the
/// README makes — a procedure defined once, run against pluggable providers — is only true if the
/// adopter's OWN steps move too. So: one registration, one procedure, one custom step, two targets.</para>
/// </summary>
public class PlatformTransferTests
{
    private static readonly ProcedureUnit Files = new("runtime", "Application files");
    private static readonly ProcedureUnit Register = new("discovery", "Service registration");

    /// <summary>The procedure. Identical on every platform — that is the point of it.</summary>
    private static readonly Procedure Service = new("service", [Files, Register]);

    [Fact]
    public async Task ONE_registration_of_an_adopters_service_runs_on_TWO_different_platforms()
    {
        using var box = new Sandbox();
        box.Publish("Server.dll", "v1");

        // Written once, by the adopter, against IUnitDriver. It knows nothing about either platform.
        var discovery = new ServiceRegistry();
        var mine = new CustomUnits { Classifier = new RegistryClassifier(), ["discovery"] = discovery };

        // The SAME instance handed to both targets. Nothing about it is provider-shaped, so nothing about it
        // has to change when the platform does.
        var machine = new LocalTarget(box.Root, mine);
        var elsewhere = new MemoryTarget(mine);

        // Only the OPTIONS differ between platforms, which is exactly the line D4 draws: the procedure is
        // neutral, and the vendor vocabulary lives in configuration. The adopter's own kind is spelled the
        // same in both, because it is theirs.
        var onMachine = Request(box, ("runtime.kind", LocalOptions.DirectoryKind));
        var onMemory = Request(box);      // the memory target needs no kind for its own units

        Assert.True((await Runner(machine, box).ApplyAsync(Service, onMachine)).Ok);
        Assert.Equal(1, discovery.Registrations);

        discovery.Reset();

        Assert.True((await Runner(elsewhere, box).ApplyAsync(Service, onMemory)).Ok);
        Assert.Equal(1, discovery.Registrations);
    }

    [Fact]
    public async Task The_adopters_own_failure_classes_travel_with_it()
    {
        // A step that can pause on one platform and only fail on another would make the promise hollow —
        // the operator's experience of the same failure would depend on where they deployed.
        using var box = new Sandbox();
        box.Publish("Server.dll", "v1");

        var mine = new CustomUnits
        {
            Classifier = new RegistryClassifier(),
            ["discovery"] = new ServiceRegistry { Throws = new RegistryUnreachable() },
        };

        var onMachine = await Runner(new LocalTarget(box.Root, mine), box)
            .ApplyAsync(new Procedure("service", [Register]), Request(box));

        var onMemory = await Runner(new MemoryTarget(mine), box)
            .ApplyAsync(new Procedure("service", [Register]), Request(box));

        Assert.True(onMachine.Resumable);
        Assert.True(onMemory.Resumable);
        Assert.Equal(onMachine.Reason?.Value, onMemory.Reason?.Value);
    }

    [Fact]
    public void Forgetting_to_register_on_ONE_platform_says_so_rather_than_deploying_something_else()
    {
        // The mistake platform transfer invites: the units are registered per target, so moving to a new
        // one means remembering to bring them. It is refused with the kinds that DO exist, which is the
        // difference between an error and a wrong deployment.
        var forgot = new MemoryTarget();                       // …no CustomUnits handed over

        var thrown = Assert.ThrowsAsync<UnitKindException>(() => forgot.Driver.PhaseAsync(
            new UnitContext(Register, new DeploymentRequest("acme",
                new DeploymentArtifact(new Dictionary<string, string>()),
                new Dictionary<string, string> { ["discovery.kind"] = "discovery" }))));

        Assert.NotNull(thrown);
    }

    private static ProcedureRunner Runner(IDeploymentTarget target, Sandbox box) =>
        new(target, new InMemoryRunHistory(), new InMemoryStateStore(), new RetryPolicy(Attempts: 1));

    /// <summary>The request, differing between platforms only in the vendor-shaped options.</summary>
    private static DeploymentRequest Request(Sandbox box, params (string Key, string? Value)[] platform)
    {
        var options = new Dictionary<string, string>
        {
            ["runtime.source"] = "app",
            ["discovery.kind"] = "discovery",       // the adopter's own kind — the same everywhere
        };
        foreach (var (key, value) in platform.Where(p => p.Value is not null))
            options[key] = value!;

        return new DeploymentRequest("acme",
            new DeploymentArtifact(new Dictionary<string, string> { ["app"] = box.Artifact }), options);
    }

    /// <summary>An adopter's service — registering the deployment somewhere Tyanor knows nothing about.</summary>
    private sealed class ServiceRegistry : IUnitDriver
    {
        public int Registrations { get; private set; }

        public Exception? Throws { get; init; }

        public void Reset() => Registrations = 0;

        public Task<UnitPhase> PhaseAsync(UnitContext c) =>
            Throws is null
                ? Task.FromResult(Registrations > 0 ? UnitPhase.Ready : UnitPhase.Missing)
                : throw Throws;

        public Task CreateAsync(UnitContext c) { Registrations++; return Task.CompletedTask; }
        public Task<bool> UpdateAsync(UnitContext c) => Task.FromResult(false);
        public Task RemoveAsync(UnitContext c) { Registrations = 0; return Task.CompletedTask; }
        public Task AwaitSettledAsync(UnitContext c) => Task.CompletedTask;

        public Task<IReadOnlyList<ResourceState>> RefreshAsync(UnitContext c) =>
            Task.FromResult<IReadOnlyList<ResourceState>>(
                Registrations > 0 ? [new ResourceState("discovery://service", "app/registration", "v1")] : []);
    }

    private sealed class RegistryUnreachable : Exception;

    private sealed class RegistryClassifier : IFailureClassifier
    {
        public FailureClass? Classify(Exception error)
        {
            for (Exception? e = error; e is not null; e = e.InnerException)
                if (e is RegistryUnreachable) return FailureClass.Transient;

            return null;
        }
    }
}
