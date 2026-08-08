namespace Tyanor;

/// <summary>What the engine intends to do about one unit, and why.</summary>
/// <param name="Unit">The unit.</param>
/// <param name="Phase">What the provider reported when the plan was made.</param>
/// <param name="Action">What <see cref="Reconcile.Decide"/> chose for that phase.</param>
public sealed record PlannedStep(ProcedureUnit Unit, UnitPhase Phase, ReconcileAction Action)
{
    /// <summary>
    /// Whether this step will issue a mutating call. <see cref="ReconcileAction.Attach"/> will not — it
    /// waits on an operation someone else already started, which is a thing the operator should see in a
    /// plan precisely BECAUSE it means another run is in flight.
    /// </summary>
    public bool Mutates => Reconcile.Mutates(Action);

    /// <summary>One line an operator can read.</summary>
    public override string ToString() => Action switch
    {
        ReconcileAction.Create => $"{Unit.Label}: create (nothing there now)",
        ReconcileAction.Update => $"{Unit.Label}: apply configuration (may be a no-op)",
        ReconcileAction.Attach => $"{Unit.Label}: ALREADY RUNNING — will wait for it, change nothing",
        ReconcileAction.Recreate => $"{Unit.Label}: REPLACE — it is in a state the provider cannot update",
        ReconcileAction.SettleThenRecreate => $"{Unit.Label}: REPLACE — waiting for a rollback to finish first",
        ReconcileAction.Remove => $"{Unit.Label}: DESTROY",
        ReconcileAction.Nothing => $"{Unit.Label}: nothing to do (already gone)",
        _ => $"{Unit.Label}: {Action}",
    };
}

/// <summary>
/// What a run would do, worked out before doing it.
///
/// <para><b>A Tyanor plan is derived from the PROVIDER, not from a stored model.</b> Each step comes from
/// asking the target what phase the unit is in right now and running the same
/// <see cref="Reconcile.Decide"/> the apply will run. That makes it cheap, and it makes it honest in a way
/// a state-file plan cannot be: a plan computed from a local mirror is a claim about a world that may have
/// moved, and the difference only surfaces at apply time.</para>
///
/// <para><b>It is a forecast, not a contract.</b> Reality can move between plan and apply — that is true
/// of any such tool, and pretending otherwise is worse than saying so. Two honest limits: an
/// <see cref="ReconcileAction.Update"/> may turn out to be a no-op (only the provider knows whether the
/// configuration actually differs), and a unit that is <see cref="UnitPhase.Converging"/> now may have
/// settled by the time apply reaches it. Neither can be resolved without doing the work.</para>
/// </summary>
/// <param name="Procedure">Which procedure.</param>
/// <param name="Prefix">Which deployment of it.</param>
/// <param name="Steps">One per unit, in apply order.</param>
/// <param name="ActiveRun">
/// A run already recorded as live for this procedure and prefix — possibly started on ANOTHER MACHINE,
/// when the history is shared. This is the second half of "is anything already happening here?": the steps
/// answer it from the provider, this answers it from the record of intent, and the two can disagree in
/// ways that are worth seeing (see <see cref="HasStalledRun"/>).
/// </param>
public sealed record Plan(
    string Procedure, string Prefix, IReadOnlyList<PlannedStep> Steps, RunRecord? ActiveRun = null)
{
    /// <summary>
    /// Which direction this plan is for. A teardown gets a plan too — it is the operation that destroys
    /// things, so it is the one that most needs a gate in front of it.
    /// </summary>
    public RunKind Kind { get; init; } = RunKind.Apply;

    /// <summary>Steps that will issue a mutating call.</summary>
    public IReadOnlyList<PlannedStep> Changes => Steps.Where(s => s.Mutates).ToList();

    /// <summary>
    /// Steps that will REPLACE a unit — the ones worth a confirmation, because replacing usually means
    /// losing whatever the unit was holding.
    /// </summary>
    public IReadOnlyList<PlannedStep> Replacements =>
        Steps.Where(s => s.Action is ReconcileAction.Recreate or ReconcileAction.SettleThenRecreate).ToList();

    /// <summary>
    /// True when the provider is already converging one of these units. Worth surfacing loudly: on an apply
    /// it is safe (the engine attaches rather than conflicting), but the operator is watching someone else's
    /// deployment, and should know that before they wonder why nothing they changed took effect.
    /// </summary>
    /// <remarks>
    /// Read from the PHASE, not from the action. Asking the action was equivalent for an apply — only
    /// <see cref="UnitPhase.Converging"/> produces <see cref="ReconcileAction.Attach"/> — and wrong for a
    /// teardown, where nothing ever attaches, so a removal plan reported an idle provider however busy it
    /// was. That in turn made <see cref="HasStalledRun"/> always true and <see cref="InSync"/> always false
    /// whenever a run was live, which is the opposite of what those are for.
    /// </remarks>
    public bool HasWorkInFlight => Steps.Any(s => s.Phase is UnitPhase.Converging);

    /// <summary>
    /// A run is recorded as live but NOTHING is converging in the provider. It paused, or the process
    /// running it died. Applying now RESUMES that run rather than starting a fresh one — which is usually
    /// what the operator wants, and always something they should know before it happens.
    ///
    /// <para>This is the signal that only shared state can give, and the reason cross-machine is a
    /// capability rather than a hazard: without it, a second operator sees an idle provider and assumes
    /// nobody is here.</para>
    /// </summary>
    public bool HasStalledRun => ActiveRun is not null && !HasWorkInFlight;

    /// <summary>
    /// Whether the provider and the record of intent agree about whether work is happening. They disagree
    /// when a run is recorded live with nothing converging (it stopped), or — rarer, and worth
    /// investigating — when something is converging that no run here claims.
    /// </summary>
    public bool InSync => (ActiveRun is not null) == HasWorkInFlight;

    /// <summary>
    /// Differences between recorded state and what the provider actually has — found by refreshing during
    /// the plan. Empty means state and reality agree.
    /// </summary>
    public IReadOnlyList<Drift> Drift { get; init; } = [];

    /// <summary>
    /// Resources this run will DELIBERATELY destroy — the ones a teardown is about to take away. Empty for
    /// an apply, which destroys nothing on purpose.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Drift"/> because they answer different questions and only one of them is a
    /// surprise. Drift is "the world moved without me"; this is "I am about to remove twelve things". A
    /// teardown that reported its own intent as drift would be describing the operator's own decision as an
    /// anomaly.
    /// </remarks>
    public IReadOnlyList<Drift> Destroying { get; init; } = [];

    /// <summary>Resources that exist in the provider but not in state — created outside Tyanor, or a state
    /// that was lost. They will be adopted on the next apply.</summary>
    public int ToAdd => Drift.Count(d => d.Change == ResourceChange.Add);

    /// <summary>Resources whose fingerprint no longer matches what was recorded.</summary>
    public int ToChange => Drift.Count(d => d.Change == ResourceChange.Change);

    /// <summary>
    /// Resources that will be gone afterwards: the ones a teardown will destroy, plus the ones already
    /// deleted outside Tyanor.
    /// </summary>
    public int ToDestroy => Drift.Count(d => d.Change == ResourceChange.Destroy) + Destroying.Count;

    /// <summary>
    /// The line an operator reads before deciding: <c>3 to add, 1 to change, 0 to destroy</c>.
    /// </summary>
    /// <remarks>
    /// Counts are RESOURCES, from the refresh; the steps above are UNITS, from the reconcile. They answer
    /// different questions — "what will change in my infrastructure" versus "what will this run do" — and
    /// conflating them would make one of the two wrong.
    /// </remarks>
    public string Summary => $"{ToAdd} to add, {ToChange} to change, {ToDestroy} to destroy";

    /// <summary>
    /// True when state and the provider disagree about anything. Repairable by applying, which rewrites
    /// state from what was refreshed — the answer to a stale mirror is to re-read it, not to hand-edit it.
    /// </summary>
    public bool HasDrift => Drift.Count > 0;

    /// <summary>Nothing to do — every unit is already as asked, no drift, and no run is outstanding.</summary>
    public bool IsNoOp => Changes.Count == 0 && !HasWorkInFlight && ActiveRun is null && !HasDrift;

    /// <summary>
    /// The line to put a confirmation behind. True when this plan will take something away that exists —
    /// a teardown with anything left to destroy, or a unit the provider will not update in place.
    /// </summary>
    /// <remarks>
    /// The distinction between this and <see cref="Changes"/> is the one worth wiring into a UI: a create
    /// or an update is recoverable by running it again, and a destroy is not recoverable at all.
    /// </remarks>
    public bool IsDestructive => Destroying.Count > 0 || Replacements.Count > 0;
}
