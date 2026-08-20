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
/// What every <see cref="IStorageBackend"/> must do — the seam that had no suite.
///
/// <para><b>Why this was missing, and why that mattered.</b> <c>docs/DECISIONS.md</c> D20 tells an adopter
/// to write the backend their application needs, hold it to the contract suites, and upstream it if it
/// generalizes. But the two suites below cover the STORES a backend opens, not the backend itself — so
/// somebody writing SQLite or Postgres had nothing holding the part that is actually theirs: reading a
/// descriptor, answering to a kind, and keeping two locations apart. Tyanor cannot ship a provider or a
/// backend for everything on day one, which makes the seams the product; a seam with no contract is a seam
/// somebody has to guess at.</para>
///
/// <para><b>It composes the other two rather than repeating them.</b> Passing this means the backend
/// resolves descriptors correctly AND the stores it hands back satisfy
/// <see cref="StateStoreContract"/> and <see cref="RunHistoryContract"/> — one suite, and the whole of what
/// the engine assumes.</para>
///
/// <para><b>A backend that genuinely cannot hold one of the two</b> should throw
/// <see cref="NotSupportedException"/> from that half, and the checks below accept that as the honest
/// answer. It may not do so for BOTH: something that stores neither is not a backend, and a contract
/// satisfiable by refusing everything is worse than no contract.</para>
/// </summary>
/// <param name="backend">The backend under test.</param>
/// <param name="descriptor">
/// Makes a FRESH descriptor each time it is called — a new file, a new database, a new key. Checks must not
/// see each other's data, and two of these must name genuinely different places.
/// </param>
public sealed class StorageBackendContract(IStorageBackend backend, Func<string> descriptor) : ContractSuite
{
    /// <inheritdoc/>
    public override string Subject => "IStorageBackend";

    private StorageConnection Fresh() => StorageConnection.Parse(descriptor());

    /// <summary>Run one half, or report that the backend honestly refuses it.</summary>
    private static async Task<string?> Supported(Func<Task<string?>> half)
    {
        try { return await half(); }
        catch (NotSupportedException) { return null; }      // stated refusal is a legitimate answer
    }

    private static RunRecord Run(string id) =>
        new(id, "site", "acme", RunKind.Apply, RunStatus.Succeeded, DateTimeOffset.UtcNow);

    private static DeploymentState State(string resource) =>
        DeploymentState.Empty("site", "acme").With("db", [new ResourceState(resource, "T", "v1")]);

    /// <inheritdoc/>
    protected override IReadOnlyList<(string Name, Func<CancellationToken, Task<string?>> Run)> Cases =>
    [
        ("It answers to a kind", _ =>
            Task.FromResult<string?>(string.IsNullOrWhiteSpace(backend.Kind)
                ? "Kind is blank, so no descriptor could ever select this backend"
                : null)),

        ("The descriptors it is given name its kind", _ =>
        {
            // A fixture handing this suite descriptors for a DIFFERENT backend would test nothing, and the
            // failure would look like the backend's rather than the fixture's.
            var kind = StorageConnection.Parse(descriptor()).Kind;
            return Task.FromResult<string?>(
                string.Equals(kind, backend.Kind, StringComparison.OrdinalIgnoreCase)
                    ? null
                    : $"the descriptor names kind '{kind}' but this backend answers to '{backend.Kind}'");
        }),

        ("It stores at least one of state and history", async _ =>
        {
            // Both halves refusing is not a backend. Checked first, because otherwise every check below
            // passes by being skipped.
            var connection = Fresh();
            var state = true;
            var history = true;

            try { backend.OpenState(connection); } catch (NotSupportedException) { state = false; }
            try { backend.OpenHistory(connection); } catch (NotSupportedException) { history = false; }

            await Task.CompletedTask;
            return state || history
                ? null
                : "it refuses both OpenState and OpenHistory, so there is nothing it can be registered for";
        }),

        ("Two state stores opened at one location are ONE store", ct => Supported(async () =>
        {
            // The property everything else rests on: a descriptor names a PLACE. A backend that handed back
            // an independent store per call would lose every write made through the other one, and the guide
            // invites exactly that by writing `new FileStateStore(path)` at the point of use.
            var connection = Fresh();
            await backend.OpenState(connection).SaveAsync(State("db-1"), ct);

            var read = await backend.OpenState(connection).GetAsync("site", "acme", ct);
            return read.For("db").FirstOrDefault()?.Id == "db-1"
                ? null
                : "a second store opened at the same location did not see the first one's write";
        })),

        ("Two run logs opened at one location are ONE log", ct => Supported(async () =>
        {
            var connection = Fresh();
            await backend.OpenHistory(connection).UpsertAsync(Run("r1"), ct);

            var read = await backend.OpenHistory(connection).RecentAsync(50, ct);
            return read.Any(r => r.Id == "r1")
                ? null
                : "a second log opened at the same location did not see the first one's write";
        })),

        ("Different locations are kept apart", ct => Supported(async () =>
        {
            // One machine hosting two deployments is the whole reason a descriptor is configuration rather
            // than a constant. A backend that ignores the target and writes to one place looks perfect until
            // the second deployment appears.
            var one = Fresh();
            var two = Fresh();
            await backend.OpenState(one).SaveAsync(State("db-1"), ct);

            var other = await backend.OpenState(two).GetAsync("site", "acme", ct);
            return other.Units.Count == 0
                ? null
                : $"state written at '{one.Target}' was visible at '{two.Target}'";
        })),

        ("The state store it opens satisfies StateStoreContract", ct => Supported(async () =>
        {
            // Composed rather than repeated. A fresh descriptor per call is what makes each check in the
            // delegated suite start from an empty store.
            try
            {
                await new StateStoreContract(() => backend.OpenState(Fresh())).AssertAllAsync(ct);
                return null;
            }
            catch (ContractException e) { return e.Message; }
        })),

        ("The run history it opens satisfies RunHistoryContract", ct => Supported(async () =>
        {
            try
            {
                await new RunHistoryContract(() => backend.OpenHistory(Fresh())).AssertAllAsync(ct);
                return null;
            }
            catch (ContractException e) { return e.Message; }
        })),
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
            // A neutral resource type, not a real vendor's. This suite is run by whoever wrote a store —
            // for Postgres, for Redis, for a filing cabinet — and a sample reading `AWS::RDS::DBInstance`
            // teaches every one of them that a `Type` looks like CloudFormation's, which is the leak
            // `provider-boundary.md` is about wearing test-data clothes.
            var written = DeploymentState.Empty("site", "acme")
                .With("db", [new ResourceState("db-1", "database/instance", "v1")]);
            await store.SaveAsync(written, ct);

            var read = await store.GetAsync("site", "acme", ct);
            var resource = read.For("db").FirstOrDefault();
            if (resource is null) return "the unit came back with no resources";
            if (resource.Id != "db-1") return $"Id came back as {resource.Id}";
            if (resource.Type != "database/instance") return $"Type came back as {resource.Type}";
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
                .With("web", [new ResourceState("web-1", "storage/bucket")]), ct);

            var fingerprint = (await store.GetAsync("site", "acme", ct)).For("web").FirstOrDefault()?.Fingerprint;
            return fingerprint is null ? null : $"a null fingerprint came back as '{fingerprint}'";
        }),

        ("The serial advances on every save", async ct =>
        {
            // The STORE owns the serial: a caller hands back the version it read, and the store persists at
            // one past it. That is what lets a backend with conditional writes refuse a save derived from
            // state someone else has replaced.
            //
            // This used to assert the opposite — that the serial a caller wrote came back unchanged — which
            // is satisfiable by a store that never advances it at all, and which paired with a `With` that
            // incremented meant a conditional store implemented as documented would refuse EVERY save.
            var store = create();

            var first = DeploymentState.Empty("site", "acme").With("db", [new ResourceState("db-1", "T", "v1")]);
            await store.SaveAsync(first, ct);

            var afterFirst = await store.GetAsync("site", "acme", ct);
            if (afterFirst.Serial <= first.Serial)
                return $"saved state read at serial {first.Serial}, and it came back at " +
                       $"{afterFirst.Serial} — a save has to advance it or nothing can detect a clobber";

            // Round trip: hand back exactly what was read, and it advances again.
            await store.SaveAsync(afterFirst.With("db", [new ResourceState("db-1", "T", "v2")]), ct);

            var afterSecond = (await store.GetAsync("site", "acme", ct)).Serial;
            return afterSecond > afterFirst.Serial
                ? null
                : $"a second save left the serial at {afterSecond}, having read {afterFirst.Serial}";
        }),

        ("Editing state does not advance the serial by itself", async ct =>
        {
            // `With` is an edit of the version that was READ, so it must not move the number the store
            // compares against — otherwise a Refresh, which edits every unit before saving once, would hand
            // back a serial ahead by the unit count and no comparison could work.
            var store = create();
            await store.SaveAsync(DeploymentState.Empty("site", "acme")
                .With("db", [new ResourceState("db-1", "T", "v1")]), ct);

            var read = await store.GetAsync("site", "acme", ct);
            var edited = read
                .With("db", [new ResourceState("db-1", "T", "v2")])
                .With("api", [new ResourceState("api-1", "T", "v1")]);

            return edited.Serial == read.Serial
                ? null
                : $"two edits moved the serial from {read.Serial} to {edited.Serial}";
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
