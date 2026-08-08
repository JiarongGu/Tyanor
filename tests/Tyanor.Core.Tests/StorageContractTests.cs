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

    public static TheoryData<string> Checks() => Names(new RunHistoryContract(() => null!));

    internal static TheoryData<string> Names(ContractSuite suite)
    {
        var data = new TheoryData<string>();
        foreach (var check in suite.Checks) data.Add(check);
        return data;
    }

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
        FileRunHistoryContractTests.Names(new StateStoreContract(() => null!));

    [Theory]
    [MemberData(nameof(Checks))]
    public Task FileStateStore_satisfies(string check) =>
        new StateStoreContract(() => new FileStateStore(_scratch.Path("state"))).AssertAsync(check);

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
