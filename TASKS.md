# TASKS — Backlog

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

## 3. Run history that survives the process

`IRunHistory` has no implementation. A run record must be readable after a crash or it cannot make a run
resumable — that is the whole contract.

- SQLite is the obvious first one. Keep it in a separate package so Core stays dependency-free.
- Must refuse to delete a live record (`RunRecord.IsLive`) — see `reconcile-dont-mirror.md`.
- Acceptance: kill the process mid-run; a new process finds the live record via `LiveAsync` and resumes.

## 4. Decide what a "procedure" is authored as

Today a `Procedure` is constructed in C#. The brief wants restore → build → test → package → publish →
deploy → validate, which is broader than deployment units.

**Do not design this until items 1–3 are done.** The engine's shape should be pulled by two real
procedures, not pushed by a diagram — and the temptation here is to invent a DSL, which
`units-not-graphs.md` exists to resist.

---

## Deferred, deliberately

- **A plan/diff step.** Wants a resource model, which wants a graph, which is the thing being avoided (D3).
- **Plugin discovery.** Providers register in the composition root (D6).
- **Any provider beyond AWS**, until item 2 says what is actually shared.
