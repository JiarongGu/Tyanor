using Tyanor.Engine;
using Xunit;

namespace Tyanor.Tests;

/// <summary>
/// The engine, driven by a fake provider. These cover the behaviours that make a run resumable — and they
/// exist because each one fails SILENTLY in production: a re-issued operation looks like a slow deploy, a
/// credential error classified as terminal looks like a legitimate failure, and a skipped unit looks like
/// a fast one.
/// </summary>
public class ProcedureRunnerTests
{
    private static readonly Procedure Site = new("site",
    [
        new ProcedureUnit("db", "Database"),
        new ProcedureUnit("api", "API"),
        new ProcedureUnit("web", "Website"),
    ]);

    private static DeploymentRequest Request() =>
        new("acme", new DeploymentArtifact(new Dictionary<string, string> { ["infrastructure"] = "/tmp/x" }));

    [Fact]
    public async Task A_clean_run_creates_every_unit_in_order()
    {
        var driver = new FakeDriver();                       // everything Missing
        var runner = new ProcedureRunner(new FakeTarget(driver), new FakeHistory());

        var outcome = await runner.ApplyAsync(Site, Request(), _ => { });

        Assert.True(outcome.Ok);
        Assert.Equal(["db:create", "api:create", "web:create"], driver.Calls);
    }

    [Fact]
    public async Task Resuming_skips_what_is_done_attaches_to_what_is_running_and_creates_the_rest()
    {
        // The exact shape of an interrupted run: one unit finished, one was mid-flight when the process
        // died, one never started. A single re-run has to do the right thing for all three.
        var driver = new FakeDriver
        {
            Phases = { ["db"] = UnitPhase.Ready, ["api"] = UnitPhase.Converging, ["web"] = UnitPhase.Missing },
            NothingToUpdate = { "db" },
        };
        var runner = new ProcedureRunner(new FakeTarget(driver), new FakeHistory());

        var outcome = await runner.ApplyAsync(Site, Request(), _ => { });

        Assert.True(outcome.Ok);
        // db: an update that reports no change. api: WAITED ON, never re-issued. web: created.
        Assert.Equal(["db:update", "api:await", "web:create"], driver.Calls);
        Assert.DoesNotContain("api:create", driver.Calls);
        Assert.DoesNotContain("api:update", driver.Calls);
    }

    [Fact]
    public async Task A_broken_unit_is_removed_and_remade()
    {
        var driver = new FakeDriver { Phases = { ["db"] = UnitPhase.Broken } };
        var runner = new ProcedureRunner(new FakeTarget(driver), new FakeHistory());

        await runner.ApplyAsync(new Procedure("one", [new ProcedureUnit("db", "Database")]), Request(), _ => { });

        Assert.Equal(["db:remove", "db:create"], driver.Calls);
    }

    [Fact]
    public async Task An_expired_credential_pauses_the_run_and_keeps_it_live()
    {
        // The defining behaviour. A tool that fails here throws away everything already provisioned.
        var driver = new FakeDriver { Throw = { ["api"] = new FakeCredentialError() } };
        var history = new FakeHistory();
        var runner = new ProcedureRunner(new FakeTarget(driver), history);

        var outcome = await runner.ApplyAsync(Site, Request(), _ => { });

        Assert.False(outcome.Ok);
        Assert.True(outcome.Resumable);
        Assert.Equal("credentials", outcome.Reason?.Value);
        Assert.Equal(RunStatus.Paused, history.Last!.Status);
        Assert.True(history.Last.IsLive);                    // still protected from deletion
        Assert.Null(history.Last.FinishedAt);                // a pause has not finished
    }

    [Fact]
    public async Task A_hard_failure_fails_the_run_terminally()
    {
        var driver = new FakeDriver { Throw = { ["api"] = new InvalidOperationException("malformed") } };
        var history = new FakeHistory();
        var runner = new ProcedureRunner(new FakeTarget(driver), history);

        var outcome = await runner.ApplyAsync(Site, Request(), _ => { });

        Assert.False(outcome.Resumable);
        Assert.Equal(RunStatus.Failed, history.Last!.Status);
        Assert.NotNull(history.Last.FinishedAt);
    }

    [Fact]
    public async Task A_transient_error_is_retried_rather_than_surfaced()
    {
        var driver = new FakeDriver { ThrowOnceThenSucceed = { ["api"] = new FakeTransientError() } };
        var runner = new ProcedureRunner(new FakeTarget(driver), new FakeHistory(),
            retry: new RetryPolicy(Attempts: 3, BaseDelay: TimeSpan.FromMilliseconds(1)));

        var outcome = await runner.ApplyAsync(Site, Request(), _ => { });

        Assert.True(outcome.Ok);
        Assert.Equal(2, driver.Attempts["api"]);             // failed once, succeeded on the retry
    }

    [Fact]
    public async Task Teardown_runs_in_reverse_and_tolerates_what_is_already_gone()
    {
        // db + api are really there; web was already removed (a re-run of an interrupted teardown).
        var driver = new FakeDriver
        {
            Phases = { ["db"] = UnitPhase.Ready, ["api"] = UnitPhase.Ready, ["web"] = UnitPhase.Missing },
        };
        var runner = new ProcedureRunner(new FakeTarget(driver), new FakeHistory());

        var outcome = await runner.DestroyAsync(Site, Request(), _ => { });

        Assert.True(outcome.Ok);
        // web was already gone; edge-first order otherwise, so importers die before what they import.
        Assert.Equal(["api:remove", "db:remove"], driver.Calls);
    }

    [Fact]
    public async Task A_resume_continues_the_SAME_run_rather_than_starting_another()
    {
        var history = new FakeHistory();
        var runner = new ProcedureRunner(new FakeTarget(new FakeDriver()), history);

        await runner.ApplyAsync(Site, Request(), _ => { }, runId: "run-1");

        Assert.All(history.All, r => Assert.Equal("run-1", r.Id));
    }

    // ── fakes ────────────────────────────────────────────────────────────────────────────────────
    private sealed class FakeCredentialError : Exception;
    private sealed class FakeTransientError : Exception;

    private sealed class FakeTarget(FakeDriver driver) : IDeploymentTarget, IFailureClassifier
    {
        public string Id => "fake";
        public IUnitDriver Driver => driver;
        public IFailureClassifier Classifier => this;
        public Task<TargetIdentity> ValidateAsync(TargetCredentials? c, CancellationToken ct) => Task.FromResult(new TargetIdentity(true));

        public FailureClass? Classify(Exception error)
        {
            for (Exception? e = error; e is not null; e = e.InnerException)
            {
                if (e is FakeCredentialError) return FailureClass.Credentials;
                if (e is FakeTransientError) return FailureClass.Transient;
            }
            return null;
        }
    }

    private sealed class FakeDriver : IUnitDriver
    {
        public List<string> Calls { get; } = [];
        public Dictionary<string, UnitPhase> Phases { get; } = [];
        public HashSet<string> NothingToUpdate { get; } = [];
        public Dictionary<string, Exception> Throw { get; } = [];
        public Dictionary<string, Exception> ThrowOnceThenSucceed { get; } = [];
        public Dictionary<string, int> Attempts { get; } = [];

        private void Guard(UnitContext c)
        {
            Attempts[c.Name] = Attempts.GetValueOrDefault(c.Name) + 1;
            if (Throw.TryGetValue(c.Name, out var always)) throw always;
            if (ThrowOnceThenSucceed.TryGetValue(c.Name, out var once) && Attempts[c.Name] == 1) throw once;
        }

        public Task<UnitPhase> PhaseAsync(UnitContext c)
        {
            Guard(c);
            return Task.FromResult(Phases.GetValueOrDefault(c.Name, UnitPhase.Missing));
        }

        public Task CreateAsync(UnitContext c)
        { Calls.Add($"{c.Name}:create"); return Task.CompletedTask; }

        public Task<bool> UpdateAsync(UnitContext c)
        { Calls.Add($"{c.Name}:update"); return Task.FromResult(!NothingToUpdate.Contains(c.Name)); }

        public Task RemoveAsync(UnitContext c)
        { Calls.Add($"{c.Name}:remove"); return Task.CompletedTask; }

        /// <summary>What the unit "holds" — scripted per unit; empty unless a test says otherwise.</summary>
        public Dictionary<string, List<ResourceState>> Resources { get; } = [];

        public Task<IReadOnlyList<ResourceState>> RefreshAsync(UnitContext c)
            => Task.FromResult<IReadOnlyList<ResourceState>>(Resources.GetValueOrDefault(c.Name) ?? []);

        public Task AwaitSettledAsync(UnitContext c)
        {
            // Only recorded when it is the WHOLE action (Attach) — otherwise every create would log one.
            if (Phases.GetValueOrDefault(c.Name) == UnitPhase.Converging) Calls.Add($"{c.Name}:await");
            return Task.CompletedTask;
        }
    }

    private sealed class FakeHistory : IRunHistory
    {
        public List<RunRecord> All { get; } = [];
        public RunRecord? Last => All.LastOrDefault();
        public Task UpsertAsync(RunRecord r, CancellationToken ct = default) { All.Add(r); return Task.CompletedTask; }
        public Task<RunRecord?> LiveAsync(string p, string x, CancellationToken ct = default) => Task.FromResult<RunRecord?>(null);
        public Task<IReadOnlyList<RunRecord>> RecentAsync(int limit = 50, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<RunRecord>>(All);
        public Task DeleteAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
    }
}
