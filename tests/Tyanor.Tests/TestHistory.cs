namespace Tyanor.Tests;

/// <summary>Run history that records every upsert, so a test can assert on the sequence and not just the end.</summary>
internal sealed class TestHistory : IRunHistory
{
    /// <summary>Every record written, in order — including the ones later superseded by the same id.</summary>
    public List<RunRecord> All { get; } = [];

    /// <summary>The most recent write.</summary>
    public RunRecord? Last => All.LastOrDefault();

    /// <summary>What <see cref="LiveAsync"/> answers. Null unless a test is exercising adoption.</summary>
    public RunRecord? Live { get; set; }

    public Task UpsertAsync(RunRecord record, CancellationToken ct = default)
    {
        All.Add(record);
        return Task.CompletedTask;
    }

    public Task<RunRecord?> LiveAsync(string procedure, string prefix, CancellationToken ct = default) =>
        Task.FromResult(Live);

    public Task<IReadOnlyList<RunRecord>> RecentAsync(int limit = 50, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<RunRecord>>(All);

    public Task DeleteAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
}
