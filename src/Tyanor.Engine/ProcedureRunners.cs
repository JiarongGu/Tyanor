namespace Tyanor.Engine;

/// <summary>
/// Builds a <see cref="ProcedureRunner"/> for a chosen target, over one shared history and state store.
///
/// <para>A runner is bound to one target, because reconcile reads phases from that target and nothing else.
/// So an application with more than one provider needs a runner per provider — and this is the one line
/// that produces it, rather than every consumer reconstructing the same four arguments.</para>
///
/// <para>Plain class, no container required. <c>AddTyanor</c> registers it for applications that use DI,
/// and applications that do not construct it directly — the library does not decide which
/// (<c>docs/DECISIONS.md</c> D10).</para>
/// </summary>
/// <param name="targets">The targets this application can deploy to.</param>
/// <param name="history">Where runs are recorded. Shared across targets on purpose: a deployment's history
/// is the operator's, not the provider's.</param>
/// <param name="state">Where deployment state lives. Optional; without it the engine still reconciles and
/// resumes, but cannot say what it owns.</param>
/// <param name="retry">How transient provider errors are retried.</param>
public sealed class ProcedureRunners(
    DeploymentTargets targets, IRunHistory history, IStateStore? state = null, RetryPolicy? retry = null)
{
    /// <summary>The targets available, so a caller can offer the choice before making it.</summary>
    public DeploymentTargets Targets { get; } = targets;

    /// <summary>A runner for the target with this id.</summary>
    /// <param name="targetId">The provider id — <c>"aws"</c>, <c>"local"</c>.</param>
    /// <exception cref="ArgumentException">No target has that id.</exception>
    public ProcedureRunner For(string targetId) => new(Targets.Get(targetId), history, state, retry);

    /// <summary>A runner for a target held directly, registered or not.</summary>
    /// <param name="target">The target.</param>
    public ProcedureRunner For(IDeploymentTarget target) => new(target, history, state, retry);

    /// <summary>
    /// A runner for the only target, when exactly one is registered — the ordinary case, and the one the
    /// minimal path in the README describes.
    /// </summary>
    /// <exception cref="InvalidOperationException">There is not exactly one target.</exception>
    public ProcedureRunner ForSingle() => new(Targets.Single(), history, state, retry);
}
