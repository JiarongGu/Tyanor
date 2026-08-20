using Microsoft.Extensions.DependencyInjection;
using Tyanor;
using Tyanor.Engine;
using Tyanor.Engine.State;
using Tyanor.Providers.Local;
using Tyanor.Testing;
using Xunit;

namespace Tyanor.Docs.Tests;

/// <summary>
/// Every C# sample in <c>docs/adoption.md</c>, compiled — the same rule <see cref="GuideSamples"/> holds the
/// guide to, and for the same reason.
///
/// <para><b>Adoption docs rot faster than a guide.</b> A guide is re-read by whoever edits the API; an
/// adoption document is read once, by someone new, who has no way to tell that the sentence they are
/// trusting stopped being true two releases ago. So the fences are checked rather than believed:
/// <c>npm run doctor</c> refuses any fence in that file which does not appear here, ignoring indentation.</para>
///
/// <para>Nothing here is executed except the resume sample at the bottom, which really runs — because it is
/// the claim the whole document is asking the reader to rely on.</para>
/// </summary>
internal static class AdoptionSamples
{
    // ── Decide these before you write code ───────────────────────────────────────────────────────
    private static void TwoStoresTwoLocations(IServiceCollection services)
    {
        services.AddTyanor(cfg =>
        {
            cfg.UseState("json:/var/lib/myapp/state.json");     // what Tyanor OWNS — survives, must stay true
            cfg.UseHistory("json:/var/lib/myapp/runs.json");    // what was ATTEMPTED — an account of runs
            cfg.AddTarget(new LocalTarget("/srv"));
        });
    }

    // ── Where Tyanor sits in your build ──────────────────────────────────────────────────────────
    private static DeploymentArtifact TheHandover(string publishOutput, string synthOutput)
    {
        var artifact = new DeploymentArtifact(new Dictionary<string, string>
        {
            ["app"] = publishOutput,                  // dotnet publish wrote this
            ["infrastructure"] = synthOutput,         // cdk synth wrote this
        });

        return artifact;
    }

    // ── Wrapping a step you already have ─────────────────────────────────────────────────────────
    private static void RegisterItOnceCarryItEverywhere(HttpClient http)
    {
        var mine = new CustomUnits
        {
            Classifier = new SmokeTestClassifier(),        // so YOUR transient errors pause instead of failing
            ["smoke"] = new SmokeTestUnit(http),
        };

        var machine = new LocalTarget("/srv", mine);
        var forTest = new MemoryTarget(mine);

        Console.WriteLine($"{machine.Id} {forTest.Id}");
    }

    // ── Infrastructure that already exists ───────────────────────────────────────────────────────
    private static async Task AdoptWhatIsAlreadyThere(
        ProcedureRunner runner, Procedure procedure, DeploymentRequest request)
    {
        // Reads the provider and rewrites state to match. Touches no infrastructure whatsoever.
        await runner.RefreshAsync(procedure, request);

        // Now the plan is about real differences rather than about an empty state file.
        var plan = await runner.PlanAsync(procedure, request);
        Console.WriteLine(plan.Summary);
    }

    // ── Who runs it: desktop, CI, or a service ───────────────────────────────────────────────────
    private static async Task<int> ThePipelineGates(
        ProcedureRunner runner, Procedure procedure, DeploymentRequest request)
    {
        var validation = await runner.ValidateAsync(procedure, request);
        if (!validation.Ok)
        {
            Console.Error.WriteLine(validation);          // every problem, one per line
            return 1;
        }

        var plan = await runner.PlanAsync(procedure, request);
        Console.WriteLine(plan.Summary);

        // Unattended and about to take something away: stop, and let a person look.
        if (plan.IsDestructive) return 2;

        var outcome = await runner.ApplyAsync(procedure, request, report: Console.WriteLine);
        return outcome.Ok ? 0 : outcome.Resumable ? 75 : 1;      // 75 is EX_TEMPFAIL — try again
    }
}

/// <summary>
/// The adopter's own step, wrapping something their deployment already did — the shape D19 is about.
///
/// <para>It is the document's central sample, so it is a real driver rather than a sketch: every member the
/// interface needs, resolving its one setting the way the convention says.</para>
/// </summary>
internal sealed class SmokeTestUnit(HttpClient http) : StepUnitDriver
{
    // The method that is not optional. Everything else the engine does follows from being able to ask this.
    public override async Task<UnitPhase> PhaseAsync(UnitContext context)
    {
        try
        {
            var response = await http.GetAsync(Url(context), context.Cancellation);
            return response.IsSuccessStatusCode ? UnitPhase.Ready : UnitPhase.Missing;
        }
        catch (HttpRequestException)
        {
            return UnitPhase.Missing;      // not answering yet is a fact, not a failure
        }
    }

    // The check IS the whole of it — a step has no control plane to hand work to, so this is where it runs.
    public override async Task CreateAsync(UnitContext context)
    {
        if (await PhaseAsync(context) is not UnitPhase.Ready)
            throw new SmokeTestFailed($"{context.Label}: {Url(context)} is not answering.");
    }

    // Resolve exactly what the apply resolves, and REPORT the refusal instead of throwing it.
    public override Task<IReadOnlyList<string>> ValidateAsync(UnitContext context) =>
        new UnitProblems().Check(() => Url(context)).Found();

    private static string Url(UnitContext context) =>
        context.OwnOption("url") ?? throw new SmokeTestMisconfigured(
            $"Unit '{context.Name}' is a smoke test but names no 'url'.");
}

/// <summary>
/// The unit is configured wrongly. A <see cref="DefinitionException"/>, which is what makes it a
/// CONFIGURATION problem — terminal, nothing touched, and collected by <see cref="UnitProblems.Check"/>.
/// </summary>
internal sealed class SmokeTestMisconfigured(string message) : DefinitionException(message);

/// <summary>The endpoint did not answer. An ordinary failure, for the classifier to read.</summary>
internal sealed class SmokeTestFailed(string message) : Exception(message);

/// <summary>
/// The adopter's classifier, so their own step can PAUSE rather than only ever end the run.
/// </summary>
internal sealed class SmokeTestClassifier : IFailureClassifier
{
    // Through the framework's walk, which is the point of it existing: an implementer should not have to
    // remember that the informative exception is usually nested.
    public FailureClass? Classify(Exception error) =>
        FailureClassifiers.Walk(error, e => e is SmokeTestFailed ? FailureClass.Transient : null);
}

/// <summary>
/// The resume sample — and unlike the rest of this file it really runs, because the document asks the reader
/// to rely on exactly this behaviour.
/// </summary>
public class AdoptionResumeSample
{
    private static readonly Procedure Site = new("site",
    [
        new ProcedureUnit("db", "Database"),
        new ProcedureUnit("api", "API"),
        new ProcedureUnit("web", "Website"),
    ]);

    [Fact]
    public async Task A_pause_keeps_the_work_and_the_same_call_finishes_it()
    {
        var procedure = Site;
        var request = new DeploymentRequest("acme", new DeploymentArtifact(new Dictionary<string, string>()));

        var target = new MemoryTarget().Fails("api", FailureClass.Credentials, "the token expired");
        var runner = new ProcedureRunner(target, new InMemoryRunHistory(), new InMemoryStateStore());

        var stopped = await runner.ApplyAsync(procedure, request);

        Assert.False(stopped.Ok);
        Assert.True(stopped.Resumable);                 // …so YOUR code must offer a resume here

        target.Faults.Remove("api");                    // the operator re-authenticated
        Assert.True((await runner.ApplyAsync(procedure, request)).Ok);
        Assert.Equal(["db", "api", "web"], target.Deployed);
    }
}
