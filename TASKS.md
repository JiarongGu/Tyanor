# TASKS — Backlog

> ## Where day one landed
>
> **The expensive part was the learning, and it is banked.** Tyanor is ~1,200 lines over 4 projects with
> 50 tests, extracted from a deployer that had already run real infrastructure. What came across is the
> part that is hard to *discover* — which reconcile branches exist, that only a terminal event may fail a
> call, that a credential error must pause rather than fail, that a plan can be read from the provider
> instead of a file. None of that was designed here; it was learned the expensive way, elsewhere.
>
> **What is NOT here is the part that is merely hard to type.** Measured against the source
> (`Aurelia.Deployment`, 2,597 lines):
>
> | | Lines | State |
> |---|---|---|
> | Operational doctrine | ~700 of `AwsDeployer` + the contracts | ✅ ported, generalized, tested |
> | AWS mechanics (CFN/S3/ACM/Route 53 calls) | ~1,000 | ❌ not started — item 1 |
> | Host IPC (`DeployModule`) | 585 | stays in Aurelia; it is UI wiring, not operations |
>
> So: roughly **half the code, most of the knowledge, and none of the running**. Nothing has deployed
> anything yet, and no claim here should be read as if it had.
>
> **The one scoping call that matters most:** *do not port AWS before item 2.* The contracts will move when
> a second, differently-shaped consumer arrives, and hardening them around one provider is exactly the
> mistake that produced `CdkOutDir` in the source. Item 2 is cheap now and expensive after either consumer
> ships.

Open work, worked one item at a time, top first. Implement fully (rules → code → tests), update the docs
it touches, **remove the item**, then commit. Discovered work is added here, never dropped.

---

## 1. `Tyanor.Providers.Aws` — port the tested AWS deployer

The engine is provider-neutral and unit-tested, but nothing drives a real cloud yet. The port source is
Aurelia's `apps/desktop/Aurelia.Deployment` — **~2,600 lines that have deployed real infrastructure**,
survived a crash-and-rebuild mid-run, and torn down cleanly.

Split as measured on 2026-08-06:

| Source | Lines | Destination |
|---|---|---|
| `Aws/*`, `AwsCloudFormation/*`, `Domains/AwsRoute53*` | ~800 | straight port into the provider |
| `AwsDeployer.cs` | 699 | **split** — reconcile/classify/retry are already in the engine; keep only the CloudFormation calls |
| `DeployModule.cs` | 585 | **stays in Aurelia** — it is host IPC, not operations |

- Map CloudFormation status strings → `UnitPhase`. The four that matter: a non-rollback `*_IN_PROGRESS` is
  `Converging`; a rollback `*_IN_PROGRESS` is `Unwinding`; `ROLLBACK_COMPLETE` and `*_FAILED` are `Broken`;
  `*_COMPLETE` is `Ready`.
- Port `ClassifyAwsError` as the `IFailureClassifier`. It already names the real codes — keep every one,
  and keep the `InnerException` walk.
- `"No updates are to be performed"` is `UpdateAsync → false`, not an error.
- Acceptance: the phase table and the classifier are unit-tested against real status/code strings; a live
  deploy stays behind `TYANOR_LIVE_AWS`.

## 2. Prove the abstraction with a second, differently-shaped consumer

Aurelia deploys a static site plus a serverless API. **Daoris needs to self-host a server** — a different
shape, and the reason Tyanor exists rather than a helper library inside Aurelia.

One consumer makes an abstraction a guess; two make it honest. Expect this to move `DeploymentRequest`
and to reveal at least one thing wrongly assumed to be generic — that is the point, and it is cheaper now
than after either consumer ships.

- Acceptance: both procedures run on the same engine with no `if (provider == …)` anywhere in Core or
  Engine.

## 3. State backends beyond a local file — SQLite, Postgres, S3

`FileRunHistory` ships and is the default; the seam is `IRunHistory` and the choice is the consumer's
(`AddTyanor(cfg => cfg.UseFileState(...))`). What is missing is everywhere else state needs to live.

One package per backend so `Tyanor.Core` stays dependency-free — the sibling libraries' shape:

- **`Tyanor.Storage.Sqlite`** — a single-machine operator with more than a file's worth of history.
- **`Tyanor.Storage.Postgres`** — a team, or a service that already has a database.
- **`Tyanor.Storage.S3`** — CI and multiple machines sharing one history.

**Cross-machine CHECKING is supported; cross-machine SYNCING is not — D9 as scoped by D11.**
`PlanAsync` reads the shared history, so a second machine sees `ActiveRun`, `HasStalledRun` ("a run is
recorded live but nothing is converging — it stopped, possibly on a machine that is not coming back") and
`InSync`. `ApplyAsync` adopts a live run rather than opening a competing one. **No lease, no lock** — the
provider arbitrates by attachment, and the plan makes the situation visible. Do not add locking here
without a case that attachment demonstrably cannot cover.

**What each backend must decide deliberately: concurrent writes.** `FileRunHistory` is last-writer-wins
with no cross-process lock, so two machines writing at the same instant can lose a record. That is bounded
to visibility — the provider is still the arbiter, so infrastructure stays correct — but it stops being
acceptable the moment anything automated gates on the history. S3 preconditions and a Postgres transaction
are the cheap correct answers, and they belong in the backend, not as a new concept in the engine.

- Every backend must refuse to delete a live record (`RunRecord.IsLive`) — the guard is per-implementation
  today, and a shared test suite over `IRunHistory` would be a better home for it.
- Acceptance: kill the process mid-run; a new process finds the live record via `LiveAsync` and resumes.
  For a shared backend, do it from a DIFFERENT machine.

## 4. Decide what a "procedure" is authored as

Today a `Procedure` is constructed in C#. The brief wants restore → build → test → package → publish →
deploy → validate, which is broader than deployment units.

**Do not design this until items 1–3 are done.** The engine's shape should be pulled by two real
procedures, not pushed by a diagram — and the temptation here is to invent a DSL, which
`units-not-graphs.md` exists to resist.

---

## Deferred, deliberately

- **A resource-level diff** ("this property will change from X to Y"). Wants a resource model, which wants
  a graph (D3). The UNIT-level plan that shipped gives most of the value — what will be created, replaced,
  or waited on — for none of that cost.
- **Plugin discovery.** Providers register in the composition root (D6).
- **Any provider beyond AWS**, until item 2 says what is actually shared.
