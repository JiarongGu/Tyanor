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
    /// <summary>Steps that will issue a mutating call.</summary>
    public IReadOnlyList<PlannedStep> Changes => Steps.Where(s => s.Mutates).ToList();

    /// <summary>
    /// Steps that will REPLACE a unit — the ones worth a confirmation, because replacing usually means
    /// losing whatever the unit was holding.
    /// </summary>
    public IReadOnlyList<PlannedStep> Replacements =>
        Steps.Where(s => s.Action is ReconcileAction.Recreate or ReconcileAction.SettleThenRecreate).ToList();

    /// <summary>
    /// True when another run is already converging one of these units. Worth surfacing loudly: applying
    /// now is safe (the engine attaches rather than conflicting), but the operator is watching someone
    /// else's deployment, and should know that before they wonder why nothing they changed took effect.
    /// </summary>
    public bool HasWorkInFlight => Steps.Any(s => s.Action is ReconcileAction.Attach);

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

    /// <summary>Nothing to do — every unit is already as asked, and no run is outstanding.</summary>
    public bool IsNoOp => Changes.Count == 0 && !HasWorkInFlight && ActiveRun is null;
}
