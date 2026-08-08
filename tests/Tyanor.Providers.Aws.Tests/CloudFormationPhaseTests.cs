using System.Reflection;
using Amazon.CloudFormation;
using Xunit;

namespace Tyanor.Providers.Aws.Tests;

/// <summary>
/// The phase table, against the real CloudFormation status strings.
///
/// <para>This is the most valuable test in the provider and the one a mocked SDK cannot replace: a mock
/// agrees with whatever status string the test hands it, so it can never catch one spelled wrong or one
/// nobody thought about. Every wrong answer here is quiet — an update issued against a stack that refuses
/// it, a wait that never ends, or a database deleted and remade for no reason.</para>
/// </summary>
public class CloudFormationPhaseTests
{
    /// <summary>
    /// Every status CloudFormation can report, and what it means. This list is the specification; the
    /// exhaustiveness test below refuses to let it fall behind the SDK.
    /// </summary>
    public static readonly Dictionary<string, UnitPhase> Table = new()
    {
        // Nothing deployed.
        ["DELETE_COMPLETE"] = UnitPhase.Missing,

        // Work in flight — attach, issue nothing.
        ["CREATE_IN_PROGRESS"] = UnitPhase.Converging,
        ["UPDATE_IN_PROGRESS"] = UnitPhase.Converging,
        ["UPDATE_COMPLETE_CLEANUP_IN_PROGRESS"] = UnitPhase.Converging,
        ["IMPORT_IN_PROGRESS"] = UnitPhase.Converging,

        // Rollbacks that revert to a PREVIOUS GOOD state. Converging, not unwinding: what they settle into
        // is usable, and the wait reports the rollback itself as a failure so a reverted update is never
        // mistaken for one that shipped.
        ["UPDATE_ROLLBACK_IN_PROGRESS"] = UnitPhase.Converging,
        ["UPDATE_ROLLBACK_COMPLETE_CLEANUP_IN_PROGRESS"] = UnitPhase.Converging,
        ["IMPORT_ROLLBACK_IN_PROGRESS"] = UnitPhase.Converging,

        // Going away, or heading somewhere CloudFormation will not update. Settle, then remake.
        ["ROLLBACK_IN_PROGRESS"] = UnitPhase.Unwinding,
        ["DELETE_IN_PROGRESS"] = UnitPhase.Unwinding,

        // Settled and usable.
        ["CREATE_COMPLETE"] = UnitPhase.Ready,
        ["UPDATE_COMPLETE"] = UnitPhase.Ready,
        ["UPDATE_ROLLBACK_COMPLETE"] = UnitPhase.Ready,
        ["IMPORT_COMPLETE"] = UnitPhase.Ready,
        ["IMPORT_ROLLBACK_COMPLETE"] = UnitPhase.Ready,

        // Settled and unusable.
        ["ROLLBACK_COMPLETE"] = UnitPhase.Broken,
        ["CREATE_FAILED"] = UnitPhase.Broken,
        ["ROLLBACK_FAILED"] = UnitPhase.Broken,
        ["DELETE_FAILED"] = UnitPhase.Broken,
        ["UPDATE_FAILED"] = UnitPhase.Broken,
        ["UPDATE_ROLLBACK_FAILED"] = UnitPhase.Broken,
        ["IMPORT_ROLLBACK_FAILED"] = UnitPhase.Broken,
        ["REVIEW_IN_PROGRESS"] = UnitPhase.Broken,
    };

    public static TheoryData<string, UnitPhase> Statuses()
    {
        var data = new TheoryData<string, UnitPhase>();
        foreach (var (status, phase) in Table) data.Add(status, phase);
        return data;
    }

    [Theory]
    [MemberData(nameof(Statuses))]
    public void Every_real_status_maps_to_the_phase_it_means(string status, UnitPhase expected)
        => Assert.Equal(expected, CloudFormationPhases.Of(status));

    [Fact]
    public void A_stack_that_does_not_exist_is_Missing()
    {
        // CloudFormation answers "no such stack" by refusing to describe it, so absence arrives as no
        // status at all rather than as a status meaning absent.
        Assert.Equal(UnitPhase.Missing, CloudFormationPhases.Of(null));
        Assert.Equal(UnitPhase.Missing, CloudFormationPhases.Of(""));
    }

    [Fact]
    public void The_table_covers_every_status_the_SDK_knows_about()
    {
        // The check that stops this table rotting. AWS adds statuses; when a future SDK upgrade brings one,
        // this fails and a person decides what it means — rather than it landing silently in the fallback,
        // where it would be treated as Broken and quietly replace a stack.
        var known = typeof(StackStatus)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(StackStatus))
            .Select(f => f.GetValue(null)!.ToString()!)
            .ToList();

        // Prove the reflection found the real list before trusting what it says: NotEmpty alone would pass
        // on one field, and a lookup that silently found nothing would make this test vacuous — which is
        // worse than not having it, because it reads as coverage.
        Assert.Contains("CREATE_COMPLETE", known);
        Assert.Contains("UPDATE_ROLLBACK_COMPLETE", known);
        Assert.True(known.Count >= 20, $"only {known.Count} statuses discovered — has the SDK layout changed?");

        foreach (var status in known) Assert.Contains(status, (IReadOnlyDictionary<string, UnitPhase>)Table);
    }

    [Fact]
    public void An_unknown_status_is_BROKEN_rather_than_assumed_healthy()
        // Broken produces a replace, which a plan shows the operator before it happens. Ready would issue
        // an update against a stack in a state nobody here understood.
        => Assert.Equal(UnitPhase.Broken, CloudFormationPhases.Of("SOMETHING_AWS_ADDED_TUESDAY"));

    [Fact]
    public void The_two_rollback_COMPLETEs_are_one_character_apart_and_opposite()
    {
        // The subtlest pair in the file. ROLLBACK_COMPLETE is a failed CREATE: CloudFormation refuses to
        // update it and it can only be deleted. UPDATE_ROLLBACK_COMPLETE is a failed UPDATE: the stack is
        // back at its previous good configuration and is perfectly updatable. Treating them alike deletes a
        // working stack because one template change was wrong.
        Assert.Equal(UnitPhase.Broken, CloudFormationPhases.Of("ROLLBACK_COMPLETE"));
        Assert.Equal(UnitPhase.Ready, CloudFormationPhases.Of("UPDATE_ROLLBACK_COMPLETE"));

        Assert.Equal(ReconcileAction.Recreate, Reconcile.Decide(CloudFormationPhases.Of("ROLLBACK_COMPLETE")));
        Assert.Equal(ReconcileAction.Update, Reconcile.Decide(CloudFormationPhases.Of("UPDATE_ROLLBACK_COMPLETE")));
    }

    [Fact]
    public void The_one_IN_PROGRESS_that_is_not_progressing_is_not_attached_to()
    {
        // REVIEW_IN_PROGRESS is a stack shell left by a change set that was created and never executed.
        // Nothing is happening, so attaching would wait for an event that never comes — a hang rather than
        // an error, which is the worst way for a deploy tool to fail.
        Assert.Equal(UnitPhase.Broken, CloudFormationPhases.Of("REVIEW_IN_PROGRESS"));
        Assert.NotEqual(ReconcileAction.Attach, Reconcile.Decide(CloudFormationPhases.Of("REVIEW_IN_PROGRESS")));
    }

    [Theory]
    [InlineData("ROLLBACK_COMPLETE")]
    [InlineData("UPDATE_ROLLBACK_COMPLETE")]
    [InlineData("CREATE_FAILED")]
    [InlineData("DELETE_FAILED")]
    public void A_wait_that_ends_in_a_rollback_or_a_failure_reports_it(string status)
        // Including UPDATE_ROLLBACK_COMPLETE, which leaves a usable stack: usable at the WRONG
        // configuration. A run that returned success over it would tell the operator their change shipped.
        => Assert.True(CloudFormationPhases.SettledBadly(status));

    [Theory]
    [InlineData("CREATE_COMPLETE")]
    [InlineData("UPDATE_COMPLETE")]
    [InlineData("DELETE_COMPLETE")]
    [InlineData(null)]
    public void A_wait_that_ends_well_reports_nothing(string? status)
        => Assert.False(CloudFormationPhases.SettledBadly(status));

    [Theory]
    [InlineData("CREATE_IN_PROGRESS", false)]
    [InlineData("UPDATE_ROLLBACK_COMPLETE_CLEANUP_IN_PROGRESS", false)]
    [InlineData("CREATE_COMPLETE", true)]
    [InlineData("ROLLBACK_COMPLETE", true)]
    [InlineData(null, true)]
    public void A_wait_stops_when_the_stack_stops_moving(string? status, bool settled)
        => Assert.Equal(settled, CloudFormationPhases.Settled(status));

    [Fact]
    public void Nothing_to_change_is_recognised_from_what_CloudFormation_actually_says()
    {
        // The real message, and it arrives as an exception. On a resume this is the ordinary answer for
        // every unit that already finished, so mistaking it for an error would break resume outright.
        Assert.True(CloudFormationPhases.IsNoUpdatesNeeded(
            "No updates are to be performed."));
        Assert.True(CloudFormationPhases.IsNoUpdatesNeeded(
            "An error occurred (ValidationError) when calling UpdateStack: No updates are to be performed."));

        // A genuine validation failure shares the ValidationError code and must NOT be swallowed as success.
        Assert.False(CloudFormationPhases.IsNoUpdatesNeeded(
            "Template format error: Unresolved resource dependencies [Foo]"));
        Assert.False(CloudFormationPhases.IsNoUpdatesNeeded(null));
    }

    [Fact]
    public void Only_a_genuine_missing_stack_reads_as_missing()
    {
        Assert.True(CloudFormationPhases.IsStackMissing(
            "ValidationError", "Stack with id mysite-db does not exist"));

        // The bug this replaced: the ported code treated EVERY CloudFormation exception as "no such stack",
        // so a throttle read as absent and the create that followed hit a stack that was there all along.
        Assert.False(CloudFormationPhases.IsStackMissing("Throttling", "Rate exceeded"));
        Assert.False(CloudFormationPhases.IsStackMissing(
            "ValidationError", "Template format error: unsupported structure"));
        Assert.False(CloudFormationPhases.IsStackMissing(null, "does not exist"));
    }
}
