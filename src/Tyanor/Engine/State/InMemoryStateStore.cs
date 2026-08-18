namespace Tyanor.Engine.State;

/// <summary>
/// Deployment state that lives only as long as the process — the counterpart to
/// <see cref="InMemoryRunHistory"/>, and chosen for the same reasons.
///
/// <para><b>Deliberately NOT the default.</b> State answers what Tyanor OWNS, so losing it means the next
/// teardown cannot tell what Tyanor created from what was already there — which is the difference between a
/// safe destroy and a destructive one (<c>docs/DECISIONS.md</c> D12). Choosing this is choosing to give that
/// up, which is right for a test or a one-shot CI run and wrong anywhere a deployment outlives the process
/// that made it.</para>
///
/// <para>It exists because it was written by hand twice in this repository's own tests before it was written
/// once here — and a hand-rolled one is not held to <c>StateStoreContract</c>, so it is free to be subtly
/// wrong about the things the contract exists to pin, like keeping a null fingerprint null.</para>
/// </summary>
public sealed class InMemoryStateStore : IStateStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<(string Procedure, string Prefix), DeploymentState> _states = [];

    /// <inheritdoc/>
    public Task<DeploymentState> GetAsync(string procedure, string prefix, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult(_states.GetValueOrDefault((procedure, prefix))
                ?? DeploymentState.Empty(procedure, prefix));
    }

    /// <summary>Write state at the next version.</summary>
    /// <param name="state">What to persist; its serial is the version it was read at.</param>
    /// <param name="ct">Cancellation.</param>
    /// <remarks>
    /// The serial advances the same way the file store's does, so a test using this sees the same numbers a
    /// real deployment would. No conditional CHECK, also the same as the file store — a store that can do
    /// one is what D20's seam is for.
    /// </remarks>
    public Task SaveAsync(DeploymentState state, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (_gate) _states[(state.Procedure, state.Prefix)] = state with { Serial = state.Serial + 1 };
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task DeleteAsync(string procedure, string prefix, CancellationToken ct = default)
    {
        lock (_gate) _states.Remove((procedure, prefix));
        return Task.CompletedTask;
    }
}
