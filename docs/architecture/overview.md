# Architecture

## The whole model in one page

```
Procedure            an ordered list of units          ← you write this
   │
ProcedureRunner      for each unit, in order:          ← Tyanor.Engine
   │                     phase   = driver.PhaseAsync(unit)
   │                     action  = Reconcile.Decide(phase)
   │                     carry out that one action
   │
IUnitDriver          create · update · remove · await  ← Tyanor.Providers.*
   │
provider API         CloudFormation, kubectl, ssh…
```

Nothing else is going on. The engine has no workflow state, no plan, and no memory of a previous run: it
asks the provider what is true and decides again.

## The reconcile table

`Reconcile.Decide` is the entire resume model, and it is a pure function:

| Phase | Action | Why |
|---|---|---|
| `Missing` | `Create` | nothing there |
| `Ready` | `Update` | healthy — apply the desired config |
| `Converging` | **`Attach`** | someone's operation is in flight — **watch, issue nothing** |
| `Unwinding` | `SettleThenRecreate` | rolling back; wait for it to settle, then remake |
| `Broken` | `Recreate` | settled unusable; the provider will refuse an update |

`Attach` is the one that matters most and the one that fails most quietly: some providers reject a second
concurrent operation, and the dangerous ones accept it. `Reconcile.Mutates(Attach)` is `false`, and a test
pins that.

## Why this makes resume free

Applying and resuming are the same call. A unit that finished reports `Ready` and its update reports "no
change"; one that was mid-flight when the process died reports `Converging` and is attached to; one that
never started reports `Missing` and is created. Three different histories, one code path, no bookkeeping.

The same property handles a second operator running the same procedure, a closed laptop, and a machine
that never comes back.

## Failure

A provider classifies its own errors (`IFailureClassifier`) into three classes. The engine turns them into
an outcome:

| Class | Outcome | Operator does |
|---|---|---|
| `Credentials` | pause, `credentials` | re-authenticate, resume |
| `Transient` | retry bounded, then pause | wait, resume |
| `Hard` | fail | change the definition |

Unrecognised errors classify as `Hard` — the one nobody anticipated is the one that must not be silently
retried.

## What lives where

`Tyanor.Core` holds contracts and the decision, and takes **no package dependencies**. If a type there
needs one, it is not Core. It also names no vendor: an artifact is opaque named parts, and provider
settings live in an untyped `Options` map — see [`../DECISIONS.md`](../DECISIONS.md) D4 for the concrete
leak that motivated this.

`Tyanor.Engine` holds ordering, retry, history and the operator-facing wording.

`Tyanor.Providers.*` holds everything vendor-shaped: status vocabulary, API calls, waiting, classification.

## What is deliberately absent

- **A state file** (D1) — the largest single simplification.
- **A dependency graph** (D3) — ordering covers the real cases.
- **A plan/diff step** — needs a resource model, which needs a graph.
- **Synthesis at apply time** (D5) — Tyanor executes a pre-built artifact.
- **Plugin discovery** (D6) — providers register in the composition root.

## Extending

Adding a provider: [`../../.claude/skills/add-provider/SKILL.md`](../../.claude/skills/add-provider/SKILL.md).
