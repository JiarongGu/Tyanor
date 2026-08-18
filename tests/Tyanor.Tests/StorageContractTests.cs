using Tyanor.Engine.State;
using Tyanor.Testing;
using Xunit;

namespace Tyanor.Tests;

/// <summary>
/// The shipped storage backends, held to the same contract every other one will be.
///
/// <para>These are the suites' first customer, and running them here is how the suites stay honest: a
/// contract nobody's own implementation has to satisfy drifts into describing something nobody built.</para>
/// </summary>
public class FileRunHistoryContractTests : IDisposable
{
    private readonly Scratch _scratch = new();

    public static TheoryData<string> Checks() => Suites.Names(new RunHistoryContract(() => null!));

    [Theory]
    [MemberData(nameof(Checks))]
    public Task FileRunHistory_satisfies(string check) =>
        new RunHistoryContract(() => new FileRunHistory(_scratch.Path("runs"))).AssertAsync(check);

    [Theory]
    [MemberData(nameof(Checks))]
    public Task InMemoryRunHistory_satisfies(string check) =>
        new RunHistoryContract(() => new InMemoryRunHistory()).AssertAsync(check);

    public void Dispose() => _scratch.Dispose();
}

/// <summary>The shipped state store, held to the contract every other one will be.</summary>
public class FileStateStoreContractTests : IDisposable
{
    private readonly Scratch _scratch = new();

    public static TheoryData<string> Checks() =>
        Suites.Names(new StateStoreContract(() => null!));

    [Theory]
    [MemberData(nameof(Checks))]
    public Task FileStateStore_satisfies(string check) =>
        new StateStoreContract(() => new FileStateStore(_scratch.Path("state"))).AssertAsync(check);

    [Theory]
    [MemberData(nameof(Checks))]
    public Task InMemoryStateStore_satisfies(string check) =>
        // Held to the same contract as the durable one. Only DURABILITY differs — a store that also quietly
        // differed about keeping a null fingerprint null would make every test using it agree with a bug.
        new StateStoreContract(() => new InMemoryStateStore()).AssertAsync(check);

    public void Dispose() => _scratch.Dispose();
}

/// <summary>A temp directory that hands out a fresh file path each time and takes them all away after.</summary>
internal sealed class Scratch : IDisposable
{
    private readonly string _root = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "tyanor-contract", Guid.NewGuid().ToString("N")[..12]);

    private int _next;

    /// <summary>A path nothing has used yet — the contract requires each store to start empty.</summary>
    public string Path(string name)
    {
        Directory.CreateDirectory(_root);
        return System.IO.Path.Combine(_root, $"{name}-{Interlocked.Increment(ref _next)}.json");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* temp */ }
    }
}

/// <summary>
/// The state file's own format, versioned the way the run log already was.
///
/// <para>State is the file that answers "what does Tyanor own?", and therefore what a teardown may remove.
/// It was serialized straight from the domain record with no version marker, while the run LOG — cheaper to
/// lose by a wide margin — had a DTO and a comment explaining why. So the more valuable file was the one
/// with nothing between a property rename and an unreadable deployment.</para>
/// </summary>
public sealed class FileStateStoreFormatTests : IDisposable
{
    private readonly Scratch _scratch = new();

    [Fact]
    public async Task What_is_written_carries_a_schema_version()
    {
        var path = _scratch.Path("versioned");
        await new FileStateStore(path).SaveAsync(DeploymentState.Empty("site", "acme")
            .With("db", [new ResourceState("db-1", "T", "v1")]));

        Assert.Contains("\"Version\": 1", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task A_file_written_before_versioning_existed_still_reads()
    {
        // Version 0 is "no version field", which is what 0.1.0's own early files look like. Refusing those
        // would make the safeguard itself the thing that loses state.
        var path = _scratch.Path("unversioned");
        await File.WriteAllTextAsync(path,
            """
            [{"Procedure":"site","Prefix":"acme","Serial":3,"UpdatedAt":"2026-08-18T00:00:00+00:00",
              "Units":[{"Unit":"db","RecordedAt":"2026-08-18T00:00:00+00:00",
                        "Resources":[{"Id":"db-1","Type":"T","Fingerprint":"v1"}]}]}]
            """);

        var state = await new FileStateStore(path).GetAsync("site", "acme");

        Assert.Equal("db-1", Assert.Single(state.For("db")).Id);
        Assert.Equal(3, state.Serial);
    }

    [Fact]
    public async Task A_file_written_by_a_NEWER_Tyanor_is_refused_rather_than_half_read()
    {
        // Reading it with this version's assumptions would produce a state that looks fine and is wrong
        // about what Tyanor owns — and the first thing to notice would be a teardown.
        var path = _scratch.Path("from-the-future");
        await File.WriteAllTextAsync(path,
            """
            [{"Version":99,"Procedure":"site","Prefix":"acme","Serial":1,
              "UpdatedAt":"2026-08-18T00:00:00+00:00","Units":[]}]
            """);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new FileStateStore(path).GetAsync("site", "acme"));

        Assert.Contains("99", thrown.Message);
        Assert.Contains("upgrade", thrown.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_serial_a_caller_reads_is_the_one_it_hands_back()
    {
        // The round trip a conditional-write backend needs: read at N, save, the store persists N+1.
        var store = new FileStateStore(_scratch.Path("serials"));
        await store.SaveAsync(DeploymentState.Empty("site", "acme")
            .With("db", [new ResourceState("db-1", "T", "v1")]));

        var first = await store.GetAsync("site", "acme");
        await store.SaveAsync(first.With("db", [new ResourceState("db-1", "T", "v2")]));
        var second = await store.GetAsync("site", "acme");

        Assert.Equal(1, first.Serial);
        Assert.Equal(2, second.Serial);
    }

    public void Dispose() => _scratch.Dispose();
}

/// <summary>
/// Two stores on ONE file, in ONE process — which the documentation invites by showing stores constructed
/// wherever they are needed, and which the per-instance lock did not cover.
/// </summary>
public sealed class OneFileTwoStoresTests : IDisposable
{
    private readonly Scratch _scratch = new();

    [Fact]
    public async Task Concurrent_saves_of_DIFFERENT_deployments_do_not_clobber_each_other()
    {
        // One file holds every deployment, and a save rewrites the whole file. So two instances saving two
        // different prefixes at once each read the file, each add their own, and one deployment vanished
        // entirely — not a stale number, the whole record of what Tyanor owns for that prefix.
        //
        // D11 and D12 accept last-writer-wins between MACHINES: a distributed trade, stated and bounded.
        // This was one process losing its own writes because of how somebody happened to construct an
        // object, which is a footgun rather than a trade.
        var path = _scratch.Path("shared");
        var prefixes = Enumerable.Range(0, 24).Select(i => $"acme{i}").ToArray();

        await Task.WhenAll(prefixes.Select(prefix =>
            // A fresh store per writer, exactly as a consumer resolving one per request would get.
            new FileStateStore(path).SaveAsync(DeploymentState.Empty("site", prefix)
                .With("db", [new ResourceState($"{prefix}-db", "T", "v1")]))));

        var reader = new FileStateStore(path);
        foreach (var prefix in prefixes)
            Assert.Equal($"{prefix}-db", Assert.Single((await reader.GetAsync("site", prefix)).For("db")).Id);
    }

    [Fact]
    public async Task Concurrent_run_records_through_DIFFERENT_histories_do_not_lose_one()
    {
        var path = _scratch.Path("shared-runs");
        var ids = Enumerable.Range(0, 24).Select(i => $"r{i}").ToArray();

        await Task.WhenAll(ids.Select(id => new FileRunHistory(path).UpsertAsync(
            new RunRecord(id, "site", "acme", RunKind.Apply, RunStatus.Succeeded, DateTimeOffset.UnixEpoch))));

        var recorded = (await new FileRunHistory(path).RecentAsync(100)).Select(r => r.Id).Order().ToList();
        Assert.Equal(ids.Order(), recorded);
    }

    [Fact]
    public async Task A_torn_write_never_leaves_the_file_unreadable()
    {
        // The other thing the gate buys: every write goes through a temp file and an atomic replace, so a
        // reader racing twenty writers sees one whole document or another, never half of one.
        var path = _scratch.Path("interleaved");
        var writers = Task.WhenAll(Enumerable.Range(0, 40).Select(i =>
            new FileStateStore(path).SaveAsync(DeploymentState.Empty("site", $"p{i}")
                .With("db", [new ResourceState($"db{i}", "T", "v1")]))));

        var reader = new FileStateStore(path);
        while (!writers.IsCompleted) await reader.GetAsync("site", "p0");   // must never throw

        await writers;
    }

    public void Dispose() => _scratch.Dispose();
}
