using Tyanor.Engine.State;
using Tyanor.Testing;
using Xunit;

namespace Tyanor.Tests;

/// <summary>
/// The contract suites, tested against implementations that are deliberately WRONG.
///
/// <para><b>Who watches the watchmen.</b> Every other use of these suites runs them against something that
/// passes, which proves only that green is reachable. Nothing proved that RED is: making
/// <c>ContractSuite</c> report every check as passing, or making <c>AssertAsync</c> never throw, left the
/// entire repository green while five implementations went on "proving" they behave.</para>
///
/// <para>That is the worst shape a test can have, because the suites are the entry ticket D15 offers to
/// providers written outside this repository. A broken suite does not fail — it silently certifies.</para>
///
/// <para>Each case below also documents what a check is FOR, by naming the mistake it exists to catch.</para>
/// </summary>
public class ContractSuiteTests
{
    private static async Task<IReadOnlyList<string>> FailuresOf(ContractSuite suite) =>
        [.. (await suite.RunAllAsync()).Where(r => !r.Passed).Select(r => r.Name)];

    // ── the machinery itself ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_failing_check_makes_AssertAsync_throw()
    {
        var suite = Driver(new AlwaysReady(new MemoryTarget()));

        var thrown = await Assert.ThrowsAsync<ContractException>(
            () => suite.AssertAsync("Nothing deployed reads as Missing"));

        Assert.Contains("IUnitDriver", thrown.Message);
    }

    [Fact]
    public async Task A_passing_check_makes_AssertAsync_return()
        => await Driver(new MemoryTarget()).AssertAsync("Nothing deployed reads as Missing");

    [Fact]
    public async Task AssertAll_reports_EVERY_failure_rather_than_the_first()
    {
        // Someone bringing a new implementation wants the whole list, not one item at a time across ten runs.
        var thrown = await Assert.ThrowsAsync<ContractException>(
            () => Driver(new AlwaysReady(new MemoryTarget())).AssertAllAsync());

        Assert.Contains("Nothing deployed reads as Missing", thrown.Message);
        Assert.Contains("After removing, it is Missing again", thrown.Message);
    }

    [Fact]
    public async Task A_check_that_THROWS_is_reported_as_a_failure_rather_than_escaping()
    {
        // An implementation that throws where the contract says it must return is exactly what this exists
        // to catch. Letting the exception out would report a broken TEST instead of a broken implementation.
        //
        // Outputs are declared so that every check actually engages: the two output checks no-op for a
        // fixture that produces none, which is deliberate and would otherwise look like two checks passing
        // against a driver that cannot do anything at all.
        var results = await Driver(new ExplodesOnPhase(new MemoryTarget()), ["web.url"]).RunAllAsync();

        Assert.All(results, r => Assert.False(r.Passed));
        Assert.All(results, r => Assert.Contains("threw", r.Detail!));
    }

    [Fact]
    public async Task Asking_for_a_check_that_does_not_exist_says_what_does()
    {
        var thrown = await Assert.ThrowsAsync<ArgumentException>(
            () => Driver(new MemoryTarget()).RunAsync("no such check"));

        Assert.Contains("Nothing deployed reads as Missing", thrown.Message);
    }

    // ── each driver check catches the mistake it is named for ────────────────────────────────────

    [Fact]
    public async Task A_driver_that_claims_to_exist_before_it_does_is_caught()
        => Assert.Contains("Nothing deployed reads as Missing",
            await FailuresOf(Driver(new AlwaysReady(new MemoryTarget()))));

    [Fact]
    public async Task A_driver_whose_PHASE_READ_creates_the_unit_is_caught()
        // The plan must be read-only, and this is the moment it matters most: planning something that does
        // not exist yet is the plan people run first.
        => Assert.Contains("Reading the phase of NOTHING does not bring it into being",
            await FailuresOf(Driver(new CreatesOnPhase(new MemoryTarget()))));

    [Fact]
    public async Task A_driver_that_always_reports_a_change_is_caught()
        // It redoes finished work on every resume, and makes a plan claim a redeploy will change things
        // when it will not.
        => Assert.Contains("Updating an unchanged deployment reports no change",
            await FailuresOf(Driver(new AlwaysChanged(new MemoryTarget()))));

    [Fact]
    public async Task A_driver_that_refuses_to_remove_what_is_already_gone_is_caught()
        // A teardown is resumed by running it again, so it meets units it has already removed.
        => Assert.Contains("Removing what is not there does not throw",
            await FailuresOf(Driver(new ThrowsOnAbsentRemove(new MemoryTarget()))));

    [Fact]
    public async Task A_driver_whose_resource_IDS_move_between_reads_is_caught()
        // Ids are what a diff matches on, so an id that changes makes every plan report the whole unit
        // destroyed and recreated.
        => Assert.Contains("Resource identity survives a refresh",
            await FailuresOf(Driver(new WanderingIds(new MemoryTarget()))));

    [Fact]
    public async Task A_driver_that_reports_one_resource_TWICE_is_caught()
        => Assert.Contains("Resource ids are unique within a unit",
            await FailuresOf(Driver(new DuplicateIds(new MemoryTarget()))));

    [Fact]
    public async Task A_driver_whose_OUTPUTS_outlive_the_unit_is_caught()
        // Outputs read from a stored copy keep answering after the thing is gone, and a UI goes on showing
        // an address that stopped resolving.
        => Assert.Contains("Outputs do not survive a remove",
            await FailuresOf(Driver(new StickyOutputs(new MemoryTarget()), ["web.url"])));

    // ── the classifier suite ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_classifier_reading_only_the_OUTERMOST_exception_is_caught()
    {
        // THE most common way a classifier goes quietly wrong: SDKs wrap the informative exception inside a
        // generic one, so an expired token reads as a hard failure and a deployment that only needed
        // re-authenticating is thrown away.
        var failures = await FailuresOf(new FailureClassifierContract(new OutermostOnly()));

        Assert.Contains("A wrapped credential error still classifies", failures);
    }

    [Fact]
    public async Task A_classifier_that_GUESSES_at_an_error_it_has_never_seen_is_caught()
        => Assert.Contains("An error it has never seen is not guessed at",
            await FailuresOf(new FailureClassifierContract(new GuessesTransient())));

    [Fact]
    public async Task A_fixture_that_supplies_no_samples_is_caught_rather_than_passing_vacuously()
        // A contract satisfiable by supplying nothing is worse than no contract.
        => Assert.Contains("Samples were supplied for the classes that matter",
            await FailuresOf(new FailureClassifierContract(new NoSamples())));

    // ── the storage suites ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_history_that_lets_a_LIVE_record_be_deleted_is_caught()
        // Deleting it strands work that may still be converging, with nothing left to say it is happening.
        => Assert.Contains("Deleting a live record is refused",
            await FailuresOf(new RunHistoryContract(() => new DeletesAnything())));

    [Fact]
    public async Task A_store_that_helpfully_turns_a_null_fingerprint_into_an_empty_string_is_caught()
        // It silently converts "the provider cannot tell whether this changed" into "unchanged", and the
        // drift it was meant to surface disappears.
        => Assert.Contains("A null fingerprint stays null",
            await FailuresOf(new StateStoreContract(() => new HelpfulStore())));

    [Fact]
    public async Task A_store_that_never_advances_the_serial_is_caught()
        // The check used to assert the opposite, and was satisfied by exactly this store.
        => Assert.Contains("The serial advances on every save",
            await FailuresOf(new StateStoreContract(() => new FrozenSerial())));

    // ── scaffolding ──────────────────────────────────────────────────────────────────────────────

    private static UnitDriverContract Driver(IUnitDriver driver, IReadOnlyCollection<string>? outputs = null) =>
        new(new Fixture(driver, outputs ?? []));

    private sealed class Fixture(IUnitDriver driver, IReadOnlyCollection<string> outputs) : IUnitDriverFixture
    {
        public IUnitDriver Driver => driver;

        public ProcedureUnit Unit { get; } = new("web", "Website");

        public DeploymentRequest Request { get; } =
            new("broken", new DeploymentArtifact(new Dictionary<string, string>()));

        public IReadOnlyCollection<string> ExpectedOutputs => outputs;

        public Task ResetAsync(CancellationToken ct)
        {
            // Deliberately tolerant: a driver broken enough to throw here is one whose checks should still
            // run and fail, rather than one that takes the suite down with it.
            try { return Driver.RemoveAsync(new UnitContext(Unit, Request)); }
            catch { return Task.CompletedTask; }
        }
    }

    /// <summary>Correct in every way but one. Each subclass below breaks exactly one promise.</summary>
    private class Delegating(IUnitDriver inner) : IUnitDriver
    {
        protected IUnitDriver Inner { get; } = inner;

        public virtual Task<UnitPhase> PhaseAsync(UnitContext c) => Inner.PhaseAsync(c);
        public virtual Task CreateAsync(UnitContext c) => Inner.CreateAsync(c);
        public virtual Task<bool> UpdateAsync(UnitContext c) => Inner.UpdateAsync(c);
        public virtual Task RemoveAsync(UnitContext c) => Inner.RemoveAsync(c);
        public virtual Task AwaitSettledAsync(UnitContext c) => Inner.AwaitSettledAsync(c);
        public virtual Task<IReadOnlyList<ResourceState>> RefreshAsync(UnitContext c) => Inner.RefreshAsync(c);
        public virtual Task<IReadOnlyList<string>> ValidateAsync(UnitContext c) => Inner.ValidateAsync(c);
        public virtual Task<IReadOnlyDictionary<string, string>> OutputsAsync(UnitContext c) => Inner.OutputsAsync(c);
    }

    private sealed class AlwaysReady(IUnitDriver inner) : Delegating(inner)
    {
        public override Task<UnitPhase> PhaseAsync(UnitContext c) => Task.FromResult(UnitPhase.Ready);
    }

    private sealed class CreatesOnPhase(IUnitDriver inner) : Delegating(inner)
    {
        public override async Task<UnitPhase> PhaseAsync(UnitContext c)
        {
            await Inner.CreateAsync(c);                     // "helpfully" repairs while reporting
            return await Inner.PhaseAsync(c);
        }
    }

    private sealed class AlwaysChanged(IUnitDriver inner) : Delegating(inner)
    {
        public override async Task<bool> UpdateAsync(UnitContext c)
        {
            await Inner.UpdateAsync(c);
            return true;
        }
    }

    private sealed class ThrowsOnAbsentRemove(IUnitDriver inner) : Delegating(inner)
    {
        public override async Task RemoveAsync(UnitContext c)
        {
            if (await Inner.PhaseAsync(c) == UnitPhase.Missing)
                throw new InvalidOperationException("there is nothing here to remove");

            await Inner.RemoveAsync(c);
        }
    }

    private sealed class WanderingIds(IUnitDriver inner) : Delegating(inner)
    {
        public override async Task<IReadOnlyList<ResourceState>> RefreshAsync(UnitContext c) =>
            [.. (await Inner.RefreshAsync(c)).Select(r => r with { Id = $"{r.Id}-{Guid.NewGuid():N}" })];
    }

    private sealed class DuplicateIds(IUnitDriver inner) : Delegating(inner)
    {
        public override async Task<IReadOnlyList<ResourceState>> RefreshAsync(UnitContext c)
        {
            var found = await Inner.RefreshAsync(c);
            return [.. found, .. found];
        }
    }

    private sealed class StickyOutputs(IUnitDriver inner) : Delegating(inner)
    {
        public override Task<IReadOnlyDictionary<string, string>> OutputsAsync(UnitContext c) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(
                new Dictionary<string, string> { ["web.url"] = "remembered from last time" });
    }

    private sealed class ExplodesOnPhase(IUnitDriver inner) : Delegating(inner)
    {
        public override Task<UnitPhase> PhaseAsync(UnitContext c) =>
            throw new InvalidOperationException("this driver does not work at all");

        public override Task CreateAsync(UnitContext c) =>
            throw new InvalidOperationException("this driver does not work at all");

        public override Task RemoveAsync(UnitContext c) =>
            throw new InvalidOperationException("this driver does not work at all");

        public override Task<IReadOnlyList<ResourceState>> RefreshAsync(UnitContext c) =>
            throw new InvalidOperationException("this driver does not work at all");

        public override Task<bool> UpdateAsync(UnitContext c) =>
            throw new InvalidOperationException("this driver does not work at all");

        public override Task<IReadOnlyDictionary<string, string>> OutputsAsync(UnitContext c) =>
            throw new InvalidOperationException("this driver does not work at all");

        public override Task<IReadOnlyList<string>> ValidateAsync(UnitContext c) =>
            throw new InvalidOperationException("this driver does not work at all");

        public override Task AwaitSettledAsync(UnitContext c) =>
            throw new InvalidOperationException("this driver does not work at all");
    }

    /// <summary>Reads only the outermost exception — the classic mistake.</summary>
    private sealed class OutermostOnly : IFailureClassifierFixture, IFailureClassifier
    {
        public IFailureClassifier Classifier => this;

        public IReadOnlyList<Exception> CredentialErrors { get; } =
            [new MemoryFaultException(FailureClass.Credentials, "expired")];

        public IReadOnlyList<Exception> TransientErrors { get; } =
            [new MemoryFaultException(FailureClass.Transient, "throttled")];

        public IReadOnlyList<Exception> HardErrors { get; } = [];

        public FailureClass? Classify(Exception error) =>
            error is MemoryFaultException fault ? fault.Failure : null;
    }

    /// <summary>Calls anything it does not recognise transient, so an unknown error is retried for ever.</summary>
    private sealed class GuessesTransient : IFailureClassifierFixture, IFailureClassifier
    {
        public IFailureClassifier Classifier => this;
        public IReadOnlyList<Exception> CredentialErrors { get; } = [new MemoryFaultException(FailureClass.Credentials, "x")];
        public IReadOnlyList<Exception> TransientErrors { get; } = [new MemoryFaultException(FailureClass.Transient, "x")];
        public IReadOnlyList<Exception> HardErrors { get; } = [];

        public FailureClass? Classify(Exception error)
        {
            for (Exception? e = error; e is not null; e = e.InnerException)
                if (e is MemoryFaultException fault) return fault.Failure;

            return FailureClass.Transient;                   // the guess
        }
    }

    private sealed class NoSamples : IFailureClassifierFixture
    {
        public IFailureClassifier Classifier { get; } = new MemoryTarget();
        public IReadOnlyList<Exception> CredentialErrors { get; } = [];
        public IReadOnlyList<Exception> TransientErrors { get; } = [];
        public IReadOnlyList<Exception> HardErrors { get; } = [];
    }

    private sealed class DeletesAnything : IRunHistory
    {
        private readonly InMemoryRunHistory _inner = new();

        public Task UpsertAsync(RunRecord record, CancellationToken ct = default) => _inner.UpsertAsync(record, ct);
        public Task<RunRecord?> LiveAsync(string p, string x, CancellationToken ct = default) => _inner.LiveAsync(p, x, ct);
        public Task<IReadOnlyList<RunRecord>> RecentAsync(int limit = 50, CancellationToken ct = default) => _inner.RecentAsync(limit, ct);

        /// <summary>No guard at all — the thing every history must refuse.</summary>
        public async Task DeleteAsync(string id, CancellationToken ct = default)
        {
            try { await _inner.DeleteAsync(id, ct); }
            catch (InvalidOperationException) { /* "helpfully" ignore the refusal */ }
        }
    }

    private sealed class HelpfulStore : IStateStore
    {
        private readonly InMemoryStateStore _inner = new();

        public Task<DeploymentState> GetAsync(string p, string x, CancellationToken ct = default) => _inner.GetAsync(p, x, ct);
        public Task DeleteAsync(string p, string x, CancellationToken ct = default) => _inner.DeleteAsync(p, x, ct);

        /// <summary>Tidies a null fingerprint away, losing the difference between unknown and unchanged.</summary>
        public Task SaveAsync(DeploymentState state, CancellationToken ct = default) =>
            _inner.SaveAsync(state with
            {
                Units = [.. state.Units.Select(u => u with
                {
                    Resources = [.. u.Resources.Select(r => r with { Fingerprint = r.Fingerprint ?? "" })],
                })],
            }, ct);
    }

    private sealed class FrozenSerial : IStateStore
    {
        private readonly Dictionary<(string, string), DeploymentState> _states = [];

        public Task<DeploymentState> GetAsync(string p, string x, CancellationToken ct = default) =>
            Task.FromResult(_states.GetValueOrDefault((p, x)) ?? DeploymentState.Empty(p, x));

        /// <summary>Stores faithfully, and never advances the version — so no clobber can be detected.</summary>
        public Task SaveAsync(DeploymentState state, CancellationToken ct = default)
        {
            _states[(state.Procedure, state.Prefix)] = state;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string p, string x, CancellationToken ct = default)
        {
            _states.Remove((p, x));
            return Task.CompletedTask;
        }
    }
}
