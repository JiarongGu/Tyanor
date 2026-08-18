using Tyanor.Engine;
using Tyanor.Engine.State;
using Tyanor.Testing;
using Xunit;

namespace Tyanor.Tests;

/// <summary>
/// WHICH failures are retried, which is doctrine and had no test.
///
/// <para><c>error-classification.md</c> says it in one line — "Retry only <c>Transient</c>. Credentials and
/// hard failures rethrow at once" — because retrying is itself a CLAIM. Retrying a transient blip is honest;
/// retrying a malformed request is a lie told five times; retrying an expired credential merely delays the
/// moment somebody can fix it.</para>
///
/// <para>Nothing checked any of that. Widening the retry to cover credentials passed the whole suite.</para>
/// </summary>
public class RetryDisciplineTests
{
    private static readonly Procedure One = new("site", [new ProcedureUnit("db", "Database")]);

    private static DeploymentRequest Request() =>
        new("acme", new DeploymentArtifact(new Dictionary<string, string>()));

    private static ProcedureRunner Runner(MemoryTarget target, int attempts) =>
        new(target, new InMemoryRunHistory(), null,
            new RetryPolicy(attempts, BaseDelay: TimeSpan.FromMilliseconds(1)));

    [Fact]
    public async Task A_CREDENTIAL_failure_is_tried_exactly_once()
    {
        // Re-authenticating is the operator's move, and no amount of waiting performs it for them. Retrying
        // only postpones the message that tells them what to do.
        var target = new MemoryTarget().Fails("db", FailureClass.Credentials);

        var outcome = await Runner(target, attempts: 5).ApplyAsync(One, Request());

        Assert.Equal(1, target.Attempts["db"]);
        Assert.True(outcome.Resumable);                       // …it still PAUSES, it just does not retry
        Assert.Equal("credentials", outcome.Reason?.Value);
    }

    [Fact]
    public async Task A_HARD_failure_is_tried_exactly_once()
    {
        // The template produced it and the same template produces it again. Five attempts is the same lie
        // told five times, and it costs the operator five times as long to hear it.
        var target = new MemoryTarget().Fails("db", FailureClass.Hard);

        var outcome = await Runner(target, attempts: 5).ApplyAsync(One, Request());

        Assert.Equal(1, target.Attempts["db"]);
        Assert.False(outcome.Resumable);
    }

    [Fact]
    public async Task An_error_NOBODY_recognises_is_tried_exactly_once()
    {
        // Unrecognised means Hard, which is the safe default precisely because the error nobody anticipated
        // is the one that must not be retried silently.
        var target = new MemoryTarget().Throws("db", new InvalidOperationException("something new"));

        var outcome = await Runner(target, attempts: 5).ApplyAsync(One, Request());

        Assert.Equal(1, target.Attempts["db"]);
        Assert.False(outcome.Resumable);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    public async Task A_TRANSIENT_failure_is_tried_exactly_as_often_as_the_budget_allows(int attempts)
    {
        // And then pauses rather than failing: nothing about the desired state is wrong, so the run is still
        // resumable — a 5xx that never stops is a pause, not a definition error.
        var target = new MemoryTarget().Fails("db", FailureClass.Transient);

        var outcome = await Runner(target, attempts).ApplyAsync(One, Request());

        Assert.Equal(attempts, target.Attempts["db"]);
        Assert.True(outcome.Resumable);
        Assert.Equal("transient", outcome.Reason?.Value);
    }

    [Fact]
    public async Task A_transient_blip_inside_the_budget_never_reaches_the_operator()
    {
        var target = new MemoryTarget().FailsOnce("db");

        Assert.True((await Runner(target, attempts: 3).ApplyAsync(One, Request())).Ok);
        Assert.Equal(2, target.Attempts["db"]);
    }
}

/// <summary>
/// What a run asks the provider that it does not have to — which is invisible until somebody counts.
/// </summary>
public class ProviderCallTests
{
    private static readonly Procedure Site = new("site",
        [new ProcedureUnit("db", "Database"), new ProcedureUnit("api", "API")]);

    private static DeploymentRequest Request() =>
        new("acme", new DeploymentArtifact(new Dictionary<string, string>()));

    [Fact]
    public async Task A_TEARDOWN_does_not_ask_what_a_removed_unit_owns
        () // it knows: nothing
    {
        // The engine passes `removed: true` when recording state for a destroyed unit, so it writes "owns
        // nothing" instead of refreshing. Equivalent in outcome against a correct driver — which is why
        // removing it broke no test — but it is one provider call per unit, and against a provider whose
        // removal is eventually consistent it is the difference between recording the truth and recording
        // a resource that is on its way out.
        var target = new MemoryTarget().AlreadyDeployed("db", "api");
        var state = new InMemoryStateStore();
        var runner = new ProcedureRunner(target, new InMemoryRunHistory(), state);

        Assert.True((await runner.DestroyAsync(Site, Request())).Ok);

        Assert.Empty(target.Refreshes);
        Assert.Empty((await state.GetAsync("site", "acme")).RecordedUnits);
    }

    [Fact]
    public async Task An_APPLY_does_ask_each_unit_what_it_owns_so_state_records_reality()
    {
        // The other direction: state records what IS, straight from the provider, never what was intended.
        var target = new MemoryTarget();
        var state = new InMemoryStateStore();
        var runner = new ProcedureRunner(target, new InMemoryRunHistory(), state);

        Assert.True((await runner.ApplyAsync(Site, Request())).Ok);

        Assert.Equal(1, target.Refreshes["db"]);
        Assert.Equal(1, target.Refreshes["api"]);
    }

    [Fact]
    public async Task With_NO_state_store_a_run_asks_nothing_at_all()
    {
        // Nothing to record, so nothing to ask. A runner without state should not be paying for refreshes
        // whose answers are thrown away.
        var target = new MemoryTarget();

        Assert.True((await new ProcedureRunner(target, new InMemoryRunHistory())
            .ApplyAsync(Site, Request())).Ok);

        Assert.Empty(target.Refreshes);
    }
}
