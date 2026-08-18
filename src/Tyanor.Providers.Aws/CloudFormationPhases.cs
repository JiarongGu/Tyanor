namespace Tyanor.Providers.Aws;

/// <summary>
/// CloudFormation's status vocabulary, mapped onto <see cref="UnitPhase"/>. The ONLY place a CFN status
/// string is interpreted.
///
/// <para><b>This table is where a silent bug lives.</b> Every wrong answer here looks like something else:
/// a status read as <see cref="UnitPhase.Ready"/> that is not becomes an update against a stack that
/// refuses it, one read as <see cref="UnitPhase.Converging"/> that is not becomes a wait that never ends,
/// and one read as <see cref="UnitPhase.Broken"/> that is not becomes a stack — and a database —
/// deleted and remade for no reason. So it is a pure function over strings, tested against the real ones.</para>
/// </summary>
internal static class CloudFormationPhases
{
    /// <summary>
    /// Map a stack status. <paramref name="status"/> is null when the stack does not exist, which
    /// CloudFormation reports by refusing to describe it rather than by a status.
    /// </summary>
    public static UnitPhase Of(string? status) => status switch
    {
        // Gone, or never there. DELETE_COMPLETE is a tombstone CloudFormation keeps for a while; it means
        // nothing is deployed, and reading it as a *_COMPLETE "Ready" would skip the create entirely.
        null or "" or Deleted => UnitPhase.Missing,

        // A stack shell left by a change set that was created and never executed. It ends in _IN_PROGRESS
        // and NOTHING is happening — the one status where "in progress" is a lie, and attaching to it would
        // wait for an event that never comes.
        Review => UnitPhase.Broken,

        // Going away, or heading for a state CloudFormation will not update. When it settles, remake it.
        //
        // ROLLBACK_IN_PROGRESS is the only rollback here, and the distinction is the subtlest thing in this
        // file: it is the rollback of a failed CREATE, so it settles into ROLLBACK_COMPLETE, which CFN
        // refuses to update and which can only be deleted. Every OTHER rollback is reverting to a previous
        // good state and is handled below.
        Rollback or Deleting => UnitPhase.Unwinding,

        // Settled, and CloudFormation will not update it. UPDATE_ROLLBACK_FAILED belongs here and it is the
        // expensive one: recovering it properly needs ContinueUpdateRollback, and the action this phase
        // produces is a delete and recreate. That is deliberate — it is what the deployer this was ported
        // from did — and it is safe only because a plan shows REPLACE before anything happens. Read
        // Plan.Replacements before applying to a stack holding data.
        RollbackComplete => UnitPhase.Broken,
        var s when s.EndsWith(Failed, StringComparison.Ordinal) => UnitPhase.Broken,

        // Settled and usable. UPDATE_ROLLBACK_COMPLETE is in here, one character away from the Broken
        // ROLLBACK_COMPLETE above: an update that failed and reverted leaves the stack at its PREVIOUS good
        // configuration, and CloudFormation is happy to update it again. Treating the two the same would
        // delete a working stack because one template change was wrong.
        var s when s.EndsWith(Complete, StringComparison.Ordinal) => UnitPhase.Ready,

        // Work in flight, including the rollbacks that revert to a good state (UPDATE_ROLLBACK_*,
        // IMPORT_ROLLBACK_*). Those are Converging rather than Unwinding because what they settle into is
        // usable — and the wait reports the rollback itself as a failure, so an update that reverted is
        // never mistaken for one that worked.
        var s when s.EndsWith(InProgress, StringComparison.Ordinal) => UnitPhase.Converging,

        // A status this table has never seen. Broken is the safe answer: it produces a replace, which a
        // plan shows the operator before it happens, whereas Ready would issue an update against a stack in
        // a state nobody here understood.
        _ => UnitPhase.Broken,
    };

    /// <summary>
    /// Whether a settled status means the operation FAILED, for
    /// <see cref="IUnitDriver.AwaitSettledAsync"/> — which must throw rather than return quietly.
    /// </summary>
    /// <remarks>
    /// Any rollback counts, including the ones that leave a usable stack. A stack that reverted is at the
    /// wrong configuration, and a run that reported success over it would be telling the operator their
    /// change shipped when it did not.
    /// </remarks>
    public static bool SettledBadly(string? status) =>
        status is not null
        && (status.Contains(RollbackWord, StringComparison.Ordinal)
            || status.EndsWith(Failed, StringComparison.Ordinal));

    /// <summary>
    /// Whether a status is settled — neither converging nor unwinding — so a wait can stop.
    /// </summary>
    public static bool Settled(string? status) =>
        status is null || status.Length == 0 || !status.EndsWith(InProgress, StringComparison.Ordinal);

    /// <summary>
    /// CloudFormation's way of saying an update would change nothing. It arrives as an exception, and it is
    /// a SUCCESS — on a resume it is the ordinary answer for every unit that already finished.
    /// </summary>
    /// <remarks>
    /// Matched on message text, which this file otherwise refuses to do, because CloudFormation gives this
    /// one a `ValidationError` code shared with genuine template errors. There is no code to match on, and
    /// pretending otherwise would misreport a real validation failure as "already up to date" — which is
    /// the worse mistake, so the check is deliberately narrow.
    /// </remarks>
    public static bool IsNoUpdatesNeeded(string? message) =>
        message is not null && message.Contains("No updates are to be performed", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a CloudFormation error means "there is no such stack" — the way it answers a question about a
    /// stack that was never created.
    /// </summary>
    /// <remarks>
    /// Narrow on purpose. The deployer this was ported from treated EVERY CloudFormation exception here as
    /// "the stack does not exist", which meant a throttle read as absent and the create that followed hit a
    /// stack that was there all along. A missing stack is one specific answer; everything else must be
    /// allowed to propagate and be classified, so a transient error is retried as one.
    /// </remarks>
    public static bool IsStackMissing(string? errorCode, string? message) =>
        errorCode == "ValidationError"
        && message is not null
        && message.Contains("does not exist", StringComparison.OrdinalIgnoreCase);

    private const string InProgress = "_IN_PROGRESS";
    private const string Complete = "_COMPLETE";
    private const string Failed = "_FAILED";
    private const string RollbackWord = "ROLLBACK";
    private const string Deleted = "DELETE_COMPLETE";
    private const string Deleting = "DELETE_IN_PROGRESS";
    private const string Rollback = "ROLLBACK_IN_PROGRESS";
    private const string RollbackComplete = "ROLLBACK_COMPLETE";
    private const string Review = "REVIEW_IN_PROGRESS";
}
