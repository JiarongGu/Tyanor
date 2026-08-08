using Tyanor.Engine;
using Tyanor.Engine.State;
using Xunit;

namespace Tyanor.Tests;

/// <summary>
/// Progress percentages, and whose frame of reference they are in.
///
/// <para>A driver reports through its OWN unit and the engine rescales into the run. Without the rule the
/// number means nothing: the engine has always emitted run-relative percentages, so a driver emitting its
/// own would be read as one — a unit half done showing as a run half done. Both shipped providers avoided
/// the question by always reporting -1, which is safe and useless, and means a ten-minute stack deploy shows
/// no movement at all.</para>
/// </summary>
public class ProgressTests
{
    private static DeploymentRequest Request() =>
        new("acme", new DeploymentArtifact(new Dictionary<string, string>()));

    private static ProcedureRunner Runner(FakeTarget target) => new(target, new InMemoryRunHistory());

    [Fact]
    public async Task A_drivers_percent_is_rescaled_into_the_run()
    {
        // Four equal units. The second unit reporting halfway is 37.5% of the run — 25 done plus half of
        // the next quarter — which rounds to 38.
        var procedure = new Procedure("site",
        [
            new ProcedureUnit("a", "A"), new ProcedureUnit("b", "B"),
            new ProcedureUnit("c", "C"), new ProcedureUnit("d", "D"),
        ]);
        var target = new FakeTarget { Report = { ["b"] = 50 } };
        var seen = new List<ProgressReport>();

        await Runner(target).ApplyAsync(procedure, Request(), seen.Add);

        var line = Assert.Single(seen, r => r.Unit == "b" && r.Message == "halfway");
        Assert.Equal(38, line.Percent);
    }

    [Fact]
    public async Task Weight_is_respected_when_rescaling()
    {
        // A ten-minute unit and a ten-second one must not each be half the bar. The heavy unit reporting
        // halfway is 30% of a run whose weights are 6 and 4.
        var procedure = new Procedure("site",
            [new ProcedureUnit("heavy", "Heavy", Weight: 6), new ProcedureUnit("light", "Light", Weight: 4)]);
        var target = new FakeTarget { Report = { ["heavy"] = 50 } };
        var seen = new List<ProgressReport>();

        await Runner(target).ApplyAsync(procedure, Request(), seen.Add);

        Assert.Equal(30, Assert.Single(seen, r => r.Unit == "heavy" && r.Message == "halfway").Percent);
    }

    [Fact]
    public async Task Unknown_stays_unknown()
    {
        // -1 means the driver cannot tell. Turning that into a number would be the one kind of progress
        // worse than none, because it looks like information.
        var procedure = new Procedure("site", [new ProcedureUnit("a", "A"), new ProcedureUnit("b", "B")]);
        var target = new FakeTarget { Report = { ["b"] = -1 } };
        var seen = new List<ProgressReport>();

        await Runner(target).ApplyAsync(procedure, Request(), seen.Add);

        Assert.Equal(-1, Assert.Single(seen, r => r.Unit == "b" && r.Message == "halfway").Percent);
    }

    [Fact]
    public async Task A_nonsense_percent_is_passed_through_rather_than_quietly_clamped()
    {
        // A driver reporting 140 has a bug. Making it 100 hides the bug and produces a bar that jumps
        // backwards later, which is harder to diagnose than a number that is obviously wrong.
        var procedure = new Procedure("site", [new ProcedureUnit("a", "A")]);
        var target = new FakeTarget { Report = { ["a"] = 140 } };
        var seen = new List<ProgressReport>();

        await Runner(target).ApplyAsync(procedure, Request(), seen.Add);

        Assert.Equal(140, Assert.Single(seen, r => r.Message == "halfway").Percent);
    }

    [Fact]
    public async Task A_finished_unit_still_reports_the_run_total()
    {
        // The engine's own lines were always run-relative and stay so.
        var procedure = new Procedure("site", [new ProcedureUnit("a", "A"), new ProcedureUnit("b", "B")]);
        var seen = new List<ProgressReport>();

        await Runner(new FakeTarget()).ApplyAsync(procedure, Request(), seen.Add);

        Assert.Equal([50, 100], seen.Where(r => r.Message.EndsWith("done.")).Select(r => r.Percent));
    }

    [Fact]
    public async Task Progress_never_goes_backwards_across_a_run()
    {
        var procedure = new Procedure("site",
            [new ProcedureUnit("a", "A"), new ProcedureUnit("b", "B", Weight: 3)]);
        var target = new FakeTarget { Report = { ["a"] = 50, ["b"] = 50 } };
        var seen = new List<ProgressReport>();

        await Runner(target).ApplyAsync(procedure, Request(), seen.Add);

        var measured = seen.Select(r => r.Percent).Where(p => p >= 0).ToList();
        Assert.Equal(measured.Order(), measured);
    }

    private sealed class FakeTarget : IDeploymentTarget, IUnitDriver, IFailureClassifier
    {
        /// <summary>Unit name → the unit-relative percent its driver reports while settling.</summary>
        public Dictionary<string, int> Report { get; } = [];

        public string Id => "fake";
        public IUnitDriver Driver => this;
        public IFailureClassifier Classifier => this;
        public FailureClass? Classify(Exception error) => null;
        public Task<TargetIdentity> ValidateAsync(TargetCredentials? c, CancellationToken ct) => Task.FromResult(new TargetIdentity(true));
        public Task<UnitPhase> PhaseAsync(UnitContext c) => Task.FromResult(UnitPhase.Missing);
        public Task CreateAsync(UnitContext c) => Task.CompletedTask;
        public Task<bool> UpdateAsync(UnitContext c) => Task.FromResult(false);
        public Task RemoveAsync(UnitContext c) => Task.CompletedTask;

        public Task AwaitSettledAsync(UnitContext c)
        {
            if (Report.TryGetValue(c.Name, out var percent))
                c.Report(new ProgressReport(c.Name, "halfway", percent));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ResourceState>> RefreshAsync(UnitContext c)
            => Task.FromResult<IReadOnlyList<ResourceState>>([]);
    }
}
