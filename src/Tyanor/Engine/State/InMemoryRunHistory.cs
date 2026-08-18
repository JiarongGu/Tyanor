namespace Tyanor.Engine.State;

/// <summary>
/// Run history that lives only as long as the process.
///
/// <para><b>Deliberately NOT the default.</b> A run recorded here cannot be resumed after a crash, which
/// is the one thing run history exists for — so choosing this is choosing to give that up. It is right for
/// tests, for a dry run, and for a one-shot procedure in CI where the process outliving the run would be a
/// surprise. It is wrong for anything an operator would expect to re-enter.</para>
///
/// <para>Choosing it is explicit — `UseInMemoryState()`, never a default — because "my deployment vanished
/// when the machine slept" is a bad way to discover which history you configured.</para>
/// </summary>
public sealed class InMemoryRunHistory : IRunHistory
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, RunRecord> _runs = [];
    private readonly List<string> _order = [];

    /// <inheritdoc/>
    public Task UpsertAsync(RunRecord record, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_runs.ContainsKey(record.Id)) _order.Add(record.Id);
            _runs[record.Id] = record;
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<RunRecord?> LiveAsync(string procedure, string prefix, CancellationToken ct = default)
    {
        lock (_gate)
        {
            for (var i = _order.Count - 1; i >= 0; i--)
            {
                var r = _runs[_order[i]];
                if (r.Procedure == procedure && r.Prefix == prefix && r.IsLive) return Task.FromResult<RunRecord?>(r);
            }
            return Task.FromResult<RunRecord?>(null);
        }
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<RunRecord>> RecentAsync(int limit = 50, CancellationToken ct = default)
    {
        lock (_gate)
        {
            IReadOnlyList<RunRecord> recent = _order.AsEnumerable().Reverse().Take(limit).Select(id => _runs[id]).ToList();
            return Task.FromResult(recent);
        }
    }

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">The run is still live — same guard as every history.</exception>
    public Task DeleteAsync(string id, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_runs.TryGetValue(id, out var r)) return Task.CompletedTask;
            if (r.IsLive)
                throw new InvalidOperationException(
                    $"Run '{id}' is {r.Status} and may still be converging in the provider.");
            _runs.Remove(id);
            _order.Remove(id);
        }
        return Task.CompletedTask;
    }
}
