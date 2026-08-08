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
> So: roughly **half the code, most of the knowledge, and none of the running**.
>
> **That last part is no longer true.** `Tyanor.Providers.Local` ships and deploys — a directory
> materialized from an artifact, a process run out of it, a health check, a teardown — and it was built
> before AWS on purpose, to test the contracts against a shape they were not extracted from. It found two
> (`ValidateAsync` assumed credentials exist; `Options` assumed homogeneous units) and confirmed that
> `UnitPhase`, `Reconcile` and the engine needed nothing. See **D13**. What is still true: no *cloud* has
> been deployed to, and no real consumer ships on Tyanor yet.

Open work, worked one item at a time, top first. Implement fully (rules → code → tests), update the docs
it touches, **remove the item**, then commit. Discovered work is added here, never dropped.

---

## 1. `Tyanor.Providers.Aws` — port the tested AWS deployer

Nothing drives a real cloud yet. The port source is Aurelia's `apps/desktop/Aurelia.Deployment` —
**~2,600 lines that have deployed real infrastructure**, survived a crash-and-rebuild mid-run, and torn
down cleanly.

**Unblocked as of D13**: the contracts have now been tested against a second shape, so the reason to wait
is gone. `Tyanor.Providers.Local` is the worked reference — read it for what the six driver methods look
like when nothing is faked, and note how little of it is anything but provider vocabulary.

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

## 2. Put a real consumer on it

D13 proved a second *shape* fits, using a provider and tests inside this repo. It did not prove a second
*consumer* fits, and that is a different claim: a real application brings a lifecycle, a UI, a logging
opinion and a configuration story, and those are where a library gets pushed on.

- **Daoris self-hosting a server** is the closest fit — `Tyanor.Providers.Local` was built to its shape,
  so this is now mostly wiring rather than design.
- **Aurelia** is the other half, and it needs item 1 first.

Expect this to move `DeploymentRequest` again. That is not a failure of D13; a test cannot want something
a person will.

- Acceptance: one of the two ships a deployment through Tyanor, with the composition root in the
  application and no Tyanor change required to make it work.

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
- **A third provider** (Kubernetes, SSH, a container host). Two shapes have now been checked against each
  other and agree (D13); a third proves nothing further until a consumer asks for it.
- **Anything a provider could orchestrate for itself.** The local provider was tempted twice — stopping a
  process before replacing files, and retrying its own health check — and both belong to the engine, which
  already has them. A provider that grows run-state logic is writing a second engine inside itself.
