using Xunit;

namespace Tyanor.Tests;

/// <summary>
/// The reconcile decision — the whole resume model. These are pure and always-on because a mocked SDK
/// cannot catch a wrong decision here: it would look like a differently-ordered deployment, and the one
/// branch that matters most (Attach) fails by SUCCEEDING at something it should never have started.
/// </summary>
public class ReconcileTests
{
    [Fact]
    public void A_converging_unit_is_attached_to_and_never_re_issued()
    {
        // The branch resume exists for. Re-issuing against an in-flight operation is either rejected by
        // the provider or — worse — accepted, starting a second conflicting operation.
        var action = Reconcile.Decide(UnitPhase.Converging);

        Assert.Equal(ReconcileAction.Attach, action);
        Assert.False(Reconcile.Mutates(action));
    }

    [Theory]
    [InlineData(UnitPhase.Missing, ReconcileAction.Create)]
    [InlineData(UnitPhase.Ready, ReconcileAction.Update)]
    [InlineData(UnitPhase.Broken, ReconcileAction.Recreate)]
    [InlineData(UnitPhase.Unwinding, ReconcileAction.SettleThenRecreate)]
    public void Every_other_phase_maps_to_its_one_action(UnitPhase phase, ReconcileAction expected)
        => Assert.Equal(expected, Reconcile.Decide(phase));

    [Fact]
    public void Attach_is_the_only_non_mutating_action()
    {
        // Stated as a test because it is a contract a provider adapter can silently break: an adapter
        // that "helpfully" re-applies while attaching reintroduces exactly the conflict Attach prevents.
        var mutating = Enum.GetValues<ReconcileAction>().Where(Reconcile.Mutates).ToArray();

        Assert.DoesNotContain(ReconcileAction.Attach, mutating);
        Assert.Equal(Enum.GetValues<ReconcileAction>().Length - 1, mutating.Length);
    }

    [Fact]
    public void A_broken_unit_is_remade_rather_than_updated()
    {
        // Providers refuse to update a unit that settled badly (CloudFormation's ROLLBACK_COMPLETE is the
        // canonical case). Attempting an update there fails every retry and never self-heals.
        Assert.Equal(ReconcileAction.Recreate, Reconcile.Decide(UnitPhase.Broken));
        Assert.NotEqual(ReconcileAction.Update, Reconcile.Decide(UnitPhase.Broken));
    }
}

/// <summary>
/// How a classified failure becomes an outcome. The rule is one line — only Hard is terminal — and it is
/// the difference between a tool that resumes and one that throws away twenty minutes of correct work.
/// </summary>
public class OutcomeTests
{
    [Theory]
    [InlineData(FailureClass.Credentials, "credentials")]
    [InlineData(FailureClass.Transient, "transient")]
    public void A_recoverable_failure_pauses_with_a_reason(FailureClass failure, string reason)
    {
        var outcome = OperationOutcome.From(failure, "boom");

        Assert.False(outcome.Ok);
        Assert.True(outcome.Resumable);
        Assert.Equal(reason, outcome.Reason?.Value);
    }

    [Fact]
    public void A_hard_failure_is_terminal_and_carries_no_resume_offer()
    {
        var outcome = OperationOutcome.From(FailureClass.Hard, "malformed template");

        Assert.False(outcome.Ok);
        Assert.False(outcome.Resumable);
        Assert.Null(outcome.Reason);
        Assert.Equal("malformed template", outcome.Error);
    }

    [Fact]
    public void A_hard_failure_still_says_something_when_the_provider_said_nothing()
    {
        // An outcome with no error text gives the operator nothing to act on, so there is a floor.
        Assert.False(string.IsNullOrWhiteSpace(OperationOutcome.From(FailureClass.Hard).Error));
    }

    [Fact]
    public void Success_is_not_resumable_and_carries_no_error()
    {
        var outcome = OperationOutcome.Success();

        Assert.True(outcome.Ok);
        Assert.False(outcome.Resumable);
        Assert.Null(outcome.Error);
    }
}

/// <summary>A run's live-ness is what protects in-flight work from being deleted or double-started.</summary>
public class RunRecordTests
{
    private static RunRecord Run(RunStatus status) =>
        new("r1", "site", "acme", RunKind.Apply, status, DateTimeOffset.UnixEpoch);

    [Theory]
    [InlineData(RunStatus.Running)]
    [InlineData(RunStatus.Paused)]
    public void Running_and_paused_are_both_live(RunStatus status)
    {
        // Paused is live because the provider may still be converging — the pause is OUR stop, not its.
        Assert.True(Run(status).IsLive);
        Assert.True(Run(status).Resumable);
    }

    [Theory]
    [InlineData(RunStatus.Succeeded)]
    [InlineData(RunStatus.Failed)]
    public void A_finished_run_is_not_live(RunStatus status) => Assert.False(Run(status).IsLive);
}
