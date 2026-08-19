using Tyanor.Engine;
using Tyanor.Engine.State;
using Tyanor.Testing;
using Xunit;

namespace Tyanor.Tests;

/// <summary>
/// Cancelling a run, which is a documented guarantee and had no engine test at all.
///
/// <para>The promise is that cancellation leaves the run LIVE: whatever the provider started is still
/// converging out there, so marking it failed would hide work genuinely in flight, and the operator's handle
/// on that work is the record. Every part of that is about what gets WRITTEN, which is exactly the part
/// that was broken.</para>
/// </summary>
public sealed class CancellationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tyanor-cancel-" + Guid.NewGuid().ToString("N")[..8]);

    private static readonly Procedure Site = new("site",
        [new ProcedureUnit("db", "Database"), new ProcedureUnit("api", "API"), new ProcedureUnit("web", "Website")]);

    private static DeploymentRequest Request() => Requests.Bare();

    /// <summary>A REAL file history, because the in-memory one ignores the token and hid this.</summary>
    private FileRunHistory History() => new(Path.Combine(_dir, "runs.json"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* temp */ }
    }

    /// <summary>Cancel as soon as the first unit finishes, so the run stops partway with work done.</summary>
    private static Action<ProgressReport> CancelAfterFirstUnit(CancellationTokenSource cts)
    {
        var cancelled = false;
        return report =>
        {
            if (cancelled || !report.Message.EndsWith("done.", StringComparison.Ordinal)) return;
            cancelled = true;
            cts.Cancel();
        };
    }

    [Fact]
    public async Task Cancelling_records_the_run_as_PAUSED_and_says_it_was_external()
    {
        // The defect this was written for. The engine wrote the ending with the token that had JUST been
        // cancelled, so a history honouring it — the shipped file one does — threw instead of writing. The
        // run stayed recorded as Running with no reason, and PauseReason.External never reached a record at
        // all. Being told to stop is not a reason to stop saying why you stopped.
        var history = History();
        var target = new MemoryTarget();
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ProcedureRunner(target, history)
                .ApplyAsync(Site, Request(), CancelAfterFirstUnit(cts), ct: cts.Token));

        var run = Assert.Single(await history.RecentAsync());
        Assert.Equal(RunStatus.Paused, run.Status);
        Assert.Equal("external", run.Reason?.Value);
    }

    [Fact]
    public async Task A_cancelled_run_stays_LIVE_so_the_work_in_flight_still_has_a_handle()
    {
        var history = History();
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ProcedureRunner(new MemoryTarget(), history)
                .ApplyAsync(Site, Request(), CancelAfterFirstUnit(cts), ct: cts.Token));

        var run = Assert.Single(await history.RecentAsync());
        Assert.True(run.IsLive);
        Assert.Null(run.FinishedAt);                                    // it has not finished; it stopped
        Assert.NotNull(await history.LiveAsync("site", "acme"));        // …and a resume can find it
    }

    [Fact]
    public async Task What_was_deployed_before_the_cancel_stays_deployed_and_recorded()
    {
        var history = History();
        var state = new FileStateStore(Path.Combine(_dir, "state.json"));
        var target = new MemoryTarget();
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ProcedureRunner(target, history, state)
                .ApplyAsync(Site, Request(), CancelAfterFirstUnit(cts), ct: cts.Token));

        Assert.Equal(["db"], target.Deployed);
        Assert.Equal(["db"], (await state.GetAsync("site", "acme")).RecordedUnits);
    }

    [Fact]
    public async Task Applying_again_CONTINUES_the_cancelled_run_rather_than_opening_a_second()
    {
        // The whole point of leaving it live: a cancel is a pause, and a pause is resumed by re-running.
        var history = History();
        var target = new MemoryTarget();
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ProcedureRunner(target, history)
                .ApplyAsync(Site, Request(), CancelAfterFirstUnit(cts), ct: cts.Token));

        var cancelledId = (await history.RecentAsync())[0].Id;

        Assert.True((await new ProcedureRunner(target, history).ApplyAsync(Site, Request())).Ok);

        var run = Assert.Single(await history.RecentAsync());
        Assert.Equal(cancelledId, run.Id);                              // the SAME run, continued
        Assert.Equal(RunStatus.Succeeded, run.Status);
        Assert.Equal(["db", "api", "web"], target.Deployed);
    }

    [Fact]
    public async Task A_cancelled_TEARDOWN_is_recorded_the_same_way()
    {
        var history = History();
        var target = new MemoryTarget().AlreadyDeployed("db", "api", "web");
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ProcedureRunner(target, history)
                .DestroyAsync(Site, Request(), CancelAfterFirstUnit(cts), ct: cts.Token));

        var run = Assert.Single(await history.RecentAsync());
        Assert.Equal(RunKind.Destroy, run.Kind);
        Assert.Equal(RunStatus.Paused, run.Status);
        Assert.True(run.IsLive);
    }

    [Fact]
    public async Task A_token_cancelled_BEFORE_the_run_starts_leaves_no_trace_at_all()
    {
        // Nothing happened, so nothing is recorded — and the point is that this is DETERMINISTIC. Without
        // the check at the top of the run it depended on which side of the opening history write the
        // cancellation landed: a moment earlier and the write threw and recorded nothing, a moment later
        // and the history held a paused run that had touched nothing and that the next apply would adopt.
        var history = History();
        var target = new MemoryTarget();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ProcedureRunner(target, history).ApplyAsync(Site, Request(), ct: cts.Token));

        Assert.Empty(await history.RecentAsync());
        Assert.Empty(target.Deployed);
        Assert.Null(await history.LiveAsync("site", "acme"));   // nothing for a later run to adopt
    }

    [Fact]
    public async Task It_leaves_no_trace_even_when_the_HISTORY_ignores_the_token()
    {
        // The same guarantee, against a store that does not honour cancellation — and this is the version
        // that actually pins the up-front check. With the file history the opening write throws on its own,
        // so the behaviour looks right whether or not the engine checks first. With a store that ignores
        // the token, skipping the check writes a Running record, the loop then throws, and the history ends
        // up holding a paused run that touched nothing and that the next apply would adopt.
        //
        // A guarantee that only holds for one implementation of a seam is not a guarantee.
        var history = new InMemoryRunHistory();
        var target = new MemoryTarget();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ProcedureRunner(target, history).ApplyAsync(Site, Request(), ct: cts.Token));

        Assert.Empty(await history.RecentAsync());
        Assert.Empty(target.Deployed);
    }
}
