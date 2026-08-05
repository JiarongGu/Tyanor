# CLAUDE.md — Tyanor

Operating rules for every Claude Code session in this repo. Read top-to-bottom on session start. These
**override generic knowledge** — they encode decisions that were expensive to reach.

> **Tyanor (天仪) is a local-first operations and delivery platform.** A procedure — restore, build, test,
> package, publish, deploy, validate — is defined once in C# and executed against pluggable providers.
> Cloud providers are implementations; the operational doctrine does not change when the infrastructure
> does.

---

## 1. Read the rules before writing code

[`.claude/rules/RULES_INDEX.md`](.claude/rules/RULES_INDEX.md) lists every rule and when it applies. Read
the matching ones **first**. The four today are short and all load-bearing:

- **[`reconcile-dont-mirror.md`](.claude/rules/reconcile-dont-mirror.md)** — the RECONCILE loop reads the
  provider live and attaches to work in flight. (Its "no state file" opening is superseded by **D12**:
  Tyanor does keep one set of deployment state. Read D12 first.)
- **[`error-classification.md`](.claude/rules/error-classification.md)** — credentials / transient / hard.
- **[`units-not-graphs.md`](.claude/rules/units-not-graphs.md)** — ordered list, reverse teardown, no DAG.
- **[`provider-boundary.md`](.claude/rules/provider-boundary.md)** — Core names no vendor.

Reasoning behind each lives in [`docs/DECISIONS.md`](docs/DECISIONS.md). A decision there is not
re-litigated per feature — but it *can* be overturned by appending a new entry that says why.

## 2. What this library is, in one paragraph

An operator declares a `Procedure` (an ordered list of `ProcedureUnit`). `ProcedureRunner` walks it: for
each unit it asks the provider what phase that unit is in, calls `Reconcile.Decide`, and carries out the
one resulting action. Failures are classified by the provider into three classes, which the engine turns
into a pause (resumable) or a failure (terminal). Runs are recorded in `IRunHistory`; what Tyanor OWNS is
recorded in `IStateStore` and re-synced from reality by `RefreshAsync`, which is what makes a safe teardown
and honest add/change/destroy counts possible.

**Resume is not a feature; it is the absence of one.** Applying and resuming are the same call, because
each unit is decided from what is true now rather than from what a previous run remembered.

## 3. Layout

```
src/
  Tyanor.Core/      contracts + the reconcile decision. No I/O, no provider, no dependencies.
  Tyanor.Engine/    ProcedureRunner: ordering, reconcile, retry, classified outcomes, history + state.
  Tyanor.Extensions.DependencyInjection/   AddTyanor. Optional — the engine works without a container.
  Tyanor.Providers.*/   one per target. The ONLY place vendor vocabulary exists.
tests/
  Tyanor.Core.Tests/    pure tests for the decision logic. Always-on, no cloud, no mocks of an SDK.
```

**`Tyanor.Core` AND `Tyanor.Engine` take no package dependencies** — `doctor` checks it. If something needs
one, it belongs in a package beside them, not in them.

## 4. Before you commit: `npm run doctor`

One command — build, test, and every check the repo can make cheaply. It exists so the checklist is not
something anyone has to remember, because the step people forget is the step that breaks.

```
npm run doctor                    build + test + decisions + rules + sensitive + the two claims
node devtools/dev.mjs decisions   supersession points both ways; every cited D<n> exists
node devtools/dev.mjs rules       every rule indexed, every link resolves
node devtools/dev.mjs sensitive   credential scan
```

Two of doctor's checks verify **claims the README makes out loud** — that `Tyanor.Core` and
`Tyanor.Engine` take no package dependencies, and that the version ships from one place. If one fails
because the claim changed deliberately, change the claim. Do not silence the check.

`devtools/README.md` says what goes wrong without each check. Everything is driven by
`devtools/project.config.mjs`; no tool names Tyanor.

## 5. Conventions

- **Never commit without explicit approval.** Branch off `main`; Conventional Commits.
- **`TreatWarningsAsErrors` is on, with XML docs required.** A public type without a `<summary>` fails the
  build. That is deliberate: this library's value is largely in *why*, and the why belongs where the reader
  is, not in a wiki.
- **Comments say why, never what.** `// increment i` is noise; `// Attach: re-issuing here starts a second
  conflicting operation` is the reason the line exists.
- **Test the decisions, not the plumbing.** The reconcile table and the classifiers are where a silent bug
  hides — a wrong decision looks like a differently-ordered deployment, and `Attach` fails by *succeeding*
  at something it should never have started.
- **Live provider calls stay behind an env-gated integration test**, skipped as a vacuous pass, so an
  ordinary run never touches a cloud or spends money.

## 6. Ecosystem

Four independent libraries; none depends on another.

| | |
|---|---|
| [Lyntai](https://github.com/JiarongGu/Lyntai) (灵台) | AI cognition — providers, routing, memory |
| [Shenora](https://github.com/JiarongGu/Shenora) (神阙) | Desktop runtime — the shell an app is built in |
| [Daoris](https://github.com/JiarongGu/Daoris) (道衍) | Engineering doctrine — how the work is done |
| **Tyanor** (天仪) | **Operations & delivery — how the work ships and runs** |

## 7. Where knowledge lives

| Information | Location |
|---|---|
| Enforced conventions | `.claude/rules/*.md` (indexed in `RULES_INDEX.md`) |
| Why a decision was made | `docs/DECISIONS.md` |
| How to add a provider | `.claude/skills/add-provider/SKILL.md` |
| What is left to build | `TASKS.md` |
| System shape | `docs/architecture/overview.md` |
