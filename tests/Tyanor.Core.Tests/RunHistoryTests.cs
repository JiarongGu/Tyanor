using Tyanor.Engine.State;
using Xunit;

namespace Tyanor.Tests;

/// <summary>
/// Run history is the state Tyanor DOES keep — the record of intent that makes a run resumable after the
/// process dies. These tests are about exactly that: what survives, and what must not be thrown away.
/// </summary>
public sealed class FileRunHistoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tyanor-" + Guid.NewGuid().ToString("N"));

    private string StatePath => Path.Combine(_dir, "nested", "runs.json");

    private static RunRecord Run(string id, RunStatus status = RunStatus.Running) =>
        new(id, "site", "acme", RunKind.Apply, status, DateTimeOffset.UnixEpoch);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort temp cleanup */ }
    }

    [Fact]
    public async Task A_run_survives_a_new_history_over_the_same_file()
    {
        // The whole contract: a record written before the process died is readable by the next one. If
        // this fails, nothing is resumable and the engine's guarantee is a fiction.
        await new FileRunHistory(StatePath).UpsertAsync(Run("r1"));

        var reopened = await new FileRunHistory(StatePath).LiveAsync("site", "acme");

        Assert.NotNull(reopened);
        Assert.Equal("r1", reopened.Id);
        Assert.True(reopened.IsLive);
    }

    [Fact]
    public async Task The_state_location_is_created_including_missing_directories()
    {
        // The consumer picks the path; they should not also have to create it.
        await new FileRunHistory(StatePath).UpsertAsync(Run("r1"));

        Assert.True(File.Exists(StatePath));
    }

    [Fact]
    public async Task Upsert_updates_a_run_in_place_rather_than_appending_a_second()
    {
        // A resume continues one run. If each pause appended, an interrupted job would read as several
        // failures and the operator would learn to distrust the record.
        var history = new FileRunHistory(StatePath);
        await history.UpsertAsync(Run("r1"));
        await history.UpsertAsync(Run("r1", RunStatus.Succeeded) with { FinishedAt = DateTimeOffset.UnixEpoch });

        var all = await history.RecentAsync();

        Assert.Single(all);
        Assert.Equal(RunStatus.Succeeded, all[0].Status);
    }

    [Fact]
    public async Task A_finished_run_is_no_longer_live()
    {
        var history = new FileRunHistory(StatePath);
        await history.UpsertAsync(Run("r1", RunStatus.Succeeded));

        Assert.Null(await history.LiveAsync("site", "acme"));
    }

    [Fact]
    public async Task Live_is_scoped_to_the_procedure_and_prefix()
    {
        // One account hosts several deployments; a live run of one must not block or resume another.
        var history = new FileRunHistory(StatePath);
        await history.UpsertAsync(Run("r1"));

        Assert.Null(await history.LiveAsync("site", "other-prefix"));
        Assert.Null(await history.LiveAsync("other-procedure", "acme"));
        Assert.NotNull(await history.LiveAsync("site", "acme"));
    }

    [Theory]
    [InlineData(RunStatus.Running)]
    [InlineData(RunStatus.Paused)]
    public async Task Deleting_a_live_run_is_refused(RunStatus status)
    {
        // It is the operator's only handle on work that may STILL be converging in the provider. Deleting
        // it strands that work with nothing left to say it is happening.
        var history = new FileRunHistory(StatePath);
        await history.UpsertAsync(Run("r1", status));

        await Assert.ThrowsAsync<InvalidOperationException>(() => history.DeleteAsync("r1"));
        Assert.NotNull(await history.LiveAsync("site", "acme"));
    }

    [Fact]
    public async Task Deleting_a_finished_run_works_and_is_idempotent()
    {
        var history = new FileRunHistory(StatePath);
        await history.UpsertAsync(Run("r1", RunStatus.Failed));

        await history.DeleteAsync("r1");
        await history.DeleteAsync("r1");                       // already gone — not an error

        Assert.Empty(await history.RecentAsync());
    }

    [Fact]
    public async Task A_pause_reason_round_trips_through_the_file()
    {
        // The reason is what the UI routes on — losing it turns "re-enter your credentials" into
        // "something went wrong".
        var history = new FileRunHistory(StatePath);
        await history.UpsertAsync(Run("r1", RunStatus.Paused) with { Reason = PauseReason.Credentials, Error = "expired" });

        var reopened = (await new FileRunHistory(StatePath).RecentAsync())[0];

        Assert.Equal("credentials", reopened.Reason?.Value);
        Assert.Equal("expired", reopened.Error);
    }

    [Fact]
    public async Task A_corrupt_file_is_reported_rather_than_silently_replaced()
    {
        // It may be the only record of something still running, so overwriting it is the one thing that
        // must not happen quietly.
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        await File.WriteAllTextAsync(StatePath, "{ not json");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => new FileRunHistory(StatePath).RecentAsync());

        Assert.Contains(StatePath, ex.Message);
    }

    [Fact]
    public async Task An_empty_history_reads_as_empty_rather_than_failing()
    {
        Assert.Empty(await new FileRunHistory(StatePath).RecentAsync());
        Assert.Null(await new FileRunHistory(StatePath).LiveAsync("site", "acme"));
    }

    [Theory]
    [InlineData("Unheard-Of")]
    [InlineData("9")]
    [InlineData("")]
    public async Task A_status_this_version_cannot_read_is_treated_as_LIVE(string status)
    {
        // The safe error is to PROTECT a record we cannot classify, not to let it be deleted — it may be the
        // only handle on work still converging.
        //
        // "9" is the one that was actually broken: Enum.TryParse happily accepts a NUMBER, so a hand-edited
        // status parsed to an undefined RunStatus that was neither Running nor Paused, read as not live, and
        // became deletable. The fallback has to hold for a hand-edited file, which is the case it is for.
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        await File.WriteAllTextAsync(StatePath,
            $$"""
            [{"Id":"r1","Procedure":"site","Prefix":"acme","Kind":"Apply","Status":"{{status}}",
              "StartedAt":"2026-08-18T00:00:00+00:00"}]
            """);

        var history = new FileRunHistory(StatePath);

        Assert.NotNull(await history.LiveAsync("site", "acme"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => history.DeleteAsync("r1"));
    }

    [Fact]
    public async Task A_kind_this_version_cannot_read_falls_back_to_Apply()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        await File.WriteAllTextAsync(StatePath,
            """
            [{"Id":"r1","Procedure":"site","Prefix":"acme","Kind":"7","Status":"Succeeded",
              "StartedAt":"2026-08-18T00:00:00+00:00"}]
            """);

        Assert.Equal(RunKind.Apply, (await new FileRunHistory(StatePath).RecentAsync())[0].Kind);
    }
}

/// <summary>The in-memory history keeps the same guards; only durability differs.</summary>
public class InMemoryRunHistoryTests
{
    [Fact]
    public async Task It_refuses_to_delete_a_live_run_too()
    {
        var history = new InMemoryRunHistory();
        await history.UpsertAsync(new RunRecord("r1", "site", "acme", RunKind.Apply, RunStatus.Running, DateTimeOffset.UnixEpoch));

        await Assert.ThrowsAsync<InvalidOperationException>(() => history.DeleteAsync("r1"));
    }

    [Fact]
    public async Task It_finds_the_live_run_for_a_procedure_and_prefix()
    {
        var history = new InMemoryRunHistory();
        await history.UpsertAsync(new RunRecord("r1", "site", "acme", RunKind.Apply, RunStatus.Paused, DateTimeOffset.UnixEpoch));

        Assert.Equal("r1", (await history.LiveAsync("site", "acme"))?.Id);
    }
}
