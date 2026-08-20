using Microsoft.Extensions.DependencyInjection;
using Tyanor;
using Tyanor.Engine;
using Tyanor.Engine.State;
using Tyanor.Providers.Aws;
using Tyanor.Providers.Local;
using Tyanor.Testing;
using Xunit;

namespace Tyanor.Docs.Tests;

/// <summary>
/// Every C# sample in <c>docs/guide.md</c>, compiled.
///
/// <para><b>Why this file exists.</b> The guide is the document that rots invisibly: nothing about a renamed
/// method or a changed signature makes a fenced code block fail, so a guide drifts silently and is wrong
/// exactly when someone new is trusting it. This was being checked by hand — which is another way of saying
/// it was going to stop being checked.</para>
///
/// <para><b>The samples are not duplicated here; they are the SAME TEXT.</b> <c>npm run doctor</c> refuses
/// any fence in the guide that does not appear in this file, ignoring indentation. So the two cannot drift:
/// edit the guide and this stops compiling, edit this and the check says the guide no longer matches.</para>
///
/// <para>Almost nothing here is executed — these are compile-time assertions about signatures, and running
/// them would write to <c>/srv</c>. The one exception is the contract sample at the bottom, which is a real
/// test that really passes.</para>
/// </summary>
internal static class GuideSamples
{
    // ── 1. Describe the deployment ───────────────────────────────────────────────────────────────
    private static void DescribeTheProcedure()
    {
        var procedure = new Procedure("server",
        [
            new ProcedureUnit("runtime", "Application files"),
            new ProcedureUnit("service", "Server", Weight: 3),     // takes longer, so it is more of the bar
        ]);

        _ = procedure;
    }

    private static void DescribeTheRequest(string publishOutput)
    {
        var request = new DeploymentRequest(
            Prefix: "acme",                                        // lets one machine host two of the same procedure
            Artifact: new DeploymentArtifact(new Dictionary<string, string>
            {
                ["app"] = publishOutput,                           // opaque named parts — YOUR names
            }),
            Options: new Dictionary<string, string>
            {
                ["runtime.kind"] = "directory",   ["runtime.source"] = "app",
                ["service.kind"] = "process",     ["service.command"] = "dotnet",
                ["service.args"] = "Server.dll",  ["service.watch"] = "runtime",
                ["service.health.port"] = "8080",
            });

        _ = request;
    }

    // ── 2. Pick a target ─────────────────────────────────────────────────────────────────────────
    private static ProcedureRunner PickATarget()
    {
        var target  = new LocalTarget("/srv");
        var history = new FileRunHistory("/var/lib/myapp/runs.json");     // what was ATTEMPTED
        var state   = new FileStateStore("/var/lib/myapp/state.json");    // what Tyanor OWNS

        var runner = new ProcedureRunner(target, history, state);

        return runner;
    }

    private static async Task<string?> CheckWhoYouAre(
        IDeploymentTarget target, TargetCredentials? credentials, CancellationToken ct)
    {
        var identity = await target.ValidateAsync(credentials, ct);       // null credentials = ambient identity
        if (!identity.Ok) return identity.Error;
        Console.WriteLine($"Deploying into {identity.Account} as {identity.Principal}");

        return null;
    }

    // ── 3. Check it before touching anything ─────────────────────────────────────────────────────
    private static async Task CheckTheDefinition(ProcedureRunner runner, Procedure procedure, DeploymentRequest request)
    {
        var validation = await runner.ValidateAsync(procedure, request);
        if (!validation.Ok) Console.WriteLine(validation);      // every problem, one per line
    }

    // ── 4. Preview, then apply ───────────────────────────────────────────────────────────────────
    private static async Task Preview(ProcedureRunner runner, Procedure procedure, DeploymentRequest request)
    {
        var plan = await runner.PlanAsync(procedure, request);

        Console.WriteLine(plan.Summary);                  // "3 to add, 1 to change, 0 to destroy"
        foreach (var step in plan.Steps) Console.WriteLine(step);

        // A destroy, or a replacement of a unit holding data, is not undoable. Ask first.
        if (plan.IsDestructive && !await AskTheOperator(plan)) return;

        // Someone else is mid-deploy. Applying is safe — the engine attaches — but say so.
        if (plan.HasWorkInFlight) Console.WriteLine("another deployment is already in flight");

        // A run is recorded live with nothing converging: it stopped. Applying RESUMES it.
        if (plan.HasStalledRun) Console.WriteLine("a previous run stopped; this will continue it");
    }

    private static Task<bool> AskTheOperator(Plan plan) => Task.FromResult(true);

    private static async Task<OperationOutcome> Apply(
        ProcedureRunner runner, Procedure procedure, DeploymentRequest request)
    {
        var outcome = await runner.ApplyAsync(procedure, request, report: Console.WriteLine);

        return outcome;
    }

    // ── 5. When it stops ─────────────────────────────────────────────────────────────────────────
    private static void WhenItStops(OperationOutcome outcome)
    {
        if (!outcome.Ok && outcome.Resumable)
        {
            // Credentials expired, or a transient failure outlasted the retries.
            // Fix it and call ApplyAsync again — the work so far is kept.
        }
    }

    // ── telling a wrong definition from a wrong world ────────────────────────────────────────────
    private static async Task PlanSomethingNobodyValidated(
        ProcedureRunner runner, Procedure procedure, DeploymentRequest request)
    {
        try
        {
            var preview = await runner.PlanAsync(procedure, request);
            Console.WriteLine(preview.Summary);
        }
        catch (DefinitionException e)
        {
            // Nothing was touched. This belongs on the screen where they fix the procedure, not on the one
            // that says a deployment failed.
            Console.Error.WriteLine(e.Message);
        }
    }

    // ── 6. Ask what it produced ──────────────────────────────────────────────────────────────────
    private static async Task AskWhatItProduced(ProcedureRunner runner, Procedure procedure, DeploymentRequest request)
    {
        var outputs = await runner.OutputsAsync(procedure, request);
        Console.WriteLine(outputs["service.url"]);
    }

    // ── 7. Drift, and repairing it ───────────────────────────────────────────────────────────────
    private static async Task ReportDrift(ProcedureRunner runner, Procedure procedure, DeploymentRequest request)
    {
        var plan = await runner.PlanAsync(procedure, request);
        if (plan.HasDrift)
            foreach (var d in plan.Drift)
                Console.WriteLine($"{d.Unit}: {d.Resource.Id} — {d.Change}");
    }

    private static async Task RepairDrift(ProcedureRunner runner, Procedure procedure, DeploymentRequest request)
    {
        await runner.RefreshAsync(procedure, request);   // "my records are wrong" — re-read reality, change nothing
        await runner.ApplyAsync(procedure, request);     // "the deployment is wrong" — put it back as described
    }

    private static void ReportOrphans(Plan plan)
    {
        foreach (var orphan in plan.Orphaned)
            Console.WriteLine($"{orphan.Unit} is not in this procedure any more, and owns {orphan.Resources.Count}");
    }

    private static async Task DestroyAnOrphan(
        ProcedureRunner runner, Procedure procedure, DeploymentRequest request)
    {
        await runner.DestroyAsync(procedure.Only("cache"), request);   // …then delete the code
    }

    // ── 8. Tearing it down ───────────────────────────────────────────────────────────────────────
    private static async Task TearDown(ProcedureRunner runner, Procedure procedure, DeploymentRequest request)
    {
        var teardown = await runner.PlanAsync(procedure, request, RunKind.Destroy);
        Console.WriteLine(teardown.Summary);                        // "0 to add, 0 to change, 12 to destroy"
        foreach (var step in teardown.Steps) Console.WriteLine(step);   // in the order they will actually go

        if (Confirmed()) await runner.DestroyAsync(procedure, request);
    }

    // ── what a teardown cannot take away ─────────────────────────────────────────────────────────
    private static void ReportRetained(Plan teardown)
    {
        foreach (var step in teardown.Retained)
            Console.WriteLine($"{step.Unit.Label} will REMAIN — it cannot be removed");
    }

    private static async Task NarrowIt(ProcedureRunner runner, Procedure procedure, DeploymentRequest request)
    {
        await runner.ApplyAsync(procedure.Only("runtime"), request);     // Terraform's -target
    }

    // ── Reading the run log ──────────────────────────────────────────────────────────────────────
    private static async Task ListRecentRuns(IRunHistory history)
    {
        foreach (var run in await history.RecentAsync(20))
            Console.WriteLine($"{run.StartedAt:g}  {run.Procedure}/{run.Prefix}  {run.Kind}  {run.Status}");
    }

    private static async Task IsAnythingOutstanding(
        IRunHistory history, Procedure procedure, DeploymentRequest request)
    {
        if (await history.LiveAsync(procedure.Name, request.Prefix) is { } live)
            Console.WriteLine($"{live.Id} is {live.Status} — applying continues it rather than starting a new one");
    }

    // ── What Tyanor thinks it owns ───────────────────────────────────────────────────────────────
    private static async Task WhatIsOwned(IStateStore state, Procedure procedure, DeploymentRequest request)
    {
        var owned = await state.GetAsync(procedure.Name, request.Prefix);
        foreach (var unit in owned.Units)
            Console.WriteLine($"{unit.Unit}: {unit.Resources.Count} resources, last read {unit.RecordedAt:g}");
    }

    // ── Wiring it into an application ────────────────────────────────────────────────────────────
    private static void Compose(IServiceCollection services, TargetCredentials credentials)
    {
        services.AddTyanor(cfg =>
        {
            cfg.UseState("json:/var/lib/myapp/state.json");
            cfg.UseHistory("json:/var/lib/myapp/runs.json");
            cfg.AddTarget(new LocalTarget("/srv"));
            cfg.AddTarget(new AwsTarget(credentials));          // several coexist, selected by Id
        });
    }

    // ── Testing your own deployment code ─────────────────────────────────────────────────────────
    private static async Task TestAgainstMemory(Procedure procedure, DeploymentRequest request)
    {
        var runner = new ProcedureRunner(new MemoryTarget(), new InMemoryRunHistory(), new InMemoryStateStore());

        Assert.True((await runner.ApplyAsync(procedure, request)).Ok);
    }

    /// <summary>The half that matters when a step exists before Tyanor supports what it talks to.</summary>
    private static void HostYourOwnUnits(HttpClient http)
    {
        var target = new MemoryTarget(new CustomUnits { ["migration"] = new VerifyMigrationUnit(http) });

        _ = target;
    }

    // ── stopping for a person ────────────────────────────────────────────────────────────────────
    private static void PauseForAPerson(UnitContext context) =>
        throw new UnitPausedException(
            new PauseReason("approval"),
            $"{context.Label}: waiting for someone to approve this release. Resume once they have.");

    private static async Task TestAPause(ProcedureRunner runner, Procedure procedure, DeploymentRequest request)
    {
        var target = new MemoryTarget().Fails("api", FailureClass.Credentials, "the token expired");

        var outcome = await runner.ApplyAsync(procedure, request);

        Assert.True(outcome.Resumable);        // …now assert YOUR application offers the resume

        _ = target;
    }

    private static void AssertOnWhatHappened(MemoryTarget target)
    {
        Assert.Equal(["db:update", "api:await", "web:create"], target.Calls);
        Assert.Equal(["db", "api"], target.Deployed);
    }

    // ── Extending it ─────────────────────────────────────────────────────────────────────────────
    private static void OneStepOfYourOwn(TargetCredentials credentials, HttpClient http)
    {
        var target = new AwsTarget(credentials, new CustomUnits
        {
            ["migration"] = new VerifyMigrationUnit(http),
            Classifier = new MyClassifier(),        // so YOUR transient errors pause instead of failing
        });

        target.Dispose();
    }

    private static bool Confirmed() => false;

    // ── the same step, on every platform ─────────────────────────────────────────────────────────
    private static void OneRegistrationEveryPlatform(TargetCredentials credentials)
    {
        var mine = new CustomUnits { Classifier = new MyClassifier(), ["discovery"] = new ServiceRegistry() };

        var machine = new LocalTarget("/srv", mine);
        var cloud   = new AwsTarget(credentials, mine);
        var forTest = new MemoryTarget(mine);

        cloud.Dispose();
        Console.WriteLine($"{machine.Id} {forTest.Id}");
    }
}

/// <summary>The adopter's own service — something Tyanor does not support and does not need to.</summary>
internal sealed class ServiceRegistry : IUnitDriver
{
    public Task<UnitPhase> PhaseAsync(UnitContext context) => Task.FromResult(UnitPhase.Missing);
    public Task CreateAsync(UnitContext context) => Task.CompletedTask;
    public Task<bool> UpdateAsync(UnitContext context) => Task.FromResult(false);
    public Task RemoveAsync(UnitContext context) => Task.CompletedTask;
    public Task AwaitSettledAsync(UnitContext context) => Task.CompletedTask;
    public Task<IReadOnlyList<ResourceState>> RefreshAsync(UnitContext context) =>
        Task.FromResult<IReadOnlyList<ResourceState>>([]);
}

/// <summary>
/// The composition-root sample, which is two constructor overloads rather than statements.
/// </summary>
internal sealed class Deployer
{
    private readonly ProcedureRunner _aws = null!;

    // one provider — ask for the runner
    public Deployer(ProcedureRunner runner) { }

    // several — ask for the one you mean
    public Deployer(ProcedureRunners runners) => _aws = runners.For("aws");

    /// <summary>Reading the field, so the sample's assignment is not flagged as pointless.</summary>
    public ProcedureRunner Aws => _aws;
}

/// <summary>
/// The contract sample — and unlike the rest of this file it really runs, because it can.
/// </summary>
public class GuideContractSample
{
    [Fact]
    public Task My_driver_satisfies_the_contract() =>
        new UnitDriverContract(new MyFixture()).AssertAllAsync();
}

/// <summary>Whatever the reader's own provider is. Here, the one that needs nothing to run.</summary>
internal sealed class MyFixture : IUnitDriverFixture
{
    public IUnitDriver Driver { get; } = new MemoryTarget();

    public ProcedureUnit Unit { get; } = new("web", "Website");

    public DeploymentRequest Request { get; } =
        new("guide", new DeploymentArtifact(new Dictionary<string, string>()));

    public Task ResetAsync(CancellationToken ct) => Driver.RemoveAsync(new UnitContext(Unit, Request));
}

/// <summary>A step of the reader's own — the shape D19 is about.</summary>
internal sealed class VerifyMigrationUnit(HttpClient http) : IUnitDriver
{
    public Task<UnitPhase> PhaseAsync(UnitContext context) => Task.FromResult(UnitPhase.Missing);
    public Task CreateAsync(UnitContext context) => Task.CompletedTask;
    public Task<bool> UpdateAsync(UnitContext context) => Task.FromResult(false);
    public Task RemoveAsync(UnitContext context) => Task.CompletedTask;
    public Task AwaitSettledAsync(UnitContext context) => Task.CompletedTask;
    public Task<IReadOnlyList<ResourceState>> RefreshAsync(UnitContext context) =>
        Task.FromResult<IReadOnlyList<ResourceState>>([]);

    /// <summary>What the reader would actually call.</summary>
    public HttpClient Http => http;
}

/// <summary>The reader's classifier, so their own transient errors can pause rather than fail.</summary>
internal sealed class MyClassifier : IFailureClassifier
{
    public FailureClass? Classify(Exception error) => null;
}

/// <summary>
/// Two methods, not six — the guide's <c>StepUnitDriver</c> sample, compiled.
/// </summary>
/// <remarks>
/// A top-level type rather than a nested one, because the fence in the guide is a whole class declaration
/// and the check compares text: nesting it would indent every line and stop it matching.
/// </remarks>
internal sealed class CacheWarm(HttpClient http) : StepUnitDriver
{
    public override async Task<UnitPhase> PhaseAsync(UnitContext context) =>
        (await http.GetAsync(context.OwnOption("url"), context.Cancellation)).IsSuccessStatusCode
            ? UnitPhase.Ready
            : UnitPhase.Missing;

    public override Task CreateAsync(UnitContext context) =>
        http.GetAsync(context.OwnOption("url"), context.Cancellation);
}

/// <summary>The guide's irreversible-unit declaration, compiled.</summary>
internal sealed class PublishStep : StepUnitDriver
{
    public override Task<UnitPhase> PhaseAsync(UnitContext context) => Task.FromResult(UnitPhase.Ready);

    public override Task CreateAsync(UnitContext context) => Task.CompletedTask;

    public override bool IsRemovable(UnitContext context) => false;
}
