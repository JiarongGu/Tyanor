namespace Tyanor.Testing;

/// <summary>
/// What every <see cref="IRunHistory"/> must do, whatever it stores runs in.
///
/// <para>The guarantee the engine rests on is that a record written before the process dies is readable
/// after it restarts, and that a LIVE record cannot be deleted — because it is the operator's only handle on
/// work that may still be converging in the provider. Both are easy to implement almost-correctly.</para>
/// </summary>
/// <param name="create">Makes a FRESH, empty history each time it is called. Checks must not see each
/// other's records.</param>
public sealed class RunHistoryContract(Func<IRunHistory> create) : ContractSuite
{
    /// <inheritdoc/>
    public override string Subject => "IRunHistory";

    private static RunRecord Run(string id, RunStatus status = RunStatus.Running, string prefix = "acme") =>
        new(id, "site", prefix, RunKind.Apply, status, DateTimeOffset.UtcNow.AddMinutes(-5));

    /// <inheritdoc/>
    protected override IReadOnlyList<(string Name, Func<CancellationToken, Task<string?>> Run)> Cases =>
    [
        ("A running record is found as live", async ct =>
        {
            var history = create();
            await history.UpsertAsync(Run("r1"), ct);
            var live = await history.LiveAsync("site", "acme", ct);
            return live?.Id == "r1" ? null : $"expected r1, got {live?.Id ?? "nothing"}";
        }),

        ("A paused record is still live", async ct =>
        {
            // A pause is resumable, so it is exactly the record a resume has to find.
            var history = create();
            await history.UpsertAsync(Run("r1", RunStatus.Paused), ct);
            return await history.LiveAsync("site", "acme", ct) is not null ? null : "a paused run was not live";
        }),

        ("A finished record is not live", async ct =>
        {
            var history = create();
            await history.UpsertAsync(Run("r1", RunStatus.Succeeded), ct);
            await history.UpsertAsync(Run("r2", RunStatus.Failed), ct);
            var live = await history.LiveAsync("site", "acme", ct);
            return live is null ? null : $"{live.Id} ({live.Status}) was reported live";
        }),

        ("Live is scoped to one procedure and prefix", async ct =>
        {
            // Otherwise one deployment's run blocks or resumes another's, which is a wrong deployment
            // produced by a lookup.
            var history = create();
            await history.UpsertAsync(Run("r1", prefix: "acme"), ct);

            if (await history.LiveAsync("site", "other", ct) is { } wrongPrefix)
                return $"prefix 'other' matched a run recorded under 'acme' ({wrongPrefix.Id})";
            if (await history.LiveAsync("other", "acme", ct) is { } wrongProcedure)
                return $"procedure 'other' matched a run recorded under 'site' ({wrongProcedure.Id})";
            return null;
        }),

        ("Upserting the same id updates rather than duplicates", async ct =>
        {
            // A resume continues a run. Appending instead would show one interrupted job as five failures,
            // and an operator who cannot trust the history stops reading it.
            var history = create();
            await history.UpsertAsync(Run("r1"), ct);
            await history.UpsertAsync(Run("r1", RunStatus.Succeeded), ct);

            var all = (await history.RecentAsync(50, ct)).Where(r => r.Id == "r1").ToList();
            if (all.Count != 1) return $"{all.Count} records with id r1; expected 1";
            return all[0].Status == RunStatus.Succeeded ? null : $"status stayed {all[0].Status}";
        }),

        ("Deleting a live record is refused", async ct =>
        {
            // Deleting it strands work that may still be converging, with nothing left to tell the operator
            // it is happening.
            var history = create();
            await history.UpsertAsync(Run("r1"), ct);
            try
            {
                await history.DeleteAsync("r1", ct);
                return "a live record was deleted; it must throw instead";
            }
            catch (OperationCanceledException) { throw; }
            catch { return null; }
        }),

        ("A finished record can be deleted", async ct =>
        {
            var history = create();
            await history.UpsertAsync(Run("r1", RunStatus.Succeeded), ct);
            await history.DeleteAsync("r1", ct);
            return (await history.RecentAsync(50, ct)).Any(r => r.Id == "r1") ? "it survived the delete" : null;
        }),

        ("Deleting one that is not there does not throw", async ct =>
        {
            var history = create();
            await history.DeleteAsync("never-existed", ct);
            return null;
        }),

        ("Recent returns newest first", async ct =>
        {
            var history = create();
            var now = DateTimeOffset.UtcNow;
            await history.UpsertAsync(Run("old", RunStatus.Succeeded) with { StartedAt = now.AddHours(-2) }, ct);
            await history.UpsertAsync(Run("new", RunStatus.Succeeded) with { StartedAt = now }, ct);

            var recent = (await history.RecentAsync(50, ct)).Select(r => r.Id).ToList();
            return recent.FirstOrDefault() == "new" ? null : $"got {string.Join(", ", recent)}";
        }),

        ("Recent honours its limit", async ct =>
        {
            var history = create();
            for (var i = 0; i < 5; i++)
                await history.UpsertAsync(Run($"r{i}", RunStatus.Succeeded), ct);

            var count = (await history.RecentAsync(2, ct)).Count;
            return count <= 2 ? null : $"asked for 2, got {count}";
        }),

        ("A record survives a round trip intact", async ct =>
        {
            // The fields a pause is made of. A backend that drops Reason turns a resumable stop into an
            // unexplained one, and the operator loses the sentence telling them what to do.
            var history = create();
            var written = Run("r1", RunStatus.Paused) with
            {
                Reason = PauseReason.Credentials,
                Error = "the provider rejected these credentials",
                FinishedAt = null,
            };
            await history.UpsertAsync(written, ct);

            var read = await history.LiveAsync("site", "acme", ct);
            if (read is null) return "the record was not found";
            if (read.Reason?.Value != "credentials") return $"Reason came back as {read.Reason?.Value ?? "null"}";
            if (read.Error != written.Error) return $"Error came back as {read.Error ?? "null"}";
            if (read.Kind != RunKind.Apply) return $"Kind came back as {read.Kind}";
            return null;
        }),
    ];
}

/// <summary>
/// What every <see cref="IStateStore"/> must do, whatever it keeps state in.
///
/// <para>State answers what Tyanor OWNS, so a teardown removes what it created and not what was already
/// there. A backend that loses a field here does not fail loudly — it produces a plan with the wrong
/// numbers, which is worse.</para>
/// </summary>
/// <param name="create">Makes a FRESH, empty store each time it is called.</param>
public sealed class StateStoreContract(Func<IStateStore> create) : ContractSuite
{
    /// <inheritdoc/>
    public override string Subject => "IStateStore";

    /// <inheritdoc/>
    protected override IReadOnlyList<(string Name, Func<CancellationToken, Task<string?>> Run)> Cases =>
    [
        ("Nothing stored reads as empty, never null", async ct =>
        {
            // "No state" and "an empty deployment" are the same thing to a plan. Returning null would invite
            // a caller's null check that quietly means "assume nothing exists".
            var state = await create().GetAsync("site", "acme", ct);
            if (state is null) return "GetAsync returned null";
            return state.Units.Count == 0 ? null : $"a fresh store reported {state.Units.Count} units";
        }),

        ("Saved state comes back", async ct =>
        {
            var store = create();
            var written = DeploymentState.Empty("site", "acme")
                .With("db", [new ResourceState("db-1", "AWS::RDS::DBInstance", "v1")]);
            await store.SaveAsync(written, ct);

            var read = await store.GetAsync("site", "acme", ct);
            var resource = read.For("db").FirstOrDefault();
            if (resource is null) return "the unit came back with no resources";
            if (resource.Id != "db-1") return $"Id came back as {resource.Id}";
            if (resource.Type != "AWS::RDS::DBInstance") return $"Type came back as {resource.Type}";
            if (resource.Fingerprint != "v1") return $"Fingerprint came back as {resource.Fingerprint ?? "null"}";
            return null;
        }),

        ("A null fingerprint stays null", async ct =>
        {
            // The check most likely to be failed by a backend that seems fine. Null means "the provider
            // cannot tell whether this changed", and StateDiff deliberately reports that as a CHANGE. A store
            // that helpfully turns it into an empty string silently converts "unknown" into "unchanged", and
            // the drift it was meant to surface disappears.
            var store = create();
            await store.SaveAsync(DeploymentState.Empty("site", "acme")
                .With("web", [new ResourceState("web-1", "AWS::S3::Bucket")]), ct);

            var fingerprint = (await store.GetAsync("site", "acme", ct)).For("web").FirstOrDefault()?.Fingerprint;
            return fingerprint is null ? null : $"a null fingerprint came back as '{fingerprint}'";
        }),

        ("The serial survives", async ct =>
        {
            // It is how a store with conditional writes refuses to clobber state someone else replaced.
            var store = create();
            var written = DeploymentState.Empty("site", "acme").With("db", [new ResourceState("db-1", "T", "v1")]);
            await store.SaveAsync(written, ct);

            var serial = (await store.GetAsync("site", "acme", ct)).Serial;
            return serial == written.Serial ? null : $"wrote serial {written.Serial}, read {serial}";
        }),

        ("Saving replaces rather than merges", async ct =>
        {
            var store = create();
            await store.SaveAsync(DeploymentState.Empty("site", "acme")
                .With("db", [new ResourceState("db-1", "T", "v1")]), ct);
            await store.SaveAsync(DeploymentState.Empty("site", "acme")
                .With("db", [new ResourceState("db-2", "T", "v1")]), ct);

            var ids = (await store.GetAsync("site", "acme", ct)).For("db").Select(r => r.Id).ToList();
            return ids is ["db-2"] ? null : $"got {string.Join(", ", ids)}";
        }),

        ("State is scoped to one procedure and prefix", async ct =>
        {
            // One machine hosting two deployments of the same procedure is the whole reason Prefix exists.
            var store = create();
            await store.SaveAsync(DeploymentState.Empty("site", "acme")
                .With("db", [new ResourceState("db-1", "T", "v1")]), ct);

            if ((await store.GetAsync("site", "other", ct)).Units.Count != 0) return "prefix 'other' saw acme's state";
            if ((await store.GetAsync("other", "acme", ct)).Units.Count != 0) return "procedure 'other' saw site's state";
            return null;
        }),

        ("Two deployments coexist", async ct =>
        {
            var store = create();
            await store.SaveAsync(DeploymentState.Empty("site", "one").With("db", [new ResourceState("a", "T", "v")]), ct);
            await store.SaveAsync(DeploymentState.Empty("site", "two").With("db", [new ResourceState("b", "T", "v")]), ct);

            var one = (await store.GetAsync("site", "one", ct)).For("db").FirstOrDefault()?.Id;
            var two = (await store.GetAsync("site", "two", ct)).For("db").FirstOrDefault()?.Id;
            return one == "a" && two == "b" ? null : $"got '{one}' and '{two}'; saving one overwrote the other";
        }),

        ("Delete forgets the deployment", async ct =>
        {
            var store = create();
            await store.SaveAsync(DeploymentState.Empty("site", "acme")
                .With("db", [new ResourceState("db-1", "T", "v1")]), ct);
            await store.DeleteAsync("site", "acme", ct);

            var after = await store.GetAsync("site", "acme", ct);
            return after.Units.Count == 0 ? null : $"{after.Units.Count} units survived the delete";
        }),

        ("Deleting one that is not there does not throw", async ct =>
        {
            await create().DeleteAsync("site", "never-deployed", ct);
            return null;
        }),

        ("Many resources in one unit all survive", async ct =>
        {
            // A real stack has dozens. A backend that keeps the last one looks correct in every test that
            // uses one resource.
            var store = create();
            var resources = Enumerable.Range(0, 25)
                .Select(i => new ResourceState($"r{i}", "T", $"v{i}"))
                .ToList();
            await store.SaveAsync(DeploymentState.Empty("site", "acme").With("db", resources), ct);

            var read = (await store.GetAsync("site", "acme", ct)).For("db");
            return read.Count == 25 ? null : $"wrote 25 resources, read {read.Count}";
        }),
    ];
}
