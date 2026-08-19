namespace Tyanor;

/// <summary>Where a run got to. <see cref="Running"/> and <see cref="Paused"/> are both LIVE.</summary>
public enum RunStatus
{
    /// <summary>Started and not yet finished — possibly by a process that is no longer alive.</summary>
    Running,

    /// <summary>Stopped for a reason that re-running resolves (see <see cref="PauseReason"/>).</summary>
    Paused,

    /// <summary>Finished as intended.</summary>
    Succeeded,

    /// <summary>Stopped terminally. Re-running will not help until something changes.</summary>
    Failed,
}

/// <summary>What a run was trying to do — deploy forward, or tear down.</summary>
public enum RunKind
{
    /// <summary>Converge the target on the desired state.</summary>
    Apply,

    /// <summary>Destroy everything the procedure created, in reverse order.</summary>
    Destroy,
}

/// <summary>
/// One recorded run. This is the "development status" the operator sees, and it is deliberately a record
/// of INTENT — what was attempted, with what, and how it ended — never a mirror of the provider's state.
/// </summary>
/// <param name="Id">Stable across a pause and its resume: resuming continues a run, it does not start one.</param>
/// <param name="Procedure">Which procedure was run.</param>
/// <param name="Prefix">Which deployment of it — the operator-chosen name base.</param>
/// <param name="Kind">Apply or remove.</param>
/// <param name="Status">Where it got to.</param>
/// <param name="StartedAt">When the attempt began.</param>
/// <param name="FinishedAt">Null while still live.</param>
/// <param name="Reason">Set iff <see cref="RunStatus.Paused"/>.</param>
/// <param name="Error">Provider or engine detail for a stop.</param>
public sealed record RunRecord(
    string Id,
    string Procedure,
    string Prefix,
    RunKind Kind,
    RunStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt = null,
    PauseReason? Reason = null,
    string? Error = null)
{
    /// <summary>
    /// A live run is the operator's only handle on work that may still be converging in the provider —
    /// so it is protected: never delete it, and never start a second run of the same procedure while one
    /// exists. See <c>.claude/rules/reconcile-dont-mirror.md</c>.
    /// </summary>
    /// <remarks>
    /// Live is also RESUMABLE: re-entering a live run is the resume, and there is no second property saying
    /// so. Two names for one value on a public record is surface that has to be supported forever, and this
    /// one collided with <see cref="OperationOutcome.Resumable"/>, which means something else — whether a
    /// stop can be re-entered at all, rather than whether a record is still open.
    /// </remarks>
    public bool IsLive => Status is RunStatus.Running or RunStatus.Paused;

    /// <summary>
    /// Refuse to discard this record while it is <see cref="IsLive"/> — the guard every
    /// <see cref="IRunHistory.DeleteAsync"/> owes, written once.
    /// </summary>
    /// <exception cref="InvalidOperationException">The run is running or paused.</exception>
    /// <remarks>
    /// <para>Both shipped histories wrote this themselves, and they had already drifted: one told the
    /// operator what to do about it and the other stopped at the fact, so which sentence they got depended
    /// on where their history happened to live. That is the same defect
    /// <see cref="DeploymentArtifact.RequirePart"/> was extracted to fix — and a third store, which
    /// <c>docs/DECISIONS.md</c> D20 exists to make easy, would have written a third sentence.</para>
    /// <para>A method rather than a note in the interface docs because the rule is one an implementation
    /// satisfies by OMISSION: a store that simply deletes passes every test that does not think to check,
    /// and the cost is stranded work with nothing left to say it is happening.</para>
    /// </remarks>
    public void RefuseDeleteWhileLive()
    {
        if (!IsLive) return;

        throw new InvalidOperationException(
            $"Run '{Id}' is {Status} and may still be converging in the provider. Finish or resume it " +
            "before deleting the record.");
    }
}

/// <summary>
/// Where runs are recorded. An implementation may use anything durable (SQLite, a file, a table) — the
/// engine only requires that a record written before the process dies is readable after it restarts.
/// </summary>
public interface IRunHistory
{
    /// <summary>Insert or update a run by <see cref="RunRecord.Id"/>. Called at least twice per run:
    /// once as <see cref="RunStatus.Running"/> at the start, once with the outcome.</summary>
    Task UpsertAsync(RunRecord record, CancellationToken ct = default);

    /// <summary>The live run for this procedure+prefix, if any — what makes a resume discoverable after a
    /// crash, and what a caller must check before starting new work.</summary>
    Task<RunRecord?> LiveAsync(string procedure, string prefix, CancellationToken ct = default);

    /// <summary>Recent runs, newest first.</summary>
    Task<IReadOnlyList<RunRecord>> RecentAsync(int limit = 50, CancellationToken ct = default);

    /// <summary>
    /// Delete a finished run. Implementations MUST refuse a live one
    /// (<see cref="RunRecord.IsLive"/>) — deleting it strands work that is still converging, with nothing
    /// left to tell the operator it is happening.
    /// </summary>
    Task DeleteAsync(string id, CancellationToken ct = default);
}
