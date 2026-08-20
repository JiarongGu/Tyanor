# Architecture

What the system IS. For what to *do* with it, in order, see [`../guide.md`](../guide.md); for every setting
the shipped providers read, [`../providers.md`](../providers.md); for why each choice was made,
[`../DECISIONS.md`](../DECISIONS.md).

## The whole model in one page

```
Procedure            an ordered list of units          ← you write this
   │
ProcedureRunner      for each unit, in order:          ← Tyanor.Engine (namespace)
   │                     phase   = driver.PhaseAsync(context)
   │                     action  = Reconcile.Decide(phase)
   │                     carry out that one action
   │
IUnitDriver          phase · create · update           ← Tyanor.Providers.*
   │                 remove · await · refresh
   │
provider API         CloudFormation, a process, ssh…
```

Nothing else is going on. The engine has no workflow state and no memory of a previous run: every unit's
action is decided from what the provider reports now.

Alongside that, and never feeding into the decision, Tyanor keeps **one set of state** — what it owns, per
unit — because a provider working with raw resources cannot say what Tyanor created, and a teardown needs
to know ([`../DECISIONS.md`](../DECISIONS.md) D12). `RefreshAsync` re-reads reality and rewrites state to
match, which is why a stale mirror costs a wrong *count* and never a wrong *action*.

Every driver method takes a `UnitContext` — the unit, the request, progress and cancellation — so the
contract can grow without breaking every implementation again (D16).

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

A teardown has its own table, because it has its own two answers:

| Phase | `Reconcile.DecideDestroy` |
|---|---|
| `Missing` | `Nothing` — already gone |
| anything else | `Remove` |

No `Attach` there, deliberately: a unit mid-create is a unit that will exist in a minute, and waiting for
someone else's creation to finish before destroying it is a longer teardown with the same ending.

## Why this makes resume free

Applying and resuming are the same call. A unit that finished reports `Ready` and its update reports "no
change"; one that was mid-flight when the process died reports `Converging` and is attached to; one that
never started reports `Missing` and is created. Three different histories, one code path, no bookkeeping.

The same property handles a second operator running the same procedure, a closed laptop, and a machine
that never comes back.

## The six things an operator does

Deliberately Terraform's verbs, because they name the same jobs (D22):

| | Touches the provider? | |
|---|---|---|
| `ValidateAsync` | **no** | every problem in the definition, in one pass, before an account exists |
| `PlanAsync` | read-only | what a run would do, in **either** direction |
| `ApplyAsync` | yes | which is also the resume — there is no separate one |
| `DestroyAsync` | yes | reverse order, so importers go before what they import from |
| `RefreshAsync` | read-only | re-sync state from reality; repairs a stale mirror |
| `OutputsAsync` | read-only | what the deployment produced — the URL somebody asks for |

`Plan.IsDestructive` is the line to put a confirmation behind: a create or an update is recoverable by
running it again, and a destroy is not.

**Cancelling pauses; it does not fail.** The provider is still converging whatever it was given, so the run
is recorded `Paused` with `PauseReason.External`, stays LIVE, and the next apply adopts it — a cancel is
resumed the same way every other pause is. The ending is written with no cancellation token, because being
told to stop is not a reason to stop recording why. A token already cancelled before the run starts is the
one case that leaves no record: nothing happened.

`procedure.Only("web")` narrows any of them to some units — Terraform's `-target`, and safer for having no
dependency graph to skip: a subset of an ordered list is still ordered (D21).

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

A wrong *definition* — an artifact part that was never built, a unit that declares no kind — is a
`DefinitionException`, which providers do not classify at all. Null means `Hard`, which is what it is. The
base type exists so a consumer can tell "you configured this wrongly, nothing was touched" from "AWS said
no" without matching on message text.

## What lives where

Three packages: `Tyanor`, and one per provider (D26). Inside `Tyanor` the layering is expressed by namespace,
and `src/Tyanor/` mirrors it — a folder per namespace, so a type's `using` tells you its path.

| | |
|---|---|
| `Tyanor` | Contracts and the pure decisions. No I/O, and it names no vendor. |
| `Tyanor.Engine` | Ordering, reconcile, retry, the operator-facing wording, and `AddTyanor`. |
| `Tyanor.Engine.State` | Run history and deployment state — file and in-memory — and the storage backends. |
| `Tyanor.Testing` | Contract suites an implementation runs to prove itself, and `MemoryTarget` — a provider that deploys to a dictionary, for testing an application's own code. |
| `Tyanor.Providers.*` | A package each. Everything vendor-shaped: status vocabulary, API calls, waiting, classification. |

The package takes exactly one dependency — `Microsoft.Extensions.DependencyInjection.Abstractions`, for
`AddTyanor` — and `doctor` holds it to that list in both directions. **No test framework**, which is what
lets the contract suites run under whichever one the implementer already has.

The namespace boundary is not enforced by the compiler any more, so it is enforced by review: if a type in
the `Tyanor` namespace needs a package or names a vendor, it is in the wrong namespace. See
[`../DECISIONS.md`](../DECISIONS.md) D4 for the concrete leak that motivated vendor-neutrality, D10 for why
every seam is optional, and D26 for why one package rather than four.

## More than one provider at a time

`DeploymentTargets` holds them, keyed by `IDeploymentTarget.Id`, and `ProcedureRunners.For(id)` builds a
runner for one over the history and state store the application configured. Resolving "the" runner with
several registered throws and names them rather than picking — registering a second provider must not
silently change where a deployment goes (D15).

## Where state lives

Named, not coded: `"{kind}:{target}"` resolved through registered `IStorageBackend`s — Terraform's *backend*,
and the same idea (D20).

```
json:/var/lib/myapp/state.json      sqlite:/var/lib/myapp/tyanor.db
postgres:Host=db;Database=ops       s3://my-bucket/tyanor/state.json
```

Two stores, deliberately separate. What Tyanor **owns** (`IStateStore`) has to stay true; the run **log**
(`IRunHistory`) is an append-only account of attempts. Different lifetimes, and a team sharing one does not
necessarily want to share the other.

State exists to answer three questions, and a plan asks all three (D25):

| Compare | Answers | Reported as |
|---|---|---|
| config ↔ reality | what will this run do? | `Plan.Steps` — the reconcile |
| state ↔ reality | did the world move without me? | `Plan.Drift`, repaired by `Refresh` or `Apply` |
| config ↔ state | do I own something the code no longer mentions? | `Plan.Orphaned` |

The third is the one a reconcile loop structurally cannot ask, because every other pass walks the
procedure's units and an orphan is by definition not in them. It is reported rather than destroyed: Tyanor's
state holds identity, not a resource model, so nothing left in it can tell a driver how to remove the thing.

`Serial` is the version a snapshot was READ at — the store advances it on write, so a backend with
conditional writes can refuse a save derived from state someone else replaced.

Within one PROCESS the file stores are safe to use from anywhere: the lock is per file, not per object, so
constructing one wherever you need it is fine and reads never collide with writes. Between processes it is
still last-writer-wins — the boundary D11 and D20 describe, and where a backend with conditional writes is
the answer.

Only `json` ships, registered by default because it is the only kind that needs no package, no server and no
decision on day one. A bare path is refused rather than guessed: `"sqlite/state.db"` would otherwise read as a
file called `sqlite/state.db` and write state somewhere nobody meant.

`InMemoryRunHistory` and `InMemoryStateStore` exist for tests and one-shot CI runs, and are never a default.
Choosing them is choosing to give up resume and safe teardown — which is a reasonable trade in a test and a
surprise anywhere else, so it is made explicitly (`cfg.UseInMemoryState()`).

## Three seams, one answer

The same shape resolved three separate questions, which is why it is the thing to reach for next time:

| I need | Write | Register |
|---|---|---|
| a whole new target | `IDeploymentTarget` + `IUnitDriver` (D15) | `cfg.AddTarget(…)` |
| one step of my own | `StepUnitDriver` (D19, D31) | `new AwsTarget(creds, new CustomUnits { … })` |
| state somewhere else | `IStorageBackend` (D20) | `cfg.AddStorage(…)` |

**Build it where you need it, prove it with the contract suites in `Tyanor.Testing`, upstream it if it
generalizes.** Nothing is discovered from disk in any of the three (D6) — a deployment tool holds credentials,
so it does not run code it merely found. Authoring a plugin and loading one are different questions.

## What is deliberately absent

- **A dependency graph** (D3) — ordering covers the real cases, and the one constraint that looked like it
  needed edges was absorbed by changing an operation instead (D13).
- **A resource-level diff** ("this property becomes that") — needs a resource model, which needs the graph.
  The unit-level plan plus resource-level add/change/destroy counts gives most of the value for none of it.
- **Synthesis at apply time** (D5) — Tyanor executes a pre-built artifact.
- **Plugin discovery** (D6) — providers register in the composition root. Writing your own is supported and
  first-class (D15); *loading code found on disk* is the part that is refused.
- **Coordination between machines writing state at once** (D12) — divergence is shown, not resolved.

## Providers

Two ship, and they were built in this order on purpose.

`Tyanor.Providers.Local` deploys a self-hosted server to a machine, and it is the shape with **no control
plane** — nothing keeps converging once the process that started the work is gone, and nothing can be asked
what belongs to a deployment. Everything the engine takes for granted against a cloud is built there out of
a pid file and a marker (D13). It is the useful one to read before writing your own.

`Tyanor.Providers.Aws` is CloudFormation stacks plus website content in S3 behind CloudFront, ported from a
deployer that ran real infrastructure (D14). Its phase table and classifier are tested against the real
status and error strings, and the driver's own control flow — which request it builds, what it does with a
throttle, whether a teardown re-runs — against recording fakes. **No request has reached AWS**: that is what
the live test, gated behind `TYANOR_LIVE_AWS`, is for. Fakes for our control flow, a cloud for their
semantics (D23).

Adding a provider: [`../../.claude/skills/add-provider/SKILL.md`](../../.claude/skills/add-provider/SKILL.md).
Run the contract suites in `Tyanor.Testing` against it — they are in the package it already references,
they are what the built-in providers run, and
passing them is how a provider written elsewhere earns its way in.
