namespace Tyanor;

/// <summary>
/// What a deployable unit looks like RIGHT NOW, as reported by the provider — normalized to the five
/// states every convergent system has, whatever it calls them.
///
/// <para>A provider maps its own vocabulary onto this (CloudFormation's <c>CREATE_IN_PROGRESS</c>,
/// <c>ROLLBACK_COMPLETE</c>, …; Kubernetes' rollout conditions; a container registry's tag state). That
/// mapping is the ONLY place a provider's status strings appear — see <see cref="Reconcile"/>.</para>
/// </summary>
public enum UnitPhase
{
    /// <summary>Nothing is there. The unit has never been created, or was torn down.</summary>
    Missing,

    /// <summary>Healthy and settled. Whatever is deployed matches what the provider last accepted.</summary>
    Ready,

    /// <summary>Converging right now — an operation another run (or another machine) started is still
    /// running. This is the state that makes resume possible and re-issuing dangerous.</summary>
    Converging,

    /// <summary>Rolling back. Not yet settled, and it will settle into <see cref="Broken"/>.</summary>
    Unwinding,

    /// <summary>Settled in a state the provider will not let you update — it must be removed and remade.</summary>
    Broken,
}

/// <summary>What the engine should DO about a unit in a given <see cref="UnitPhase"/>.</summary>
public enum ReconcileAction
{
    /// <summary>Nothing exists — create it.</summary>
    Create,

    /// <summary>It exists and is healthy — apply the desired configuration.</summary>
    Update,

    /// <summary>Something is ALREADY converging: watch it to completion and issue nothing.</summary>
    Attach,

    /// <summary>It is unwinding: wait for it to settle, then remove and remake it.</summary>
    SettleThenRecreate,

    /// <summary>It is settled but unusable: remove it, then create fresh.</summary>
    Recreate,
}

/// <summary>
/// THE reconcile decision — the whole of Tyanor's resume model, as one pure function.
///
/// <para><b>Why this is a function and not a workflow.</b> A run does not remember what it was doing; it
/// asks the provider what is true and decides again. That is what makes an interrupted run resumable by
/// simply re-running it: there is no separate "resume" path to keep in step with the "deploy" path, and
/// therefore no way for the two to disagree. It also means a crash, a closed laptop, or a second machine
/// starting the same procedure all converge on the same answer.</para>
///
/// <para><b>Why there is no state file.</b> The provider is already a database of what exists, and it keeps
/// converging whether or not this process is alive. Mirroring it locally buys nothing and costs the drift,
/// locking and repair problems that dominate operating a state-file tool. Tyanor records INTENT (a run
/// happened, with this configuration) and reads FACT from the provider. See
/// <c>.claude/rules/reconcile-dont-mirror.md</c>.</para>
///
/// <para>Ported from a deployer that survived a real crash-and-rebuild mid-deploy and resumed to
/// completion; every branch below is there because it happened.</para>
/// </summary>
public static class Reconcile
{
    /// <summary>Decide what to do about a unit currently in <paramref name="phase"/>.</summary>
    public static ReconcileAction Decide(UnitPhase phase) => phase switch
    {
        // Never re-issue against a converging unit. Most providers reject it outright, and the ones that
        // don't will happily start a second conflicting operation — which is worse, because it succeeds.
        UnitPhase.Converging => ReconcileAction.Attach,
        UnitPhase.Unwinding => ReconcileAction.SettleThenRecreate,
        UnitPhase.Broken => ReconcileAction.Recreate,
        UnitPhase.Ready => ReconcileAction.Update,
        UnitPhase.Missing => ReconcileAction.Create,
        _ => ReconcileAction.Create,
    };

    /// <summary>
    /// Whether this action issues a mutating call. <see cref="ReconcileAction.Attach"/> does not — it is
    /// the "someone else is already doing it" answer, and a provider adapter that mutates here has
    /// misunderstood the model.
    /// </summary>
    public static bool Mutates(ReconcileAction action) => action is not ReconcileAction.Attach;
}
