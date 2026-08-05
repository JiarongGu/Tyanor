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
public sealed record Plan(string Procedure, string Prefix, IReadOnlyList<PlannedStep> Steps)
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

    /// <summary>Nothing to do — every unit is already as asked.</summary>
    public bool IsNoOp => Changes.Count == 0 && !HasWorkInFlight;
}
