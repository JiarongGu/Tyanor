using Microsoft.Extensions.DependencyInjection;
using Tyanor.Engine;
using Tyanor.Engine.State;
using Tyanor.Testing;
using Xunit;

namespace Tyanor.Tests;

/// <summary>
/// A provider written the way somebody OUTSIDE this repository would write one, held to the same bar.
///
/// <para><b>Why this is a test and not a paragraph.</b> D15 claims a provider written elsewhere is
/// first-class: same contracts, same registration, same suites, no shortcut. Everything that supported that
/// claim was written by people who could see the internals — the two shipped providers each have
/// <c>InternalsVisibleTo</c> for their own test assembly, and <c>MemoryTarget</c> lives inside the package.
/// So the claim rested on nobody having tried it from outside.</para>
///
/// <para><b>The compiler is what makes this proof rather than assertion.</b> <c>src/Tyanor/Tyanor.csproj</c>
/// declares no <c>InternalsVisibleTo</c> at all, so this assembly sees exactly the surface a stranger with a
/// NuGet reference sees. Everything below is built from it: <see cref="IDeploymentTarget"/>,
/// <see cref="IUnitDriver"/>, <see cref="UnitKindDriver"/> for the kind dispatch,
/// <see cref="UnitContext.RequirePart"/> for the artifact, <see cref="UnitProblems"/> for offline validation,
/// <see cref="DefinitionException"/> to say "you configured this wrongly", and
/// <see cref="FailureClassifiers.Walk"/> to read an error however deeply it was wrapped. If any of those
/// stopped being reachable, this file stops compiling — which is a louder failure than a document going
/// quietly out of date.</para>
///
/// <para><b>It is deliberately not shaped like either shipped provider.</b> A registry of small files, with
/// two kinds so the dispatch is exercised, and a phase that answers <i>has this already happened?</i> from
/// what is actually on disk. Real files in a scratch directory, because the contract says to point a fixture
/// at something real: a stub agrees with whatever the driver already believes, which is the one thing these
/// suites exist to catch.</para>
/// </summary>
public class OutsideProviderTests
{
    // ── the provider, written against nothing but the public surface ─────────────────────────────

    /// <summary>What this provider reads out of <see cref="DeploymentRequest.Options"/>.</summary>
    private static class RegistryOptions
    {
        public const string Kind = "kind";
        public const string EntryKind = "entry";
        public const string AliasKind = "alias";

        /// <summary>The artifact part an entry is made of.</summary>
        public const string Source = "source";

        /// <summary>What an alias points at. The unit's identity, so it never falls back.</summary>
        public const string Points = "points";
    }

    /// <summary>This provider's "you configured it wrongly" — terminal, and nothing was touched.</summary>
    private sealed class RegistryConfigurationException(string message) : DefinitionException(message);

    /// <summary>An entry: a file whose contents come from a part of the artifact.</summary>
    private sealed class EntryUnit(string root) : IUnitDriver
    {
        private static string Path(string root, UnitContext c) =>
            System.IO.Path.Combine(root, c.Request.Prefix, $"{c.Name}.entry");

        /// <summary>What the entry SHOULD hold — the source part's contents.</summary>
        private static string Desired(UnitContext c) =>
            File.ReadAllText(c.RequirePart(RegistryOptions.Source, ArtifactPart.File));

        public Task<UnitPhase> PhaseAsync(UnitContext context) =>
            // Read-only, and it answers from disk rather than from anything this process remembers — which
            // is what lets a second run attach to what a first one did.
            Task.FromResult(File.Exists(Path(root, context)) ? UnitPhase.Ready : UnitPhase.Missing);

        public Task CreateAsync(UnitContext context)
        {
            var path = Path(root, context);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, Desired(context));
            return Task.CompletedTask;
        }

        public Task<bool> UpdateAsync(UnitContext context)
        {
            var path = Path(root, context);
            // "Nothing to change" is a SUCCESS and on a resume it is the ordinary answer. A driver that
            // always reports a change redoes finished work on every resume.
            if (File.Exists(path) && File.ReadAllText(path) == Desired(context)) return Task.FromResult(false);

            return CreateAsync(context).ContinueWith(_ => true, TaskScheduler.Default);
        }

        public Task RemoveAsync(UnitContext context)
        {
            var path = Path(root, context);
            if (File.Exists(path)) File.Delete(path);       // already gone is fine — teardown must re-run
            return Task.CompletedTask;
        }

        public Task AwaitSettledAsync(UnitContext context) => Task.CompletedTask;

        public Task<IReadOnlyList<ResourceState>> RefreshAsync(UnitContext context)
        {
            var path = Path(root, context);
            return Task.FromResult<IReadOnlyList<ResourceState>>(
                File.Exists(path)
                    ? [new ResourceState(path, "registry/entry", File.ReadAllText(path))]
                    : []);                                  // absent is a fact, not a failure
        }

        public Task<IReadOnlyList<string>> ValidateAsync(UnitContext context) =>
            // The apply's OWN resolver, so the offline check refuses exactly what a create refuses.
            new UnitProblems().Check(() => Desired(context)).Found();

        public Task<IReadOnlyDictionary<string, string>> OutputsAsync(UnitContext context)
        {
            var path = Path(root, context);
            var outputs = new Dictionary<string, string>(StringComparer.Ordinal);
            if (File.Exists(path)) outputs[$"{context.Name}.entry"] = path;

            return Task.FromResult<IReadOnlyDictionary<string, string>>(outputs);
        }
    }

    /// <summary>An alias: a file naming another entry. Second kind, so the dispatch is real.</summary>
    private sealed class AliasUnit(string root) : IUnitDriver
    {
        private static string Path(string root, UnitContext c) =>
            System.IO.Path.Combine(root, c.Request.Prefix, $"{c.Name}.alias");

        private static string Target(UnitContext context) =>
            // OwnOption, not Option: what an alias points at IS its identity, and a procedure-wide value
            // would mean every alias pointing at the same place rather than a sensible default.
            context.OwnOption(RegistryOptions.Points)
            ?? throw new RegistryConfigurationException(
                $"Unit '{context.Name}' is an alias but names no '{RegistryOptions.Points}'.");

        public Task<UnitPhase> PhaseAsync(UnitContext context) =>
            Task.FromResult(File.Exists(Path(root, context)) ? UnitPhase.Ready : UnitPhase.Missing);

        public Task CreateAsync(UnitContext context)
        {
            var path = Path(root, context);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, Target(context));
            return Task.CompletedTask;
        }

        public Task<bool> UpdateAsync(UnitContext context)
        {
            var path = Path(root, context);
            if (File.Exists(path) && File.ReadAllText(path) == Target(context)) return Task.FromResult(false);

            return CreateAsync(context).ContinueWith(_ => true, TaskScheduler.Default);
        }

        public Task RemoveAsync(UnitContext context)
        {
            var path = Path(root, context);
            if (File.Exists(path)) File.Delete(path);
            return Task.CompletedTask;
        }

        public Task AwaitSettledAsync(UnitContext context) => Task.CompletedTask;

        public Task<IReadOnlyList<ResourceState>> RefreshAsync(UnitContext context)
        {
            var path = Path(root, context);
            return Task.FromResult<IReadOnlyList<ResourceState>>(
                File.Exists(path) ? [new ResourceState(path, "registry/alias", File.ReadAllText(path))] : []);
        }

        public Task<IReadOnlyList<string>> ValidateAsync(UnitContext context) =>
            new UnitProblems().Check(() => Target(context)).Found();
    }

    /// <summary>The kind dispatch — the framework's, not a switch of our own.</summary>
    private sealed class RegistryDriver : UnitKindDriver
    {
        public RegistryDriver(string root) : base(RegistryOptions.Kind)
        {
            Register(RegistryOptions.EntryKind, new EntryUnit(root));
            Register(RegistryOptions.AliasKind, new AliasUnit(root));
        }
    }

    /// <summary>How this provider's failures map onto the three classes.</summary>
    private sealed class RegistryClassifier : IFailureClassifier
    {
        // The whole chain, via the framework's walk — the single most common way a classifier goes wrong is
        // reading only the outermost exception.
        public FailureClass? Classify(Exception error) => FailureClassifiers.Walk(error, Single);

        private static FailureClass? Single(Exception e) => e switch
        {
            // The OS refused this identity: the same operator move as an expired cloud token.
            UnauthorizedAccessException => FailureClass.Credentials,
            // Busy, not wrong.
            IOException => FailureClass.Transient,
            // Anything else is unrecognised, which the engine treats as Hard — the safe default.
            _ => null,
        };
    }

    /// <summary>A target somebody else's application could register in one line.</summary>
    private sealed class RegistryTarget(string root) : IDeploymentTarget
    {
        public string Id => "registry";

        public IUnitDriver Driver { get; } = new RegistryDriver(root);

        public IFailureClassifier Classifier { get; } = new RegistryClassifier();

        /// <summary>Ambient identity, so credentials are legitimately null — and it is still a REAL check.</summary>
        public Task<TargetIdentity> ValidateAsync(TargetCredentials? credentials, CancellationToken ct)
        {
            try
            {
                Directory.CreateDirectory(root);
                return Task.FromResult(new TargetIdentity(true, root, Environment.UserName));
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException)
            {
                return Task.FromResult(new TargetIdentity(false, root, Environment.UserName, e.Message));
            }
        }
    }

    // ── the same suites every shipped provider runs ──────────────────────────────────────────────

    private sealed class Scratch : IDisposable
    {
        public Scratch()
        {
            Root = Path.Combine(Path.GetTempPath(), "tyanor-outside-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(Root);
            Source = Path.Combine(Root, "service.json");
            File.WriteAllText(Source, """{"port":8080}""");
        }

        public string Root { get; }

        public string Source { get; }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch (IOException) { /* temp */ }
        }
    }

    private sealed class EntryFixture : IUnitDriverFixture, IDisposable
    {
        private readonly Scratch _scratch = new();

        public EntryFixture()
        {
            Driver = new RegistryTarget(_scratch.Root).Driver;
            Request = new DeploymentRequest("outside",
                new DeploymentArtifact(new Dictionary<string, string> { ["service"] = _scratch.Source }),
                new Dictionary<string, string>
                {
                    ["api.kind"] = RegistryOptions.EntryKind,
                    ["api.source"] = "service",
                });
        }

        public IUnitDriver Driver { get; }

        public ProcedureUnit Unit { get; } = new("api", "API");

        public DeploymentRequest Request { get; }

        public IReadOnlyCollection<string> ExpectedOutputs { get; } = ["api.entry"];

        public Task ResetAsync(CancellationToken ct) =>
            Driver.RemoveAsync(new UnitContext(Unit, Request, _ => { }, ct));

        public void Dispose() => _scratch.Dispose();
    }

    private sealed class AliasFixture : IUnitDriverFixture, IDisposable
    {
        private readonly Scratch _scratch = new();

        public AliasFixture()
        {
            Driver = new RegistryTarget(_scratch.Root).Driver;
            Request = new DeploymentRequest("outside",
                new DeploymentArtifact(new Dictionary<string, string> { ["service"] = _scratch.Source }),
                new Dictionary<string, string>
                {
                    ["latest.kind"] = RegistryOptions.AliasKind,
                    ["latest.points"] = "api",
                });
        }

        public IUnitDriver Driver { get; }

        public ProcedureUnit Unit { get; } = new("latest", "Current version");

        public DeploymentRequest Request { get; }

        public Task ResetAsync(CancellationToken ct) =>
            Driver.RemoveAsync(new UnitContext(Unit, Request, _ => { }, ct));

        public void Dispose() => _scratch.Dispose();
    }

    private sealed class RegistryFailures : IFailureClassifierFixture
    {
        public IFailureClassifier Classifier { get; } = new RegistryClassifier();

        public IReadOnlyList<Exception> CredentialErrors { get; } =
            [new UnauthorizedAccessException("access to the path is denied")];

        public IReadOnlyList<Exception> TransientErrors { get; } =
            [new IOException("the process cannot access the file")];

        public IReadOnlyList<Exception> HardErrors { get; } =
            [new RegistryConfigurationException("names no source"), new InvalidOperationException("nonsense")];
    }

    public static TheoryData<string> DriverChecks() => Suites.Names(new UnitDriverContract(null!));

    public static TheoryData<string> ClassifierChecks() => Suites.Names(new FailureClassifierContract(null!));

    [Theory]
    [MemberData(nameof(DriverChecks))]
    public async Task An_entry_unit_satisfies_the_driver_contract(string check)
    {
        using var fixture = new EntryFixture();
        await new UnitDriverContract(fixture).AssertAsync(check);
    }

    [Theory]
    [MemberData(nameof(DriverChecks))]
    public async Task An_alias_unit_satisfies_the_driver_contract(string check)
    {
        // Both kinds, because the suite tests ONE unit and the kind nobody wrote a fixture for is the kind
        // that is broken — which is exactly how a shipped provider shipped a driver failing four checks.
        using var fixture = new AliasFixture();
        await new UnitDriverContract(fixture).AssertAsync(check);
    }

    [Theory]
    [MemberData(nameof(ClassifierChecks))]
    public Task The_classifier_satisfies_its_contract(string check) =>
        new FailureClassifierContract(new RegistryFailures()).AssertAsync(check);

    // ── and it composes, which is the other half of "first-class" ────────────────────────────────

    [Fact]
    public async Task It_registers_and_runs_like_any_other_target()
    {
        // The composition root a consumer writes, with a provider this repository has never heard of. If a
        // target written outside needed anything more than one AddTarget line, D15 would be a wish.
        using var scratch = new Scratch();
        var services = new ServiceCollection();
        services.AddTyanor(cfg =>
        {
            cfg.UseInMemoryState();
            cfg.AddTarget(new RegistryTarget(scratch.Root));
        });

        using var provider = services.BuildServiceProvider();
        var runner = provider.GetRequiredService<ProcedureRunners>().For("registry");

        var procedure = new Procedure("services",
        [
            new ProcedureUnit("api", "API"),
            new ProcedureUnit("latest", "Current version"),
        ]);

        var request = new DeploymentRequest("outside",
            new DeploymentArtifact(new Dictionary<string, string> { ["service"] = scratch.Source }),
            new Dictionary<string, string>
            {
                ["api.kind"] = RegistryOptions.EntryKind,
                ["api.source"] = "service",
                ["latest.kind"] = RegistryOptions.AliasKind,
                ["latest.points"] = "api",
            });

        Assert.True((await runner.ValidateAsync(procedure, request)).Ok);

        var plan = await runner.PlanAsync(procedure, request);
        Assert.Equal(2, plan.Changes.Count);
        Assert.False(plan.IsDestructive);

        Assert.True((await runner.ApplyAsync(procedure, request)).Ok);

        // Applying again is the resume path. Every unit now reads Ready, so the plan still shows two
        // Updates — only the provider knows whether an update changes anything, and a plan that claimed
        // otherwise would be making the one promise it refuses to make. What IS assertable is that nothing
        // drifted and nothing is stranded.
        var settled = await runner.PlanAsync(procedure, request);
        Assert.All(settled.Steps, s => Assert.Equal(ReconcileAction.Update, s.Action));
        Assert.False(settled.HasDrift);
        Assert.False(settled.HasOrphans);
        Assert.True((await runner.ApplyAsync(procedure, request)).Ok);

        var outputs = await runner.OutputsAsync(procedure, request);
        Assert.Contains("api.entry", outputs.Keys);

        Assert.True((await runner.DestroyAsync(procedure, request)).Ok);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(scratch.Root, "outside")));

        // …and a teardown of what is already gone has nothing to do, which is the one plan that is a no-op.
        Assert.True((await runner.PlanAsync(procedure, request, RunKind.Destroy)).IsNoOp);
    }

    [Fact]
    public async Task A_misconfigured_unit_is_reported_offline_rather_than_thrown()
    {
        // The gate that works before an account exists — and for a provider with no account at all, still
        // the gate that works before anything is written.
        using var scratch = new Scratch();
        var runner = new ProcedureRunner(new RegistryTarget(scratch.Root), new InMemoryRunHistory());

        var procedure = new Procedure("services", [new ProcedureUnit("api", "API")]);
        var request = new DeploymentRequest("outside",
            new DeploymentArtifact(new Dictionary<string, string> { ["service"] = scratch.Source }),
            new Dictionary<string, string> { ["api.kind"] = RegistryOptions.EntryKind });   // no source

        var validation = await runner.ValidateAsync(procedure, request);

        Assert.False(validation.Ok);
        Assert.Contains("source", validation.ToString());
        Assert.Empty(Directory.EnumerateFiles(scratch.Root, "*.entry"));      // and it touched nothing
    }
}
