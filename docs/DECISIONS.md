# Decisions

Load-bearing choices, with the reasoning that produced them. A decision recorded here is not re-litigated
per feature — but it can be *overturned*, by appending a new entry that says why. Never edit one to say
something it did not say.

Each entry names what was decided, what it was decided **against**, and what evidence exists. "It seemed
cleaner" is not evidence.

**Every entry has a number, and `D12` anywhere in this repository means entry 12 of this file.** They are
cited from code comments, rules, the guide, the changelog and the backlog, because the reasoning belongs
where the reader is rather than only here. The numbers are permanent and never reused — a decision that is
overturned keeps its number and gains a banner pointing at whatever overtook it. `npm run doctor` refuses a
citation of an entry that does not exist, anywhere in any `.cs`, `.md`, `.mjs` or `.yml`, so a reference is
never left dangling by a rename or a number typed from memory.

## Index

An overtaken entry carries a banner pointing forward; `doctor` checks that both directions exist, because
the way a log like this rots is that the entry which supersedes says so and the original says nothing.

**The shape of the thing**

| | | |
|---|---|---|
| [D3](#d3--units-are-an-ordered-list-not-a-graph-2026-08-06) | ordered list, not a graph | plus D13, D21 |
| [D8](#d8--terraforms-how-cdks-what-2026-08-06) | Terraform's *how*, CDK's *what* | the positioning |
| [D10](#d10--tyanor-is-a-library-not-a-service-2026-08-06) | a library, not a service | ⚠ packages merged by D26 |
| [D26](#d26--one-library-package-three-in-total-2026-08-19--amends-d10) | one library package, three in total | amends D10 |
| [D5](#d5--tyanor-executes-a-pre-built-artifact-it-does-not-synthesize-2026-08-06) | executes, does not synthesize | |
| [D28](#d28--net-10-only-and-it-is-a-floor-rather-than-a-limit-2026-08-19) | .NET 10 only, deliberately | revisit for a PERSON, not a survey |
| [D21](#d21--the-pipeline-does-not-need-a-new-authoring-model-it-needs-unit-kinds-2026-08-09) | a pipeline is unit kinds, not a DSL | |

**The engine**

| | | |
|---|---|---|
| [D1](#d1--reconcile-against-the-provider-keep-no-state-file-2026-08-06) | reconcile against the provider | ⚠ overtaken by D12 |
| [D2](#d2--three-failure-classes-because-there-are-three-responses-2026-08-06) | three failure classes | |
| [D16](#d16--the-gate-goes-in-front-of-the-destructive-direction-too-2026-08-09) | destroy gets a plan; `UnitContext` | ⚠ verbs renamed by D22 |
| [D18](#d18--validate-offline-and-say-what-the-deployment-produced-2026-08-09) | validate offline; outputs | |
| [D17](#d17--a-name-is-checked-where-it-stops-being-a-label-2026-08-09) | names are checked, not sanitised | |
| [D22](#d22--the-operator-facing-verbs-are-terraforms-2026-08-18--amends-d16) | the operator's verbs are Terraform's | amends D16 |

**State**

| | | |
|---|---|---|
| [D7](#d7--run-state-is-persisted-at-a-location-the-consumer-configures-2026-08-06--amends-d1) | run state is persisted | ⚠ overtaken by D12 |
| [D12](#d12--there-is-one-set-of-state-local-or-remote-kept-current-and-re-syncable-2026-08-06--supersedes-d1-d7-d11) | **one set of state, re-syncable** | supersedes D1, D7, D11 |
| [D9](#d9--cross-machine-is-a-capability-made-safe-by-visibility-rather-than-by-locking-2026-08-06) | cross-machine by visibility, not locks | ⚠ scoped by D11 |
| [D11](#d11--we-support-state-checking-not-cross-machine-syncing-2026-08-06--scopes-d9) | checking, not syncing | ⚠ overtaken by D12 |
| [D20](#d20--storage-is-a-kind-and-a-connection-2026-08-09) | storage is a kind and a connection | |
| [D25](#d25--state-answers-three-questions-and-one-of-them-had-no-code-2026-08-18) | config ↔ state was never compared | orphans, serial, schema |

**Extending it**

| | | |
|---|---|---|
| [D4](#d4--core-names-no-vendor-2026-08-06) | Core names no vendor | |
| [D6](#d6--no-plugin-discovery-2026-08-06) | no plugin *discovery* | authoring is supported — D15 |
| [D15](#d15--a-provider-written-elsewhere-is-a-first-class-provider-2026-08-09) | a provider written elsewhere is first-class | |
| [D19](#d19--an-applications-own-step-is-a-unit-not-code-that-runs-afterwards-2026-08-09) | an application's own step is a unit | |
| [D24](#d24--tyanor-ships-a-target-to-test-against-not-just-suites-to-test-with-2026-08-18) | a target to test against | it is a provider, not a mock |
| [D27](#d27--the-public-surface-is-a-file-because-010-is-when-it-stops-being-free-2026-08-19) | the public surface is a checked-in file | found 2 defects on its first run |

**What was built, and what it cost**

| | | |
|---|---|---|
| [D13](#d13--the-abstraction-was-tested-against-a-second-shape-before-aws-was-ported-2026-08-08) | the local provider, built before AWS on purpose | |
| [D14](#d14--the-aws-port-keeps-the-knowledge-and-leaves-the-application-behind-2026-08-08) | the AWS port — **never run against AWS** | ⚠ scoped by D23 |
| [D23](#d23--a-fake-cannot-tell-you-what-aws-does-it-can-tell-you-what-we-do-2026-08-18--scopes-d14) | fakes for our control flow, a cloud for their semantics | scopes D14 |
| [D29](#d29--a-sync-converges-in-both-directions-and-a-unit-owns-what-it-fills-2026-08-20) | a sync converges; a unit owns what it fills | a deleted page served for ever |
| [D30](#d30--only-against-the-real-thing-is-a-claim-about-the-question-not-the-provider-2026-08-20--scopes-d15-d23) | the gate is per QUESTION, not per provider | scopes D15, D23 |
| [D31](#d31--the-seams-are-the-product-so-their-cost-is-a-feature-2026-08-20--amends-d24) | **the seams are the product** — pause, backend contract, `StepUnitDriver` | amends D24 |
| [D32](#d32--a-teardown-has-three-answers-because-some-things-do-not-come-back-2026-08-20--amends-d16) | a teardown has THREE answers — some things do not come back | amends D16 |
| [D33](#d33--a-provider-owns-infrastructure-too-and-until-now-nothing-removed-it-2026-08-20) | a provider owns infrastructure too, and a full destroy sweeps it | found by the first adopter |
| [D34](#d34--some-values-only-exist-once-the-run-is-under-way-and-a-unit-has-to-be-able-to-ask-for-them-2026-08-20) | apply-time values reach a later unit; **a part has one writer** | found by the first adopter |
| [D35](#d35--the-providers-own-value-does-not-get-to-win-quietly-2026-08-21--amends-d34) | a rule enforced by hand is enforced where you were looking | amends D34 |
| [D36](#d36--a-units-address-is-read-one-way-and-the-wrong-spelling-is-refused-2026-08-21--amends-d35) | an address is read per unit, and the wrong spelling is refused | amends D35 |
| [D37](#d37--the-test-target-disagreed-with-every-real-one-about-what-a-deployment-is-2026-08-21) | the test target shared one store between two deployments | found by disbelieving a sentence |
| [D38](#d38--the-isolation-promise-is-now-a-contract-check-and-the-fixture-declares-the-exception-2026-08-21--amends-d37) | deployment isolation is a contract check now | amends D37 |

---

## D1 — Reconcile against the provider; keep no state file (2026-08-06)

> ⚠ **Overtaken.** Amended by **D7** (run state IS persisted) and superseded by **D12** (there IS one
> set of deployment state). What survives is the reconcile loop and why a stale mirror is affordable —
> read D12 first.

Tyanor records INTENT (a run happened, with this configuration) and reads FACT from the target. It does not
maintain a model of what exists.

**Decided against:** the Terraform model — a local state file holding the tool's belief about the world.

**Why.** A state file is a cache of someone else's database and inherits every problem a cache has: drift
when anything changes outside the tool, locking so two operators cannot corrupt it, and repair-by-surgery
on the tool's private bookkeeping when it does go wrong. A large share of the day-to-day pain in
state-file tools is pain about the state file.

The provider is already an authoritative database of what exists, and it keeps converging whether or not
our process is alive — so the durable state we would be mirroring is one we cannot lose. Dropping the
mirror makes resume a re-run (no second code path to disagree with the first), makes concurrency ordinary
rather than lock-protected, and makes a crash uninteresting.

**Evidence.** Extracted from a deployer that survived a real crash and rebuild mid-deploy and resumed to
completion, in an application a non-technical owner runs unattended.

**Consequence.** `IRunHistory` must never grow a list of created resources — that is the mirror returning
in disguise. Content hashes of the *artifact* are legitimate: they are a fact about our inputs, and nothing
in the provider can make them stale.

---

## D2 — Three failure classes, because there are three responses (2026-08-06)

Credentials → pause · transient → retry then pause · hard → fail.

**Decided against:** a single "failed" outcome with a message, and against a richer taxonomy.

**Why.** The classes exist because each names a *different thing the operator does next*: re-authenticate,
wait, or change the definition. A tool that hard-fails a credential error discards work that is completely
intact. A tool that retries a malformed request tells the same lie five times. A fourth class must arrive
with a fourth action or it is decoration.

**Consequence.** An unrecognised error classifies as `Hard`. That is the safe default: the error nobody
anticipated is exactly the one that must not be silently retried.

---

## D3 — Units are an ordered list, not a graph (2026-08-06)

**Decided against:** a dependency DAG with resolution, diffing and a plan format.

**Why.** The graph is where tools of this kind become large, and it drags in cycle detection, partial-order
execution, a diff engine per resource type, and a plan renderer. Ordering expresses data → compute → edge,
which covers the overwhelming majority of real deployments, and reverse-order teardown then falls out for
free and is always correct.

The property being protected is that a person can read a procedure and know what will happen.

**Revisit when** a real consumer has a real fan-out that ordering cannot express — not when one seems
imaginable.

---

## D4 — Core names no vendor (2026-08-06)

**Why.** The original code's "generic" request type carried `CdkOutDir` and `WebDir` — an AWS tool and an
SPA assumption inside the neutral interface. No second provider could have implemented it, and nobody
noticed, because there was only one. The leak is invisible at one provider and expensive at two.

**Consequence.** Artifacts are opaque named parts; provider settings live in an untyped `Options` map.
The moment `Options` becomes typed fields, it grows one per provider and stops being neutral.

---

## D5 — Tyanor executes a pre-built artifact; it does not synthesize (2026-08-06)

Synthesis (`cdk synth`, `helm template`, a compile) happens earlier, on a machine with that toolchain.

**Why.** It is what lets an operator deploy with no cloud SDK installed. The first consumer's user is a
non-technical owner running a desktop app who has no Node, no CDK and no bootstrap — the deployer ships a
pre-synthesized assembly and drives the provider API directly. Terraform-likes need their whole toolchain
at apply time; not needing it is a real differentiator, and it happened by necessity before it was a
principle.

---

## D6 — No plugin discovery (2026-08-06)

Providers are registered in the consuming application's composition root, not loaded from disk.

**Why.** A deployment tool holds credentials and mutates infrastructure. Loading code it *found* is a
security question nobody asked for, and the convenience it buys — not writing one registration line — is
not worth it.

---

## D7 — Run state IS persisted, at a location the consumer configures (2026-08-06) — amends D1

> ⚠ **Overtaken by D12**, which adds deployment state proper. D7's distinction between a resource
> mirror and a record of intent still holds; its conclusion that we keep only the latter does not.

D1 said "keep no state file". That was **too broad, and as written it was wrong for a library.** It
conflated two different things under one slogan:

- **A mirror of the provider's resources** — Terraform's `tfstate`, a local model of what exists in the
  cloud. Still rejected, for every reason D1 gives. Nothing here changes that.
- **A record of INTENT** — that a run was attempted, with which configuration, and how it ended. Tyanor
  has always needed this: `IRunHistory` existed from the first commit, and without something durable
  behind it a run cannot be resumed after the process dies, which is the engine's whole guarantee.

Shipping an interface with no implementation left the library unusable for its stated purpose. **Where
that state lives is the consuming application's decision, not Tyanor's** — the same shape as configuring a
storage backend in a sibling library, rather than a tool that decides where an operator's records go.

**What ships:** `TyanorOptions.StatePath`, `FileRunHistory` (JSON, atomic write-then-replace),
`InMemoryRunHistory`, and `AddTyanor(cfg => cfg.UseFileState(...))`. A consumer wanting SQLite or a table
in its own database implements `IRunHistory`.

**The default is durable, not in-memory.** An in-memory default would appear to work right up until the
moment resume mattered — which is the moment it is least recoverable and least expected.

**The line that still holds:** run history records what was ATTEMPTED. It must never grow a list of created
resources. That is the mirror returning in disguise, and it is the thing D1 is actually about.

---

## D8 — Terraform's "how", CDK's "what" (2026-08-06)

The positioning, stated so later decisions can be checked against it:

|  | *What* to deploy | *How* to deploy it |
|---|---|---|
| **Terraform** | HCL — a DSL to learn | a real engine: plan, converge, providers |
| **AWS CDK** | real code — typed, refactorable | delegated to CloudFormation; CDK has no engine of its own |
| **Tyanor** | **real C#** | **its own engine** — reconcile, classify, resume |

**Take from Terraform: the mechanics.** Plan before apply, converge rather than script, pluggable
providers, and treat a failure as a state to resume from. Those are genuinely hard and Terraform got them
substantially right.

**Take from CDK: the authoring.** A deployment is code — types, refactoring, tests, one language for the
app and the way it ships. Not a DSL, and not YAML with templating grown into a programming language badly.

**Leave behind:** Terraform's state file (D1/D7) and its resource graph (D3), which are where the size is.

### The boundary this raises, named rather than assumed

"Code-driven *what*" has two possible depths, and Tyanor currently occupies the shallower one:

- **Procedure-level (today).** `Procedure`, `ProcedureUnit`, options and artifacts are C# objects. Typed,
  refactorable, testable, no DSL. This is what D8 claims.
- **Resource-level (NOT today).** Declaring an S3 bucket or a Lambda as a C# object — CDK's actual job.
  That needs a resource model, which needs the graph D3 exists to avoid, and it would duplicate CDK, Bicep
  and Helm rather than execute what they produce (D5).

So the honest line is: **Tyanor's "what" is the procedure; the resource-level "what" belongs to whatever
synthesized the artifact.** If a consumer ever needs resource-level authoring in C#, that is a new
decision to make deliberately — not a drift, and not something to slide into one construct at a time.

**Use this as a test.** When a feature is proposed, ask which column it belongs to. A "how" feature
(retry, classification, resume, progress) belongs here. A "what" feature (a bucket type, a template
language) probably belongs in the tool that produces the artifact.

---

## D9 — Cross-machine is a capability, made safe by visibility rather than by locking (2026-08-06)

> ⚠ **Scoped by D11**: checking is supported, syncing is not.

Running one deployment from more than one place — a laptop and a pipeline, two operators, a retry job — is
**supported**, not prevented.

**Decided against:** a lock or lease that makes a second applier wait or fail.

**Why it is safe without one.** Two mechanisms already in the design, now joined up:

1. **The provider is the arbiter.** Reconcile ATTACHES to a converging unit rather than re-issuing (D1), so
   a second applier watches the first one's work instead of competing with it. This has always been true
   and needs no coordination.
2. **The plan makes it visible.** `PlanAsync` reports both halves — what the provider is doing, and what
   the shared history says anyone *claims* to be doing. Because storage is pluggable (a file today; S3 or
   Postgres later), the second half spans machines for free.

**The signal only shared state can give** is `Plan.HasStalledRun`: a run recorded live with nothing
converging. That means it paused or its process died — possibly on a machine that is not coming back.
Without shared history a second operator sees an idle provider and concludes nobody is here; with it, they
see that applying will RESUME someone else's run, and can decide.

`Plan.InSync` names the agreement directly: the record of intent and the provider either both say work is
happening, or both say it is not.

**Consequence.** `ApplyAsync` with no explicit run id ADOPTS a live run for the same procedure and prefix
rather than opening a second one. Two live records for one deployment is exactly the out-of-sync state the
plan exists to reveal, and the engine should not be the thing that creates it. It also completes "resume
is a re-run": a caller should not have to know whether they are starting or continuing.

**Revisit if** someone demonstrates a case where attaching is genuinely not enough. A lease has real costs
— expiry tuning, clock skew, a stuck lock needing manual clearing — and buys nothing that visibility plus
attachment does not already provide.

---

## D10 — Tyanor is a library, not a service (2026-08-06)

It ships types a developer calls. It is not a daemon, a controller, a CLI, or a hosted pipeline, and it
does not own an application's lifecycle.

**What that requires, concretely:**

- **Every seam is optional and every dependency is opt-in.** `Tyanor.Core` and `Tyanor.Engine` take **no
  package dependencies at all**. `AddTyanor` lives in a separate package (`Tyanor.Extensions.DependencyInjection`),
  so a console tool or a desktop app that has no container is not made to acquire one.

  > ⚠ **Repackaged since, by [D26](#d26--one-library-package-three-in-total-2026-08-19--amends-d10).** The four
  > library packages are one, `Tyanor`, which references `Microsoft.Extensions.DependencyInjection.Abstractions`
  > — the only way to write an extension method on `IServiceCollection`. "No dependencies at all" became a
  > checked BUDGET of exactly that one. Everything else in D10 stands: nothing is ambient, the minimal path is
  > still three lines with no container, and a consumer who never calls `AddTyanor` still configures nothing.
- **The minimal path is three lines**, with no container, no configuration file and no host:

  ```csharp
  var runner = new ProcedureRunner(target, new FileRunHistory("runs.json"));
  var plan = await runner.PlanAsync(procedure, request);
  await runner.ApplyAsync(procedure, request);          // progress callback optional
  ```
- **Nothing is ambient.** No static state, no background threads, no timers, no file watching. A library
  that starts doing things on its own is a service wearing a library's name.
- **The consumer decides the operator experience.** Progress is an `Action<ProgressReport>` and goes
  wherever they send it: a console, a desktop view, a log, nowhere.

**Why.** The first two consumers are a desktop app a non-technical owner runs and a self-hosted service —
neither wants a second process, and each has its own opinion about UI, logging and configuration. A tool
that insists on those is one they would have to fight or fork.

**Consequence for TASKS item 4.** Whatever "authoring a procedure" becomes, it stays a thing the developer
calls. A CLI may be worth shipping as an OPTIONAL package for CI; it must never become the way Tyanor is
used.

---

## D11 — We support state CHECKING, not cross-machine SYNCING (2026-08-06) — scopes D9

> ⚠ **Overtaken by D12.** Divergence between machines is now explicitly the developer's to resolve,
> and Tyanor's job is to show it.

D9 called cross-machine a capability. That is true of what it claims — **visibility** — and it is worth
being exact about what it does not claim, because the two are easy to conflate and the gap is silent.

**Supported today: checking.** With a shared history, a plan reads what any machine recorded — `ActiveRun`,
`HasStalledRun`, `InSync` — and `ApplyAsync` adopts a live run rather than opening a second. Every one of
those is a READ, plus a write of this run's own record. That is enough for an operator to see that someone
else is here and decide what to do.

**NOT supported: syncing.** There is no coordination between concurrent writers. `FileRunHistory` does
read-modify-write with last-writer-wins and no cross-process lock, so two machines writing at the same
instant can lose one of the two records. Nothing detects it, and nothing repairs it.

**Why that is acceptable for now, and where it stops being acceptable.** The damage is bounded to the
HISTORY, never to the infrastructure: the provider remains the arbiter, reconcile still attaches to
converging work, and a lost record costs visibility rather than correctness — the next plan reads the
provider and is right regardless. That trade holds for a handful of operators. It stops holding when
history becomes the thing something automated depends on (a pipeline gating on `HasStalledRun`), because
then a lost write is a wrong decision rather than a missing line.

**So a real S3 or Postgres backend must decide this deliberately**, not inherit the file's behaviour:
conditional writes (S3 preconditions, a Postgres transaction) are the cheap correct answer, and are a
property of that backend rather than a new concept in the engine. Until then, say "checking", not "syncing".

---

## D12 — There IS one set of state, local or remote, kept current and re-syncable (2026-08-06) — supersedes D1, D7, D11

Tyanor maintains a **centralized deployment state**: what it created, per unit, at a location the
developer chooses. `Refresh` re-reads reality and rewrites state to match. A plan reports
**add / change / destroy** from that comparison.

**D1 was wrong, and the way it was wrong is worth keeping.** It reasoned from one provider. CloudFormation
tracks stack membership itself, so for that provider the target genuinely IS the state — and I generalized
a property of the first consumer into a principle. It does not hold: a provider working with raw resources
(an S3 bucket, a DNS record, an IAM role, a Kubernetes manifest) cannot tell you **what Tyanor owns**, and
without that a teardown cannot distinguish what it created from what was already there. That is not a
missing nicety; it is the difference between a safe destroy and a destructive one.

It is the same mistake D4 records in the code this was extracted from — a single consumer's shape mistaken
for a general truth — arrived at from the opposite direction.

**What state is for**, precisely:

1. **Ownership.** What did we create? Answers safe teardown and adoption.
2. **Honest counts.** add / change / destroy, from recorded-versus-refreshed rather than from configuration.
3. **Drift.** Something changed outside Tyanor — reported, and repaired by refreshing.

**The plan protects the DEPLOYMENT, not the state.** This is the framing that keeps the design small. A
plan is a safety gate on real infrastructure: it shows what will be added, changed and destroyed so a
person can decide. It is not a consistency protocol, and Tyanor does not try to make state correct in the
face of concurrent writers.

**Divergence between machines is the developer's problem, deliberately.** Two machines can hold different
state — different application versions, different branches, a deployment out of sync. Tyanor's job is to
SHOW it (`Drift`, `Plan.Summary`, `ActiveRun`); resolving it is the developer's call, because the right
answer depends on facts Tyanor does not have. For the first consumer this does not arise: a customer runs
one version of one application. Building distributed consensus for a case nobody has is how a tool becomes
large, which is the same instinct D3 refuses for the resource graph.

**Still true from D1**, and now the reason the mirror is affordable: the provider remains the arbiter of
what is happening NOW. Reconcile still reads phases live, still attaches to converging work, and a stale
mirror therefore costs a wrong *count*, never a wrong *action*. That is why `Refresh` can repair state
instead of state needing surgery — the failure mode a state-file tool is judged on.

**What ships:** `ResourceState`, `UnitState`, `DeploymentState` (with `Serial`), `IStateStore`,
`FileStateStore`, `IUnitDriver.RefreshAsync`, `StateDiff`, `ProcedureRunner.RefreshAsync`, and drift counts
on `Plan`. State is written per unit AS THE RUN GOES, not at the end: a run that pauses halfway has still
created things, and state that only landed on success would omit exactly what a resumed run needs.

**Resources stay opaque** — id, type, fingerprint, all provider-defined. Tyanor stores identity, never a
resource model, so D3's refusal of the graph survives this change intact.

---

## D13 — The abstraction was tested against a second shape before AWS was ported (2026-08-08)

The first real provider is `Tyanor.Providers.Local` — deploy to a machine: materialize a directory from an
artifact, run a long-lived process out of it, health-check its port, tear it down in reverse.

**Decided against:** porting the AWS provider first, which was the obvious next step and had ~1,000 lines
of already-working code waiting. The contracts had exactly one consumer's shape in them, and hardening them
around the provider they were extracted from is the mistake D4 records — arrived at a second time.

**Why a machine, of all targets.** It is the provider least able to help. It has **no control plane**:
nothing keeps converging after the process that started the work goes away, nothing groups resources by
deployment, and nothing can be asked "what did you create?". Every affordance the engine takes for granted
when it talks to a cloud has to be built here out of a pid and a marker file. If the model survives that,
it is a model; if it only works where CloudFormation is doing the hard parts, it was a description of AWS.

### What it moved

Two contracts, both wrong in the same way — a single consumer's circumstances written into a neutral type.

- **`IDeploymentTarget.ValidateAsync` took a non-nullable `TargetCredentials`**, quietly asserting that
  every target has a key and a secret. A machine has neither; it authenticates as whoever is running the
  process. Now `TargetCredentials?`, where null means the identity is **ambient** — which also covers an
  instance role and an already-selected kubeconfig context, so this was never really about being local.
- **`DeploymentRequest.Options` was flat**, which is enough only when every unit is the same kind of thing.
  Every CloudFormation unit is a stack, so there the unit's name IS its configuration. A machine deployment
  is heterogeneous — a directory here, a process there — so `Option(unit, key)` now reads `"{unit}.{key}"`
  falling back to `"{key}"`. Without a convention in the contract, every provider invents its own.

### What it confirmed, and where the confirmation was a surprise

- **The three failure classes held, including the one that looked cloud-specific.** `Credentials` is not
  about tokens; it is *the provider rejected who we are*, and the operator's move is the same — become
  someone allowed to do this, resume, keep the work. An `UnauthorizedAccessException` on a directory is
  that error exactly. The class was named for expired cloud keys by accident of which provider came first.
- **State (D12) is not a nicety here; it is the only thing that can answer ownership.** A cloud provider
  makes D12 look like a convenience. A machine cannot be asked what belongs to a deployment at all, so
  without the record a teardown cannot tell the directory it created from one that was already there.
- **`UnitPhase`, `Reconcile.Decide`, `IUnitDriver` and the engine needed no change.** No
  `if (provider == …)` exists in Core or Engine — the acceptance test for this work.

### The case that looked like it needed a graph, and did not

To replace the files a server is running out of, the server has to be stopped first. So `service` would
have to come **after** `runtime` (it needs the files) and **before** it (it must stop first). Ordering
cannot express that, and this is precisely the shape of argument that ends in a dependency DAG.

It did not need one. Each build is written to its own directory — `{unit}/releases/{fingerprint}` — so
nothing is ever replaced in place, the restart falls out of the service's fingerprint changing, and the
procedure stays a list of two units applied in order. **What could not be solved by reordering was solved
by changing the operation.** That is a third answer to add to D3's two ("put B first", "they are one
unit"), and it is the one to reach for next time this argument appears.

**Consequence for D3's revisit bar:** this was a real fan-out-shaped constraint from a real second shape,
and it was absorbed without edges. The bar stays where it is.

**Evidence.** 45 tests that copy real files and start real processes, including: a second run attaching to
a server another run started rather than launching a competitor; a health check that never comes green
pausing the run instead of failing it; a hand-edited deployment reported as drift and repaired by applying;
an interrupted redeploy leaving the previous build serving; and a recorded pid whose start time does not
match being refused rather than killed. That last one matters more than it reads — operating systems reuse
pids, and a tool that kills whatever holds a remembered number eventually kills something that has nothing
to do with the deployment.

**What this does NOT claim.** The second *consumer* is still hypothetical: Daoris does not yet self-host
through Tyanor, and Aurelia does not yet deploy through it. What has been proven is that a second *shape*
fits. That is most of the value and it is not all of it — a real consumer will find things a test cannot.

---

## D14 — The AWS port keeps the knowledge and leaves the application behind (2026-08-08)

`Tyanor.Providers.Aws` ships: CloudFormation stacks, and S3 content with a CloudFront invalidation. Ported
from a deployer that ran real infrastructure, survived a crash and rebuild mid-deploy, and tore down cleanly.

**What was left out, which is most of it.** The source was ~2,600 lines. What crossed is the CloudFormation
and S3 mechanics; four things did not, each for a different reason:

- **The reconcile branches, the retry, the classification and the pause/fail mapping.** These were a single
  50-line method beside the SDK calls. They are the engine now, and re-adding them inside a provider is the
  mistake the add-provider skill exists to prevent. This is where most of the deleted lines went.
- **`DeploymentBundler` — the `cdk synth` post-processing.** It rewrites a CDK assembly's asset references
  and injects the operator's stack prefix and certificate. That is **authoring**, and D5 says it happens
  earlier on a machine with the toolchain. Its OUTPUT — a directory of ready templates and asset zips — is
  exactly the `DeploymentArtifact` this provider consumes, so the boundary landed where D5 said it would
  without anything being bent to fit.
- **The domain setup (ACM certificates, Route 53 validation and alias records).** Deferred, not rejected —
  see below.
- **The RDS pre-migration snapshot, the migration verification poll, the SEO prerenderer, and the host
  IPC.** Application policy about one application's database and one application's website. A deployment
  engine that knew about `__EFMigrationsHistory` would have stopped being one.

### The interpretation that carries the most weight

CloudFormation has two statuses one character apart that mean opposite things, and conflating them deletes
a working stack — including its database.

- `ROLLBACK_COMPLETE` is a failed **create**. CloudFormation refuses to update it; it can only be deleted.
  So it is `Broken`, and the action is a replace.
- `UPDATE_ROLLBACK_COMPLETE` is a failed **update**. The stack is back at its previous good configuration
  and is perfectly updatable. So it is `Ready`.

The same distinction decides which statuses are `Unwinding`. `Unwinding` means *it will settle into
something unusable*, so only `ROLLBACK_IN_PROGRESS` and `DELETE_IN_PROGRESS` qualify. Every other rollback
is reverting to a good state and is therefore `Converging` — and the wait reports the rollback itself as a
failure, so an update that reverted is never mistaken for one that shipped. `REVIEW_IN_PROGRESS` is the odd
one out: it ends in `_IN_PROGRESS` and nothing is happening, so attaching to it would hang rather than fail.

A test enumerates `StackStatus` from the SDK by reflection and fails if AWS adds a status this table has not
been told about. That is the check that stops the table rotting the next time the SDK is upgraded.

### Two defects found in code that had run in production

- **Every `AmazonCloudFormationException` was read as "the stack does not exist."** A throttle therefore read
  as absent, and the create that followed would hit a stack that was there all along. Now only a
  `ValidationError` that actually says "does not exist" counts, and everything else propagates to be
  classified — so a throttle is retried as the transient error it is.
- **`"No updates are to be performed"` shares its error code with genuine template errors.** It is matched on
  message text, which this provider otherwise refuses to do, and the match is deliberately narrow: reading a
  real validation failure as "already up to date" is the worse mistake by a wide margin.

### Drift on AWS is CloudFormation-known drift, and that is a limit worth stating

`RefreshAsync` reports stack resources with their CloudFormation status as the fingerprint. It does **not**
call `DetectStackDrift`, which is what would actually detect a resource edited in the console — because that
is a paid asynchronous operation per stack, far too expensive to run on every plan.

So the local provider detects out-of-band drift (it content-hashes what it deployed) and the AWS provider
does not. Rather than hide the difference, say it: on AWS, drift Tyanor reports is drift CloudFormation
knows about, and the place to find the rest is CloudFormation's own drift detection.

### What this does NOT claim

**Nothing here has been run against AWS.** The parts that can be tested without a cloud are — the phase
table and the classifier are pure functions over the real strings, and 91 tests cover them plus the
configuration that is refused before any call is made. The SDK plumbing is a port of working code and is
**unverified in this repo**. The live test exists, deploys a free single-resource stack, and is gated behind
`TYANOR_LIVE_AWS`; until someone runs it, "ported" is the honest word and "working" is not.

Mocking the SDK would not change that. A mock answers the question by agreeing with whatever this code
believes, which is why the gate is a real deployment or nothing.

> ⚠ **Scoped by [D23](#d23--a-fake-cannot-tell-you-what-aws-does-it-can-tell-you-what-we-do-2026-08-18--scopes-d14).**
> The paragraph above is right about the VOCABULARY and was applied too widely. "Does CloudFormation answer
> this way?" still needs a real deployment. "Given that answer, does this provider do the right thing?" is
> our own control flow, and leaving it untested was a gap the rule was being used to justify.

### Deferred deliberately: the domain unit (see also D15)

ACM certificate issuance plus Route 53 validation is the natural third kind, and it maps onto Tyanor's model
suspiciously well — a certificate pending validation is `Converging`, and manual DNS is a
`PauseReason.External` that resumes. That is exactly why it is being left alone: the interesting question is
whether waiting for a human to add a DNS record should pause the whole procedure or only that unit, and the
answer depends on what a real consumer wants to show its operator. Guessing it is how a wrong abstraction
gets built confidently.

---

## D15 — A provider written elsewhere is a first-class provider (2026-08-09)

The seams are public, complete, and shipped with a **contract suite** that any implementation can run to
prove it behaves the way the engine assumes. Writing your own provider or storage backend outside this
repository is a supported path, and passing the contract is what makes adopting one into this repository a
matter of evidence rather than of reading the diff.

**Decided against:** treating the built-in providers as privileged — reachable seams that happen to work for
them, with anything else expected to copy code or read source. That is where most extensible libraries end
up, and it is not a decision anyone makes; it is what happens when the only implementations are in-tree.

**This does NOT reverse D6.** Providers are still registered in the composition root and never discovered
from disk. Authoring a plugin and *loading* one are different questions: a deployment tool holds credentials
and mutates infrastructure, so running code it merely found is a security question nobody asked for. Write
your own, reference it, register it in one line — like the built-in ones, which get no shortcut.

### What two providers made obvious

Writing the local and AWS providers back to back produced three defects that one provider could not have
shown, and all three are the same defect: something that was fine while there was only one of a thing.

- **Two targets could not coexist.** `AddTarget` registered `IDeploymentTarget`, and the runner resolved it
  by type — so registering a second provider silently changed which one deployed, with no way to ask for a
  particular one. The worst kind of bug: undiscoverable, because the plan would be computed against the
  wrong target too and would therefore agree. Now `DeploymentTargets` keys them by id, refuses duplicates,
  and refuses to guess when asked for "the" target with several registered.
- **Both providers hand-wrote the same dispatcher** — a switch on a `kind` option and six one-line forwards.
  Two independent arrivals at one shape means it belongs to the framework. `UnitKindDriver` is that shape;
  a third provider written outside this repository would otherwise have written it a third time and got the
  "you did not say what this unit is" message subtly wrong.
- **Both hand-wrote artifact-part resolution**, down to the three separate failure messages. An operator
  should not get a different sentence about the same mistake depending on where they deployed, so
  `DeploymentArtifact.RequirePart` is now Core's.

### The distinction that keeps errors readable

`DefinitionException` is a base type for "the procedure or the request is wrong" — an artifact part that was
never built, a unit that declares no kind, a cross-unit reference that does not parse.

It exists because a consumer showing a deployment to a person has to tell two situations apart: *you have
configured this wrongly, fix it and nothing is lost* and *AWS said no*. Those read differently, they belong
in different places in a UI, and only one is worth a support conversation. Catching a base type is how that
stays possible without matching on message text.

Providers do not classify these. `IFailureClassifier` returning null is correct and the engine's default for
null is `Hard`, which is exactly what a wrong definition is.

### The contract suites, and why they take no test framework

`Tyanor.Testing` ships `UnitDriverContract`, `FailureClassifierContract`, `RunHistoryContract` and
`StateStoreContract`. They check the things the engine assumes that no signature states: that reading a
phase changes nothing, that removing what is already gone is fine, that an update with nothing to change
says so, that a resource keeps its identity across a refresh, that a wrapped credential error still
classifies, and that a live run record cannot be deleted.

Every one of those is easy to get almost right and fails quietly — as a duplicate deployment, a teardown
that will not re-run, a resume that redoes finished work, or a plan reporting drift that is not there.

They take **no package dependencies**, so they run under xUnit, NUnit, MSTest or a console app. A library
that made you adopt its test framework in order to check your own code would have overreached, and `doctor`
enforces the claim rather than trusting it.

**The suites' first customers are the shipped implementations.** `FileRunHistory`, `InMemoryRunHistory`,
`FileStateStore`, both local unit kinds and the AWS classifier all run them. That is deliberate: a contract
that none of our own code has to satisfy drifts into describing something nobody built.

**What a contract cannot do**, said plainly: it checks behaviour against a real target, so a provider still
needs something real to point it at. The AWS driver contract runs only behind `TYANOR_LIVE_AWS`, for the
same reason the live deployment test does.

> ⚠ **That last sentence stopped being true.** **D30** runs the driver contract for BOTH AWS unit kinds
> offline, against a fake that models existence and nothing else. The sentence was right that a contract
> needs something real to point at, and wrong that "real" had to mean AWS — which is what kept an entire
> driver unchecked. Left as written, with this note, because that is how a reader tells a claim that was
> overtaken from one that was never made.

---

## D16 — The gate goes in front of the destructive direction too (2026-08-09)

> ⚠ **Verbs renamed by D22; the teardown gained a third answer in D32.** What a destroy decides is no longer
> two things but three — take it away, notice it is already gone, or LEAVE one that cannot be removed at all.
> The gate below is unchanged and is what made the third answer cheap: a plan the operator reads before
> confirming is exactly where "this will still be there afterwards" belongs.

Three changes, found by asking what the plan and the driver contract could not express.

### A teardown gets a plan

`PlanAsync` only ever planned an apply. So the operation that DESTROYS things — the one that is not
recoverable by running it again — had no preview at all, while the recoverable one had a good one. A safety
gate covering only the safe direction is not a safety gate.

`PlanAsync(procedure, request, RunKind.Remove)` now reports the units in the order they will actually go
(reverse), which of them are already gone, and every resource the teardown will destroy. `Plan.Destroying`
is kept separate from `Plan.Drift` because they answer different questions and only one of them is a
surprise: drift is *the world moved without me*, and this is *I am about to remove twelve things*. A
teardown that reported its own intent as drift would describe the operator's decision as an anomaly.

It counts what is ACTUALLY there rather than what state once recorded. A resource someone already deleted
by hand is not something this run is about to take away, and counting it would inflate the single number
the whole decision rests on.

`Reconcile.DecideRemoval` is a second pure function beside `Decide`, so the teardown a person was SHOWN and
the teardown that runs come from one place rather than two that can drift apart. It has two answers, and
notably no `Attach`: a unit mid-create is a unit that will exist in a minute, and waiting politely for
someone else's creation to finish before destroying it is a longer teardown with the same ending.

> **Renamed since, by [D22](#d22--the-operator-facing-verbs-are-terraforms-2026-08-18--amends-d16):** `RunKind.Remove` →
> `RunKind.Destroy` and `Reconcile.DecideRemoval` → `Reconcile.DecideDestroy`. Nothing about the reasoning
> above changed — only the words, and only at the operator-facing end.

### Progress has a frame of reference, and it is the unit's

`ProgressReport.Percent` never said whose scale it was on. The engine emitted run-relative numbers; both
shipped providers emitted -1 for everything, which is safe, useless, and why a ten-minute CloudFormation
deploy showed no movement at all. A driver that had emitted its own number would have been read as
run-relative — a unit half done showing as a run half done.

**A driver reports through its own unit; the engine rescales into the run**, weighting by
`ProcedureUnit.Weight` so a ten-minute unit and a ten-second one are not each half the bar. -1 survives as
-1: a driver saying "I cannot tell" must not be turned into a number, which is the one kind of progress
worse than none.

### `IUnitDriver` takes a context, so the next addition is not another break

Only `AwaitSettledAsync` was given a progress callback. That is exactly right for a provider whose work
happens in a control plane it polls, and useless for one whose work happens in `CreateAsync` because there
is no control plane to hand it to. Copying a large directory and waiting out a stack deletion both reported
nothing — and those are the providers D15 promised were first-class.

Fixing it meant breaking the interface. So it broke once, properly: every method now takes one
`UnitContext` carrying the unit, the request, progress and cancellation. The next thing the contract needs
is additive rather than another break for every implementer, including the ones outside this repository.

The context also carries the shorthands both providers had been writing by hand —
`context.Option(key)` for `request.Option(unit.Name, key)`, and `context.Progress(...)` for building a
`ProgressReport` with the unit's name threaded through it.

**Evidence that this was overdue:** the driver contract changed twice in one session before this — nullable
credentials, then progress. Pre-1.0 is when that stops being free.

**What it cost:** a mechanical refactor of both providers, and it dropped a cancellation token on the way
through. The test for "an interrupted redeploy leaves the previous build serving" caught it, which is the
argument for having written that test rather than assuming the ordering held.

---

## D17 — A name is checked where it stops being a label (2026-08-09)

`DeploymentRequest.Prefix`, `Procedure.Name` and `ProcedureUnit.Name` are validated on construction.
`ProcedureUnit.Label` is not, and never will be.

**Why, concretely.** These are not labels. A prefix and a unit name become
`Path.Combine(root, prefix, unit)` in a machine deployment, a CloudFormation stack name, and a component of
a bucket name. Nothing checked them, so a prefix of `"../../etc"` made the local provider write outside its
own root — and made its teardown, which is `Directory.Delete(path, recursive: true)`, do the same.

That is not an exotic misuse. The prefix is documented as *operator-chosen*, the first consumer's operator
is a non-technical owner typing a site name into a field, and a path fragment in a name box is an ordinary
mistake with an extraordinary result. A deployment tool that can be aimed outside its own root by a text
input has a defect regardless of who is expected to fill it in.

**What is refused:** anything but letters, digits, `-`, `_` and `.`; a leading dot; `..` anywhere; blank; and
longer than 255 characters. Traversal is impossible by construction rather than by sanitising, because
sanitising a path is a thing people get wrong repeatedly and refusing one is not.

Two of those deserve their reasons stated. **A leading dot** is refused because the local provider keeps its
bookkeeping in `.tyanor` beside the units, so a unit allowed that name would deploy on top of the pid files
supervising it. **255** is the limit most filesystems put on a single path component — a fact about
filesystems, not about any provider.

### Core checks what is universal; a provider checks its own

`Identifiers` deliberately allows `_` and `.`, which CloudFormation stack names do not, and does not require
a leading letter, which they do. Encoding CloudFormation's charset in Core is the leak D4 is about — so the
gap is the provider's to close, and `StackUnit` closes it: a name AWS would refuse now fails locally with a
sentence naming the problem, rather than as an opaque `ValidationError` after a round trip.

That split is the general rule. Core refuses what would be wrong against every target. A provider refuses
what would be wrong against its own, in its own words.

### Two more things a procedure can no longer be

- **Two units with the same name.** A unit's name is its *address*: the stack `{prefix}-{name}`, the
  directory `{root}/{prefix}/{name}`, its entry in state. Two of them deploy on top of each other and the
  second silently overwrites the first's state, which looks exactly like a unit that quietly stopped
  existing. Compared case-INSENSITIVELY, because on Windows `Api` and `api` are one directory — so a pair
  that looks fine on the machine it was written on collides on the machine it deploys to.
- **No units at all**, and **a weight below one.** An empty procedure applies successfully, reports 100%, and
  deploys nothing, which is the most confusing kind of success. A zero weight makes a unit invisible while it
  runs; a negative one makes the progress bar go backwards.

**Validated on `with` as well as on construction**, which needed the property's `init` accessor rather than
just an initializer — a record's copy constructor writes backing fields directly, so an initializer alone is
bypassed by `request with { Prefix = "../escape" }`. A check that a one-line rewrite defeats is not one.

**Evidence that this was not tightening for its own sake:** all 327 existing tests passed unchanged. Nothing
anyone had written was a name this refuses, which is what a well-aimed check looks like.

---

## D18 — Validate offline, and say what the deployment produced (2026-08-09)

Two capabilities that a Terraform-shaped tool has and Tyanor did not: `ValidateAsync` and `OutputsAsync`, on
`IUnitDriver` and surfaced on `ProcedureRunner`.

### Validate touches nothing, on purpose

`ProcedureRunner.ValidateAsync` checks a whole procedure and request with **no provider access at all** — no
credentials, no network, nothing created — and returns EVERY problem across every unit in one pass.

**What it replaces.** A misconfigured unit used to be discovered by an apply that had already created two
other units, one problem per attempt. Each fix cost another partial deployment. Checking the definition is a
different question from checking the world, and only one of them needs an account to exist.

**Providers must not reach for their API here**, and the reason is the whole value: a consumer can check an
AWS procedure before an AWS account exists. A provider that quietly makes one call turns an offline gate into
an online one and takes that away from everybody.

**The checks are not written twice.** Each provider's `ValidateAsync` runs the same option and artifact
resolution its `CreateAsync` runs and collects the `DefinitionException`s. Two copies of a rule is two rules,
and they diverge the first time one is edited. This is what `DefinitionException` turned out to be for beyond
readable errors — it is the thing that makes an offline check and the real thing provably the same check.

**What it cannot do**, said rather than implied: it does not know whether a template is valid
CloudFormation, whether a bucket name is taken, or whether a quota is reached. Those need the provider. A
valid procedure can still fail to deploy, and pretending otherwise is how a check stops being read.

### Outputs answer "where is my site?"

Nothing did. The AWS provider already read CloudFormation outputs internally — that is how a content unit
finds the bucket a stack made — but no consumer could ask, so an application deploying through Tyanor could
not learn the URL it had just created. The first consumer's entire job is to tell a non-technical owner that
address.

**Read from the provider, not from state.** What a deployment currently exposes is a fact about the
deployment, exactly like a phase. A stored copy is one more thing that can be stale, and the honest answer
to "where is my site" is the one the provider gives now.

**Absent is empty, not an error.** Asking a procedure that is not deployed yet what it produced is a
reasonable question, and a UI that renders "your site is at …" once should not have to guard the call.

### Both are DEFAULT interface members, and that distinction matters

D16 said `UnitContext` makes additions to the driver contract additive. That was true of **parameters** and
not of methods: adding `ValidateAsync` to `IUnitDriver` would have broken every implementation, including the
out-of-repo ones D15 promised were first-class.

Default implementations returning "nothing to validate" and "no outputs" are what made these additive — and
they are correct answers rather than placeholders, because a provider genuinely may have no configuration to
get wrong and nothing to expose. That is the pattern for growing this contract from here: a new capability
arrives with a default that means *I do not do that*.

**Evidence.** A lifecycle test walks the whole operator workflow against the local provider with no cloud
involved: validate → plan → apply → outputs → refresh → plan → new build → plan → drift → repair → plan the
teardown → destroy → plan again. It is the test that says the library works, as opposed to the ones that say
a particular decision is right.

---

## D19 — An application's own step is a unit, not code that runs afterwards (2026-08-09)

`CustomUnits` — a consuming application registers its own `IUnitDriver` implementations as unit kinds inside
a shipped provider, so its steps sit in the same procedure as that vendor's units.

```csharp
var target = new AwsTarget(credentials, new CustomUnits { ["migration"] = new VerifyMigrationUnit(http) });
```

**The case, which was previously homeless.** A real deployment has steps that are nobody's vendor's business:
verify a database migration actually applied, warm a cache, prerender pages, call a health endpoint that
means something only to you. D14 left exactly these in Aurelia and called them "application policy", which
was right about where the code belongs and wrong about where the STEP belongs.

Because to add one, a consumer had to write a whole provider — and then could not mix it with that vendor's
units anyway, since a run is bound to one target. So those steps lived outside the procedure, as code that
ran after it, and got none of what the engine gives:

- **No phase**, so the step re-ran on every deploy whether or not it needed to.
- **No plan**, so nobody saw it coming and no operator could decline it.
- **No classification**, so a transient failure ("the endpoint is not warm yet") ended a deployment that was
  fine, and a credential failure could not pause and resume.
- **No state**, so nothing recorded that it had happened.

As a unit it gets all four, and the only thing it must supply is the one thing the engine cannot guess: a
readable phase. *Has this already happened?* A step that can answer that can be reconciled; a step that
cannot is a script, and belongs outside.

### The classifier comes with it

A custom unit's errors mean nothing to a provider's classifier, which correctly returns null — and the
engine's default for null is `Hard`. Safe, but it means an application's step could never pause. So
`CustomUnits.Classifier` is chained after the provider's, via `FailureClassifiers.Chain`.

That chaining works only because "not mine" is already a real answer in this contract. A classifier
returning null is *passing rather than voting*, so the next one gets its turn and an error nobody claims
still lands on `Hard`. D2's null-means-unrecognised decision paid for itself here, years of design later.

**Order: the provider first**, because it knows its own SDK. A collision is impossible in the other
direction — custom kinds are registered AFTER the built-in ones, so one trying to take the name `stack` or
`directory` is refused rather than shadowing it and silently changing what every existing procedure means.

### Why this is the right shape for "develop it in your app, then ship it here"

This completes what D15 started. A provider written elsewhere was already first-class; now a single STEP is
too, at much lower cost — no new target, no new package, one dictionary entry. A step written in an
application against `IUnitDriver`, passing the contract suites, is indistinguishable from a built-in kind. If
it turns out to be general, it moves into a provider unchanged.

That is the intended direction of travel for everything Tyanor does not yet support: build it where you need
it, prove it with the contracts, and upstream it if it generalizes — rather than waiting for this repository
to guess what you needed.

---

## D20 — Storage is a kind and a connection (2026-08-09)

Where state and run history live is named by one string — `"{kind}:{target}"` — resolved through registered
backends. Terraform's word for this is a *backend*, and it is the same idea.

```
json:/var/lib/myapp/state.json
sqlite:/var/lib/myapp/tyanor.db
postgres:Host=db;Database=ops;Username=tyanor
s3://my-bucket/tyanor/state.json
```

**Decided against** what TASKS item 3 previously described: one package per backend, each with its own
bespoke configuration surface, chosen by an `if` in the consumer's composition root. That works and it makes
every application write the same branch — and it makes storage a code decision when it is obviously a
configuration one. An operations tool whose state location cannot come from `appsettings.json` is one that
needs a rebuild to move.

**The kind is required, and a bare path is refused.** Accepting one would be convenient and would have to
guess. `"sqlite/state.db"` — a slash where a colon was meant — would read as a file called `sqlite/state.db`
and silently write state somewhere nobody intended, which is the single worst outcome available here. One
extra word buys not having to guess. A kind must also be at least two characters, which is what stops
`"C:\ProgramData\state.json"` being read as the kind `C`; it is the rule URI parsers use to tell a scheme
from a drive letter, and a Windows path is the most likely thing anyone types.

**The target is not parsed further.** A connection string full of semicolons is the backend's business and
nobody else's. Core splitting it would be Core knowing what Postgres wants, which is the D4 leak.

### Only one backend ships, on purpose

`json` — a file, registered by default because it is the only one that can be: no package, no server, no
decision on day one. SQLite, Postgres and S3 are **not** implemented here, and that is the point of the seam
rather than a gap in it.

An application that needs Postgres state writes `PostgresStorageBackend` where it needs it, registers it in
one line, and names it in configuration. `StateStoreContract` and `RunHistoryContract` already exist to hold
it to the same behaviour the shipped one meets — including the two that are easy to miss, refusing to delete
a live record and keeping a null fingerprint null. If it generalizes, it upstreams unchanged.

That is D19's path applied to storage, and this is now the third place the same answer has been right:
providers (D15), steps (D19), storage (D20). **Build it where you need it, prove it with the contracts,
upstream it if it generalizes** — rather than this repository guessing which four backends anyone wanted.

**Consequence for concurrent writes,** which D11 and D12 left open: the file backend still has none, and now
there is somewhere for the answer to live. A backend that supports conditional writes checks
`DeploymentState.Serial` and refuses a save derived from state someone else has replaced. That belongs in the
backend, as D11 said, and until one exists the honest word remains *checking* rather than *syncing*.

**Resolution is deferred, not eager.** `UseState("sqlite:…")` records the descriptor and resolves it when the
container builds the store, so a backend registered on the next line still counts. The order of two lines in
a composition root should not decide whether an application starts.

---

## D21 — The pipeline does not need a new authoring model; it needs unit kinds (2026-08-09)

TASKS item 4 asked what a "procedure" should be authored as, on the premise that the brief's pipeline —
restore → build → test → package → publish → deploy → validate — is *broader than deployment units*. It
said not to design it until items 1–3 were done, and warned that the temptation is a DSL.

Items 1–3 are done enough to test the premise, and **the premise is mostly wrong.** Nothing new is needed to
author a pipeline; the phases are unit kinds, and `CustomUnits` (D19) already lets a consumer add them
without changing Tyanor at all.

### Testing it phase by phase

The bar for being a unit is one question: **can it answer "has this already happened?"** A step that can is
reconcilable; a step that cannot is a script.

| Phase | Phase readable from? | Reconcilable |
|---|---|---|
| restore | lockfile hash vs. the resolved assets | yes |
| build | output present and newer than the inputs it was built from | yes |
| test | a recorded pass for THIS input fingerprint | yes, if the verdict is recorded |
| package | the package file exists for this version | yes |
| publish | the registry already has this version — a real remote query | yes |
| deploy | already the whole of this library | yes |
| validate | a recorded pass against this deployment's fingerprint | yes |

All seven fit. Which means a pipeline is a `Procedure` whose units happen to be builds and tests rather than
stacks, applied in order, resumable, planned, classified — and the engine already does every part of that.

**So item 4's answer is: keep authoring in C#.** No DSL, no second model, no `Pipeline` type beside
`Procedure`. That was the answer `units-not-graphs.md` was written to protect, and the exercise of checking
each phase is what makes it a finding rather than a preference.

### The one category that does NOT fit, and it is worth naming

> ✅ **Built, in D32, and it landed where this paragraph guessed.** The gap below is closed:
> `IUnitDriver.IsRemovable` returns false, `DecideDestroy` answers `Retain`, and a destroy plan lists it
> under `Plan.Retained` before anything runs. Left as written because the guess being right is the
> interesting part — the diagnosis was recorded so whoever hit it first would not have to rediscover it, and
> that is exactly what it saved.

**Publish is irreversible.** You cannot unpublish a package version. Today `IUnitDriver.RemoveAsync` must
"remove and wait until it is gone", and `Reconcile.DecideDestroy` gives `Remove` to every phase that is not
`Missing` — so a destroy over a publish unit would call something that must either lie or throw.

Nothing is being added for it yet, on purpose: it is a real gap but not a real consumer's gap, and D3's bar
says a shape is earned by someone needing it rather than by someone imagining it. What is recorded here is
the diagnosis, so whoever hits it first does not have to rediscover it — and the likely answer is a unit
declaring itself unremovable, with a destroy plan reporting it as RETAINED rather than skipping it silently.

### Meanwhile, one thing the pipeline made obvious was already missing

`Procedure.Only(...)` — a procedure narrowed to some of its units, in their original order.

The deployer this was extracted from had a whole dedicated method for one case of it (`SyncFrontendAsync`),
because pushing a website takes seconds and reconciling three stacks to do it takes minutes. D14 left that
method in Aurelia as application code, which was wrong: the *method* was application-shaped, the
*capability* is Terraform's `-target`, and dropping it meant the first consumer losing a real optimisation on
adoption.

Safer here than in Terraform, because there is no dependency graph to skip: a subset of an ordered list is
still ordered, so the units that run keep their relative order and the only thing narrowing can do is leave
something out — which the plan then shows. An unknown name is refused rather than ignored, because a typo
that quietly deploys nothing and reports success is the worst way for this to be wrong.

It narrows a destroy too, and a narrowed run touches only its own units' state — so a targeted apply cannot
quietly forget what it did not look at, which would leave a later teardown unaware those resources exist.

---

## D22 — The operator-facing verbs are Terraform's (2026-08-18) — amends D16

The command set an operator calls is **validate · plan · apply · destroy · refresh · output**, spelled the
way Terraform spells it. `ProcedureRunner.RemoveAsync` became `DestroyAsync` and `RunKind.Remove` became
`RunKind.Destroy`; `Reconcile.DecideRemoval` followed as `DecideDestroy`.

**Decided against:** a vocabulary of our own. It was already half Terraform's by accident — `plan` and
`apply` were never anything else — and half not, which is the worst of both: familiar enough that a reader
assumes they know it, different enough that they are wrong.

**Why.** These name the same jobs Terraform's do, and the audience overwhelmingly arrives knowing that. A
name that has to be translated is a cost paid by every reader forever to save one author an afternoon. The
whole positioning (D8) is "Terraform's mechanics, CDK's authoring" — so the mechanics reading as Terraform's
is the positioning being legible rather than merely claimed.

**Recorded late, and that is the point of recording it.** The rename shipped as a refactor with a changelog
line and no entry here, which left three documents citing `DecideRemoval` and `RunKind.Remove` months after
neither existed. A breaking change to public API is a decision whether or not it felt like one — this entry
exists so the next reader of D16 can tell a renamed thing from a removed one.

### The asymmetry, kept deliberately

A **driver** still says `RemoveAsync`. It removes one unit; it does not destroy a deployment. Terraform has
exactly this split between its `destroy` command and a provider's per-resource delete, and collapsing it
would mean either an operator-facing word on a per-unit contract or a per-unit word on the operator's
command — each wrong in the direction that matters.

The test for the next verb: **does an operator type it, or does a provider implement it?** The first is
Terraform's word; the second is ours.

**The stored value is unchanged.** `RunKind` still serializes as `Apply`/`Destroy` text and `Remove` was
never written to a file by any released version, so no history needed migrating — which is the only reason
this was cheap, and would not be true after 1.0.

---

## D23 — A fake cannot tell you what AWS does; it can tell you what WE do (2026-08-18) — scopes D14

> ⚠ **Sharpened by D30.** This entry is right, and it was applied to the wrong noun: read together with
> D15 it was taken to mean *a cloud provider's driver cannot be contract-tested offline*, which left the AWS
> stack driver held to `UnitDriverContract` exactly never. The line below is drawn per QUESTION — "does AWS
> do that" versus "does our driver do that" — and almost every contract check is the second kind. D30 has
> the table.

D14 said mocking the SDK proves nothing, and used that to leave the AWS driver's behaviour untested. The
first half is true. The conclusion was too wide, and the gap it left was the largest untested surface in the
repository — a provider whose every code path outside two pure functions had never been executed by anything.

**Two questions were being treated as one:**

| Question | Who can answer | Where it lives |
|---|---|---|
| Does CloudFormation report `UPDATE_ROLLBACK_COMPLETE` for a reverted update? | a real deployment | `TYANOR_LIVE_AWS` |
| Given that status, does this provider throw rather than report success? | a fake, completely | an ordinary test run |

The second is not a claim about AWS. It is a claim about **our** control flow, and the bugs it hides are
ours: a request built without its tags, a delete issued against a stack already gone, a throttle mistaken
for absence. A real deployment is a slow, expensive and *unreliable* way to find those — unreliable because
a live test exercises one happy path and none of the failure branches that matter most.

**The rule that keeps this honest: a fake replays, it never invents.** Every status string and error code a
fake hands back is a real one, and the mapping from those strings to a `UnitPhase` is pinned separately by
`CloudFormationPhaseTests`, which enumerates the SDK's own `StackStatus` by reflection and fails when AWS
adds one. So the fake is never the source of a value this provider interprets — it is a recorder of what we
sent and a script of what comes back. That is the difference between testing our logic and testing our
assumptions, and it is the whole of why D14's warning does not apply.

### What it found, and what it cost

Forty behaviour tests, running in under half a second, covering: request construction (name, capabilities,
parameters, tags, `OnFailure`), the staging bucket's per-account name, STS memoization, no-updates versus a
genuine validation error, teardown re-runnability, delete-failed reporting, first-failure-not-last, event
de-duplication across polls, S3 key shape and content types, unchanged-build detection, CloudFront
invalidation and its unique caller reference, and 1000-object delete batching.

**Every one was mutation-checked** — the behaviour was broken deliberately and the suite had to fail. A test
that passes against a fake without ever having failed is decoration, and this whole category is one where
that is easy to ship.

The cost was one line of production code: `StackUnit`'s six-second poll became injectable. Worth naming,
because it is the shape of the problem — the code was *almost* testable, and the one thing standing in the
way made "it can only be tested against AWS" true by construction rather than by necessity. When that
sentence gets said again, check whether it is a fact about the target or about a hard-coded constant.

### Where the line still is

**The STACK driver's `UnitDriverContract` stays behind the live gate**, and this is the boundary worth being
exact about. That suite asks whether a real target behaves the way the engine assumes — create then read,
remove then read, does an id survive. To run it against a fake, the fake would have to become a model of
CloudFormation: create makes it `CREATE_COMPLETE`, delete makes it absent. At that point the fake encodes my
belief about AWS and agrees with the driver by construction, which is precisely D14's objection, correctly
applied.

**The CONTENT driver's does not**, and the difference is the test for where this line falls. S3 has no state
machine to model: the only behaviour that driver uses is that what you put is what you list, and a dictionary
is not a *model* of that so much as the thing itself. There is no belief of mine encoded in it — which is why
it can run offline while the stack one cannot.

That distinction earned itself immediately. The content driver had never been run through the suite at all,
in either mode, and it failed four checks on the first attempt — all one defect: `PhaseAsync` asked whether
the BUCKET existed, but the bucket belongs to the stack that made it and outlives this unit's teardown. So a
destroyed content unit reported itself `Ready` and still owned a resource. Ask the right question of a driver
and it answers; nobody had asked.

So: **fakes for our control flow, a real deployment for their semantics.** The live test is no less necessary
than it was — it is now the only thing left that needs a cloud, rather than the only thing testing the
provider at all.

**This does not license mocking elsewhere.** The local provider tests real files and real processes, and
must keep doing so: it *can* be tested for real cheaply, and a fake filesystem would agree with whatever the
driver believed. The rule is not "fakes are fine" — it is that the untestable-without-a-cloud surface should
be as small as it honestly is, and no smaller claim than that should be made for it.

---

## D24 — Tyanor ships a target to test against, not just suites to test with (2026-08-18)

`Tyanor.Testing` gains `MemoryTarget`: a deployment target whose target is a dictionary.

**The gap.** The package shipped four contract suites — things that VERIFY an implementation — and no
implementation to verify against. So a consumer who had written a procedure, a pipeline, or a deployment UI
had nothing to run it on. Reaching the states their code most needs to handle (a run that paused on
credentials, a unit already converging, a deployment that drifted) meant real credentials, real money and
real minutes, which in practice means those paths are written once and never exercised.

**"Isn't this the mocking D14 refused?"** No, and the distinction is the whole reason it is allowed to
exist: **it is a provider, not a mock.** `LocalTarget` deploys to a machine, `AwsTarget` deploys to an
account, `MemoryTarget` deploys to a dictionary. It never simulates another provider's semantics, so it
cannot teach anyone a wrong belief about one — which is precisely what D14 objected to and D23 scoped.

The proof is not the argument, it is the test: **it passes `UnitDriverContract` and
`FailureClassifierContract`**, the same entry ticket every other implementation buys. A test target that did
not would be worse than none, because a consumer's procedure would pass against it and fail against AWS.

**One deliberate departure from honesty, and it is fenced.** `Phases` reports a unit as `Converging` or
`Broken` whatever is actually deployed. Those are states a real target reaches by timing or by failing, and
a test cannot arrange either. Everything else — create, update, remove, refresh, outputs — is the truth about
what is in the dictionary, which is why the contract passes. When no phase is scripted, the target is fully
honest.

**It replaced a private one.** The engine's own tests had a hand-rolled fake target, written five times over
before it was consolidated earlier in this same pass. Keeping it beside a shipped equivalent would have been
the exact duplication this repository keeps extracting away from — and if the shipped target is not good
enough for our own engine tests, it is not good enough for a consumer's. Same argument as D15's "the suites'
first customers are the shipped implementations".

**What it does NOT do**, said rather than discovered: it hosts one kind of unit, so a `CustomUnits` step
(D19) cannot be registered in it — test one of those against the provider it belongs to, or directly with
`UnitDriverContract`. And it is not safe across concurrent runs, because a test that needs that is testing
the engine rather than using it.

> ⚠ **The first half stopped being true, and D31 says why it mattered.** `MemoryTarget` takes `CustomUnits`
> and has done for some time; six tests drive units through it. The sentence above survived into
> `guide.md` and `adoption.md` as well, so all three told an adopter that the one harness for developing a
> unit before it meets a cloud did not support their unit — which is the opposite of true, and the exact
> thing D19's develop-here-then-upstream loop needs. The concurrency limit is unchanged.

---

## D25 — State answers three questions, and one of them had no code (2026-08-18)

A review against Terraform's state model, asked because Tyanor's is deliberately different: code-driven
rather than DSL-driven, and reconcile-first rather than state-first. Three pairings exist, and Tyanor only
had two.

| Pairing | Question | Where it lives |
|---|---|---|
| config ↔ reality | what should this run DO? | `Reconcile.Decide` — the whole engine |
| state ↔ reality | did the world move without me? | `StateDiff`, `Plan.Drift`, `Refresh` |
| **config ↔ state** | **do I own something the code no longer mentions?** | **nothing** |

**The third is the one being code-driven makes easy to lose.** In Terraform a resource is removed from the
config and the next plan says "1 to destroy", because the plan is fundamentally a diff of config against
state. In Tyanor every pass — the phase read, the drift comparison, the teardown — walks
`procedure.Units`. So deleting a unit from the C# removed it from everything that looks, and whatever it
had deployed went on existing: paid for, unmanaged, and mentioned by no plan in either direction. Deleting
four lines of C# leaked a database.

**Reported, not destroyed**, and this is where Tyanor should differ from Terraform rather than copy it.
Terraform can destroy an orphan because its state holds a resource MODEL — enough to call the provider.
Tyanor's state is deliberately opaque (D12): an id, a type, a fingerprint. The kind, the options and the
artifact parts that would tell a driver how to remove the thing were deleted along with the unit. Inventing
a teardown from a state record means putting a resource model in state, which is the graph D3 refuses,
arriving through the back door.

So `Plan.Orphaned` names what is stranded and leaves the decision where the information is. The way out is
to put the declaration back and run a narrowed destroy — which works today, and is why `Only(...)` narrowing
a destroy (D21) turned out to matter more than it looked.

**A narrowed plan reports none**, which is not an omission but the point: `Only("web")` leaves units out on
purpose, and calling those orphans would make every targeted run noisy — and noise is how a real orphan comes
to be ignored. `Procedure.IsNarrowed` exists for exactly this distinction and nothing else.

### Two more the same review found in the same file

**`Serial` could not do the job it was documented for.** D12 and D20 both lean on it: a backend with
conditional writes compares it and refuses a save derived from state someone else replaced. But `With(...)`
incremented it, so the state handed to `Save` was always one ahead of what the store held — a backend
implementing the check as written would have refused **every** save. `Refresh` was worse: it edits every unit
before saving once, so the number ran ahead by the unit count and no comparison could have worked at all.

A serial has to mean *the version I read*, or the writer and the store are not talking about the same thing.
So `With` no longer touches it and the STORE advances it, which is how an ETag or a row version behaves. The
contract suite asserted the old shape — and would have been satisfied by a store that never advanced the
serial at all — so it was rewritten to assert the property that matters.

**State had no schema version; the run log did.** `FileRunHistory` has a DTO and a comment saying the domain
type must be free to change while an older file still loads. `FileStateStore` serialized `DeploymentState`
directly. So the cheaper file to lose was the protected one, and the file that decides what a teardown may
remove had nothing between a property rename and an unreadable deployment. It now has the same DTO, a
version, and a refusal to half-read a file written by a newer Tyanor.

**What this does NOT change.** State still records identity and never a resource model. The provider is
still the arbiter of what is happening now. `Refresh` still repairs a stale mirror by re-reading rather than
by surgery. D12 stands; this fills in what it left unbuilt.

---

## D26 — One library package, three in total (2026-08-19) — amends D10

`Tyanor.Core`, `Tyanor.Engine`, `Tyanor.Extensions.DependencyInjection` and `Tyanor.Testing` are one package,
`Tyanor`. With the two providers that is three packages, down from six.

Namespaces are unchanged: `Tyanor`, `Tyanor.Engine`, `Tyanor.Engine.State`, `Tyanor.Testing` all still exist,
and folders under `src/Tyanor/` mirror them. Not one line of source or of any sample changed, which is the
first evidence that the split was never doing work — an assembly boundary that can be dissolved without
touching a `using` was not separating anything.

**Why the split existed, and why each reason failed:**

- **Layering.** Core was contracts and pure decisions; Engine did I/O. A real distinction, but not one a
  package can enforce and not one anybody could act on: nothing runs with Core alone, and no third thing
  consumed it. The namespaces still express it, which is where it belonged.
- **Optionality.** Six packages let a consumer take only what they need — except they always needed the same
  four, and got them anyway through transitive references. The choice was theoretical.
- **Zero dependencies.** This one was real, and it is the one thing that changed: merging `AddTyanor` in means
  the library now references `Microsoft.Extensions.DependencyInjection.Abstractions`.

**Decided against: keeping the DI package separate to preserve "no dependencies at all".** It is the only
honest argument for a fourth package, and the property is genuinely nice — Tyanor is embedded in desktop
applications that have no host and no container. But there is no other way to write an extension method on
`IServiceCollection`; the alternatives are reflection over `object`, which throws away the type safety that
is the entire point, or deleting `AddTyanor` and having every consumer hand-write four `AddSingleton` calls.
On `net10.0` the package is a leaf with no dependencies of its own, so a consumer who never calls `AddTyanor`
pays one small assembly. That is a smaller cost than a package whose whole purpose is to hold one file.

**What replaces the claim.** Not nothing — a **budget**. `doctor` now checks that `Tyanor` references exactly
`Microsoft.Extensions.DependencyInjection.Abstractions`, and fails in **both** directions: an unbudgeted
reference means the README overstates how little is needed, and a budgeted reference that is gone means the
budget describes a dependency nobody has. What mattered was never the number zero; it was that no dependency
arrives unnoticed, and that is now checked more precisely than "none" ever was.

**Still absent, deliberately: any test framework.** The contract suites ship in `Tyanor` and must run under
whichever framework the reader already has. One convenient `xunit` reference would quietly break that for
everyone using NUnit — so the budget names it by its absence (D15, D24).

**What this cost.** `Tyanor.Testing` no longer being separately installable means an application ships
`MemoryTarget` and the contract suites it may never call. That is a few kilobytes, and it buys the thing the
suites most needed: a provider author references `Tyanor` to implement `IUnitDriver`, and the suites proving
they got it right are *already there*. A second `dotnet add package` is exactly the friction that stops
contract suites being run at all.

**Revisit if** a real consumer is blocked by the `M.E.DI.Abstractions` reference — which would mean a
platform where it is unavailable, not merely unwanted. Splitting it back out is a mechanical change to one
`ItemGroup`; nothing else would move, because the namespaces never did.

---

## D27 — The public surface is a file, because 0.1.0 is when it stops being free (2026-08-19)

Every shipped assembly's public API is rendered to text and compared against a baseline under
`tests/ApiBaselines/`. A deliberate change is `TYANOR_UPDATE_API=1 dotnet test` and a committed diff.

**Why now.** Before the first release the surface costs nothing to change. After it, a removed member or a
changed signature breaks someone's build. Nothing in this repository could see an API change: the build
succeeds, every test passes, and a `public` that should have been `internal` ships forever — at which point
narrowing it is the breaking change, so it never happens.

**It found two things on its first run**, which is the argument for it better than anything above.

- **`CloudFormationPhases` and `AwsFailureClassifier` were public.** The AWS csproj says, in a comment, that
  the phase table and the classifier "should NOT be public API", and grants `InternalsVisibleTo` to the test
  project for exactly that reason. Both were `public` anyway. The local provider got the same decision right,
  so this was drift between two providers that nothing compared. Now internal — a change that would have been
  breaking one release later.
- **`MemoryTarget` silently absorbed a kind it did not have.** A unit declaring `kind = "discovery"` with
  nothing registered under that name got the memory behaviour instead of an error, while `LocalTarget` and
  `AwsTarget` refuse it and name the kinds they do have. So an adopter who forgot to register their own units
  on a new platform got a green test suite here and an exception in production — precisely inverting what a
  test target is for (D24). **The test written to catch it could not fail:** it never awaited
  `Assert.ThrowsAsync`, so it asserted a `Task` was non-null.

Both are the shape this repository keeps finding: **behaviour defined by absence**, guarded by a check that
cannot go red. The surface baseline is not a cleverer test, it is a check that turns absence into a line in a
diff.

**Decided against: a rule instead of a record.** It would have been easy to make this fail on any *new* public
member and demand justification. That is the wrong instrument — additions are how a library grows, and a check
that cries at every one gets suppressed. The baseline never says a change is wrong; it says a change happened,
to a reviewer who can tell. **The reviewer was the part that was missing, not the opinion.**

**What it deliberately does not catch.** Nullable reference annotations (reflection does not carry them
usefully, so `string` → `string?` is invisible), and record boilerplate is skipped so a compiler upgrade does
not read as an API change. It is a net, not a proof — and worth saying out loud, because a check believed to
be exhaustive is worse than one known to have holes.

**Per assembly, not per repository**, so a provider's API change fails the provider's own test project — and
so a provider written outside this repo can copy `tests/Shared/ApiSurface.cs` and get the same thing (D15).

---

## D28 — .NET 10 only, and it is a floor rather than a limit (2026-08-19)

The packages target `net10.0` and nothing else.

**Not because they need it.** The newest APIs anywhere in the source are `.Order()` (net7) and
`ArgumentException.ThrowIfNullOrWhiteSpace` (net8). Adding `net8.0` to the target list is a one-line change
with no conditional compilation — this was checked before deciding, so the decision is about what to support
rather than about what is possible.

**Decided for: one framework, while there is one consumer.** Tyanor's first adopters are the applications it
was extracted for, and they are on .NET 10. Multi-targeting costs a doubled build matrix, two `lib/` folders
per package, and — the part that actually bites — two behaviours to reason about the first time a framework
difference matters. Paying that for a consumer who does not exist is the same mistake as building a storage
backend nobody asked for (item 3 in `TASKS.md`).

**What it costs, honestly.** `net8.0` is LTS until November 2026 and is where most production .NET sits, so
this excludes a third party who wants to write a provider against `IUnitDriver` — which is a real cost against
D15, not a hypothetical one. It is accepted for now because the D15 claim is currently proved by providers
inside this repository, and no outside implementer has been turned away.

**Revisit when the first person is.** The trigger is specific: someone wanting to write a provider or a
storage backend who cannot reference the package. Not a survey of framework adoption — a person. The change is
a line in `Directory.Build.props` plus a build matrix, and the API baselines (D27) are what would catch a
surface that accidentally differed between the two.

**Said out loud in the README and the guide**, because the alternative is an adopter discovering it from a
restore error.

---

## D29 — A sync converges in both directions, and a unit owns what it fills (2026-08-20)

The AWS `content` unit now removes objects the build no longer produces. It did not, and the way that
failed is the reason this is a decision rather than a fix.

**The defect.** A sync uploaded every local file and stopped. Its change check asked *is every local file up
there?* — which a bucket holding those files **plus every page the build had stopped producing** satisfies.
So the update reported no change, a plan called the deployment current, and a deleted page went on being
served. Permanently: a phase read only asks whether the bucket has anything in it and a refresh only counts,
so nothing else in the system was ever going to look. The local provider had pruned its own stale files from
the first commit, which is what made this visible — one concept, two providers, one implementation.

**Decided for: converge.** A unit is a description of a desired state, and *the files that are there* is as
much part of that as *the files that are new*. Reconcile is already the whole model; a sync that only ever
adds is not reconciling, it is appending.

**Decided against: making the removal opt-in**, which is what `aws s3 sync --delete` does. That is the right
default for a general-purpose file-copying tool, where the destination is somebody else's directory. It is
the wrong one here, because this unit already claims the bucket in three other places — a removal empties
the whole thing, a phase read calls an empty bucket `Missing`, and a refresh reports an empty bucket as
owning nothing. An opt-in flag would have made the unit's ownership depend on which of four behaviours you
had configured, when the other three were never optional.

**What it costs, and the guard that buys it back.** Pruning introduces one new way to lose a site: a build
step that quietly produces nothing now empties a live bucket instead of harmlessly re-uploading nothing. So
a build with no files at all is **refused** — an empty directory does describe an empty site, but not
plausibly enough to spend a website on. This is the same species as `RequirePart`'s "Build first.", one step
further in: the part exists and is a directory, and is still not a build.

**The asymmetry with the local provider is real, not an inconsistency.** `DirectoryUnit` prunes stale files
with no such guard, because there each build lands in its own release directory and the marker only moves
once the copy has finished — an empty build costs one unused release and leaves what is serving untouched.
There is one namespace on S3 and it is the one being served. The guard belongs where the blast radius is.

**What this does not claim.** Files are still compared by name and size, never by content, so an edit that
preserves a byte count is still invisible; comparing bodies means downloading them. That limit was already
stated and is unchanged — what changed is that a file's *absence* is now noticed, which is a different
question and was answered wrongly rather than approximately.

**The generalizable part**, for a provider written elsewhere: if your unit removes everything on a teardown,
it owns the namespace, and a sync that does not prune is claiming otherwise on the one path where nobody
checks.

---

## D30 — "Only against the real thing" is a claim about the QUESTION, not the provider (2026-08-20) — scopes D15, D23

The `UnitDriverContract` runs against both AWS unit kinds **offline**. It previously ran against the stack
driver only behind `TYANOR_LIVE_AWS` — which, since nothing from this repository has ever reached AWS, meant
it had never run at all.

**How it hid.** D15 said a contract "checks behaviour against a real target, so a provider still needs
something real to point it at", and concluded the AWS one runs only behind the live gate. D23 said a fake
cannot tell you what AWS does. Both are true. Put together they read as *the AWS driver cannot be
contract-tested offline*, and that conclusion follows from neither — but it was load-bearing for a year of
`doctor` runs reporting "4 unit kinds, each held to 2 contract suites", because the check counted a gated
file as coverage. **Two correct statements composed into a wrong one, and nothing could see it.**

**The distinction that resolves it.** D23's line is drawn per QUESTION, not per provider:

| The question | Who can answer | Where it lives |
|---|---|---|
| Does CloudFormation settle a create into `CREATE_COMPLETE`? | a real deployment | `TYANOR_LIVE_AWS` |
| Will AWS accept the request we build? | a real deployment | `TYANOR_LIVE_AWS` |
| Does a phase read change anything? | our code | offline |
| Does removing twice throw? | our code | offline |
| Does an update over an unchanged deployment report no change? | our code | offline |
| Do outputs stop answering once the unit is gone? | our code | offline |

Every check in `UnitDriverContract` is in the lower half. None asks what AWS does; all ask what our driver
does when handed an answer AWS really gives. Reading D23 as *a cloud provider cannot be contract-tested*
applies the right rule to the wrong noun.

**What makes the fake safe.** `StatefulCloudFormation` models exactly two things — a created stack can be
described, a deleted one cannot — and every value it returns is a real CloudFormation string. It models NO
rollback, no `UPDATE_ROLLBACK_FAILED`, no `REVIEW_IN_PROGRESS`, no timing, no drift, and no opinion about
whether a template is valid. That is deliberate and is the whole safety property: a fake that cannot express
CloudFormation's interesting behaviour cannot accidentally start certifying it. The mapping from status
strings to phases stays where it was, pinned against the SDK's own enumeration.

**Decided against: leaving it gated and calling the gap "the last mile."** The last mile is supposed to be
what only a cloud can answer. Nineteen checks about our own control flow are not that, and calling them that
made "we are covered apart from the AWS call" an overstatement nobody could measure.

**The check now enforces it.** `providers.mjs` no longer counts a contract suite constructed inside a file
that reads an environment variable. A gated run is a promise about a run nobody has done; deleting the
offline suite makes the check say so by name.

**The lesson worth keeping**, because it is not about AWS: when two true constraints compose into "this
cannot be tested", the composition is the thing to check. `TASKS.md` already carried the warning — *when "it
can only be tested against the real thing" gets said again, check whether it is a fact about the target or
about a hard-coded constant* — and this is the same failure with a different ending: a fact about the
**question**, mistaken for a fact about the provider.

---

## D31 — The seams are the product, so their cost is a feature (2026-08-20) — amends D24

Tyanor cannot ship a provider for every service on day one. An adopting application has to be able to build
what it needs **ahead of us**, run it in production, and hand it back if it generalizes — so the seams are
not a nicety around the engine, they are the thing being shipped. That reframes what counts as a defect in
one: not only *is it possible*, but *what does it cost*, and *can somebody find out they got it wrong*.

Reviewed against that, four things were wrong, and only one of them was a missing feature.

**A capability that was documented and unreachable.** `PauseReason` has always said a provider or a
procedure may introduce its own reason — a DNS validation pending, a manual approval gate. Nothing could:
the engine produced `credentials` and `transient` from the three failure classes and `external` only on
cancellation, so no driver could cause one. The deferred ACM/Route 53 unit's entire model is "manual DNS is
a pause that resumes", and an adopter writing it first would have found the door painted on.
`UnitPausedException` opens it.

**Decided against a fourth `FailureClass`.** The three classes are a provider's reading of an ERROR, and
each answers "what should the operator do next" (D2). A pause is not an error: nothing went wrong, the work
already done is correct, and what is missing is a person or the passage of time. A fourth class would have
made every classifier's switch wrong — including the ones written outside this repository — to describe
something no classifier is looking at. It is also never retried, excluded in the engine rather than trusted
to each classifier, because asking somebody to approve a release five times in four seconds is worse than
not asking.

**A seam with no contract.** D20 says write the backend you need and hold it to the suites — but
`StateStoreContract` and `RunHistoryContract` cover the stores a backend OPENS, not the backend: reading a
descriptor, answering to a kind, keeping two locations apart. So the part that is actually the adopter's was
the part nobody could check. `StorageBackendContract` composes the other two, so passing it means both the
resolution and the stores are held. A backend that genuinely cannot do one half says so with
`NotSupportedException` and the suite accepts it — but not for both, because something storing neither is
not a backend and a contract satisfiable by refusing everything is worse than none.

**A cost nobody had counted.** `IUnitDriver` has six required methods because a unit that deploys
infrastructure needs six. A step — a check, a gate, a migration — needs two, and writes the same four
one-line stubs. That had happened in six places here, including the worked example `adoption.md` puts in
front of somebody adopting for the first time. `StepUnitDriver` is those four defaults plus the two the
interface leaves as default members, which an editor will not offer as an `override`. The saving is small
per unit and the point is the FIRST one: four more chances to return null instead of empty, and four more
reasons for a step to stay a script that runs after the procedure instead of a unit inside it.

**And one that was simply false.** D24 said `MemoryTarget` hosts one kind of unit, so a `CustomUnits` step
cannot be registered in it. It takes `CustomUnits`, six tests drive units through it, and the sentence had
been copied into `guide.md` and `adoption.md` — so all three told an adopter that the only harness for
developing a unit before it meets a cloud did not support their unit. **The worst kind of documentation
defect: not out of date, but confidently wrong about the capability the reader came for.** D19's whole
develop-here-then-upstream loop runs through that harness.

**The standing question this adds**, beside the one in `CLAUDE.md` about what an implementer would have to
copy: *how many methods does the smallest useful version take, and what happens when they get one wrong?*
The first answer should be small, and the second should be a contract check rather than a silent
misbehaviour in production.

---

## D32 — A teardown has three answers, because some things do not come back (2026-08-20) — amends D16

A unit may declare itself unremovable. `Reconcile.DecideDestroy` then chooses `ReconcileAction.Retain`, the
plan lists it under `Plan.Retained` before anything runs, the teardown leaves it and says so, and its state
is kept.

**The case, and why it was deferred until now.** `TASKS.md` item 4 has carried this since the pipeline
analysis: a publish is irreversible — you cannot unpublish a version — and yet `IUnitDriver.RemoveAsync` is
documented as "remove and wait until it is gone", so such a unit could only lie or throw. It was left
unbuilt on D3's bar: a shape is earned by someone needing it, not by someone imagining it. The note even
predicted the answer, which is a good sign it was the right thing to defer rather than the wrong thing to
skip.

**What earned it was a change in what Tyanor is FOR, not a new opinion about publishing.** Tyanor is a
code-driven CI/CD framework as much as a deployment library; it cannot ship a provider for every service on
day one, so an adopting application builds the steps we do not (D31); and publish is one of the seven phases
in the original brief. A pipeline framework whose own publish step cannot be expressed is not one.

**The three answers, and why the third is not the second.** Take it away, notice it is already gone, or
leave one that cannot go. Collapsing `Retain` into `Nothing` would have been the cheap version and it is
precisely wrong: `Nothing` means *there is nothing there*, and `Retain` means *there is, and it is staying*.
A teardown that reported success over a still-published version — or over an audit record, a sent email, a
charged invoice — would be lying in the direction that costs money and cannot be undone. So it is said out
loud on every run, and it appears in the plan before anyone confirms anything.

**Decided against: letting the driver throw.** That was one of the two shapes available before, and it
fails a teardown that has nothing wrong with it, every time, forever — turning a stated property of the
thing into an operational fault the operator has to work around by narrowing the procedure.

**Decided against: a new `UnitPhase`.** Removability is a fact about what the unit IS, not about what is
true of it right now. A phase saying "permanent" would have to be returned by a `PhaseAsync` that is also
answering "does this exist yet", and the two questions have different answers at different times.

**Not a permission check.** *I may not delete this today* is a credential failure and belongs in
`IFailureClassifier`. This is about the nature of the thing, which is why it is a property of the driver
rather than an error from an attempt.

**Its state is KEPT, and that is the half most likely to have been got wrong.** The obvious implementation
clears state for every unit a destroy walks. For a retained unit that would make Tyanor forget it owns
something still out there — which D25 identifies as the worst state can do, because an unowned resource is
one no future plan ever mentions again. `RetireAsync` now reports whether it actually removed anything, and
only that clears.

**The contract had to grow with it, or the shape would be untestable.** `UnitDriverContract` asks a
removable unit to disappear after a remove; an irreversible one cannot satisfy that, and demanding it would
mean the shape that most needs a contract is the one shape that cannot have one. The pair splits: one is
held to disappearing, the other to SURVIVING and to not having mislabelled itself. Exactly one applies to
any driver, and `ContractSuiteTests` pins that a driver claiming to be permanent and then vanishing is
caught.

**One C# rule this surfaced, worth writing down.** `IsRemovable` is a default interface member, and a class
that implements `IUnitDriver` fixes the interface mapping at itself — so a SUBCLASS declaring its own
`IsRemovable` does not change what the engine calls. It compiles and silently does nothing. That is the same
rule `StepUnitDriver` restates `ValidateAsync` and `OutputsAsync` for (D31), and it caught a test double in
this very change. Any base class offering a seam should restate the interface's defaults as `virtual`.

## D33 — A provider owns infrastructure too, and until now nothing removed it (2026-08-20)

`IDeploymentTarget.SweepAsync` — defaulted to doing nothing — is called once a FULL destroy has finished
with every unit, so a provider can remove what it created for its own use. The AWS provider deletes the
staging bucket it uploads templates and assets through; the local provider deletes the `.tyanor` folder of
pid files and the deployment folder itself, if nothing else is in it.

**Found by the first adopter, not by a review.** Their spike read `StackUnit.cs:286` creating
`{prefix}-deploy-{account}`, ran `grep -rn "DeleteBucket" src/`, and got nothing at all — no unit, no
target, no dispose path. So a destroyed deployment left a bucket standing holding every template and Lambda
asset it had ever uploaded, while `adoption.md`'s checklist promised "a destroy of that prefix leaves
nothing". The deployer this was ported from empties and deletes it, so this was a regression against the
thing that was ported and not merely an unbuilt feature.

**It is not a missing call, and mistaking it for one is how it would have been fixed wrongly.** The obvious
patch is for the stack driver to delete the bucket in `RemoveAsync`. That is precisely the reach-sideways
this project refuses: every stack in a procedure stages through the same bucket, so the first unit removed
would take away what the units after it still need — the exact failure removing in reverse order exists to
make impossible (`units-not-graphs.md`), and the same reason the content unit empties its bucket without
deleting it. No unit can own it. So the gap was never a missing delete; it was that **provider-owned
infrastructure had no lifecycle at all, and a destroy had no phase in which to sweep it.**

**Twice is the signal, and it was already twice.** This looked like an AWS problem and is not: the local
provider keeps pid files under `{root}/{prefix}/.tyanor`, deliberately outside the unit directories so that
removing a unit removes exactly what was deployed and none of the provider's — which left nobody able to
remove the provider's. Two shipped providers, the same shape, neither able to solve it inside a unit. That
is the bar `CLAUDE.md` sets for adding a seam rather than a workaround.

**Decided against: `IDisposable` on the target.** It fires when the composition root feels like it rather
than when a deployment ends. A bucket deleted because a process exited is a bucket deleted out from under a
run that paused — and a resumed apply would then fail every remaining unit for a reason nothing in the
history explains.

**Decided against: a unit that owns the staging bucket.** It would have to be first in apply order and last
in teardown, which is a dependency edge in everything but name (D3), and it would appear in every plan and
every count as a resource the operator never declared.

**Decided against: putting it in the plan.** A plan counts UNITS and the RESOURCES they own; a provider's
own scaffolding is neither, is in no state store, and the engine cannot ask a target what it *would* sweep
without a second method that exists only to describe the first. A boolean saying "a full destroy also
sweeps" would have been true of every full destroy including those against targets that sweep nothing —
information-free. It is documented instead, in `providers.md` beside the bucket it concerns.

**A narrowed destroy never sweeps**, and that is the constraint the design is actually shaped by.
`Only("web")` is a partial teardown by request: the units left out are still deployed and still need the
scaffolding. The engine knows the difference and a provider does not, so the decision stays in the engine —
`ProcedureRunner` checks `Procedure.IsNarrowed`, the same signal `Plan.Orphaned` uses to stay quiet, and it
is refused on the NARROWING rather than on what the narrowing happens to contain. Narrowing to every unit
still does not sweep: a rule about the shape of the request cannot be got wrong by someone adding a unit
later.

**A sweep that fails does not fail the teardown, and is said loudly.** Every unit is already gone, so the
destroy did what it said; failing the run would send an operator to re-run a teardown with nothing left to
remove. But silence would be exactly what D32 refuses — a teardown reporting success over something still
out there — so the engine reports the provider's own message with `ProgressStatus.Error` and succeeds. This
is the one place the engine swallows an exception on purpose, and it is worth being uneasy about; the
alternative is worse in both directions.

**Some units may be RETAINED when it runs.** An irreversible unit is one a teardown will never take away
(D32), so waiting for it would mean never sweeping at all. The provider is told this and is the only thing
that could know whether its scaffolding is still needed.

**The contract grew, because a seam only this repository can implement is not a seam (D15).**
`DeploymentTargetContract` holds a target to the two promises a sweep satisfies by OMISSION — tolerating
nothing to sweep, and surviving a second run — which is the shape of defect that passes every other test a
provider has. It is in `doctor`'s enforced list, so both shipped providers run it ungated, which is what
required an internal constructor on `AwsTarget` taking its clients. That is D23 applied to the target the
way it was already applied to the drivers: *which* bucket and *when not to fail* are our logic, and only
whether S3 accepts the calls needs a cloud.

**What the contract deliberately cannot check, and is therefore written down.** That a sweep is scoped to
the deployment it was handed. A sweep that removed staging for every prefix in an account passes both checks
and destroys a deployment nobody asked it to touch. Both providers are tested for it specifically — a second
prefix left standing beside the one torn down — but a generic suite cannot see it, so the suite says so
rather than implying coverage it does not have.

**One test wrote itself green and had to be caught by mutation.** The local check that a deployment folder
holding anything else is LEFT passed with the emptiness guard removed: `Directory.Delete` throws on a
non-empty folder, the engine swallows a failing sweep, and "left deliberately" looked identical to "left
because the delete blew up". It now asserts no error line was reported. Same shape as D27's two findings —
behaviour defined by absence, guarded by a check that could not go red.

## D34 — Some values only exist once the run is under way, and a unit has to be able to ask for them (2026-08-20)

Three things a unit could not reference, from one adoption pass and one root. `parameterFrom.{Name}` takes
`"{unit}:{OutputKey}"` and resolves at apply time. `assetsBucketParameter` names the CloudFormation
parameter to fill with the staging bucket Tyanor actually uploaded to. And **an artifact part has exactly
one writer, which is the unit that owns it** — written down here because it was written down nowhere.

**The shape was already decided; it just had not been applied twice.** `bucketFrom` and `invalidateFrom`
resolve `"{unit}:{OutputKey}"` at apply time, which is how an ordered list carries a dependency without an
edge (D3): declare the producer first, resolve when the run reaches the consumer. `parameter.*` had no
equivalent and was verbatim text, so a stack could not consume what an earlier unit produced. That blocks
**item 1's domain unit before it blocks any consumer** — an ACM certificate ARN exists only once the run has
issued it and has to be inside the CloudFront distribution before the web stack deploys.

**Decided against: making the domain unit rewrite the template part.** That was the obvious workaround and
it is the one that breaks two things at once. The artifact is the handover from a build that already
happened (D5) and is resolved at request time, so a unit editing a part makes it mutable mid-run and makes
any plan already shown stale about a unit that is not the one being edited.

**Decided against: doing it in Core.** *A value resolved at apply time reaching a later unit* is a general
question, and `UnitContext` could grow a way to read another unit's outputs — every driver already has
`OutputsAsync`. It is not built because the engine would have to thread a resolver into every context and
decide what that resolver does during `ValidateAsync`, which touches no provider at all. The bar is the one
D19 and D20 set: build it where it is needed, and upstream it when a **second provider** needs it. This is
twice in one provider, which earned the extraction below and not a Core seam.

**Extracted rather than copied: `OutputReferences`.** `bucketFrom` and `invalidateFrom` shared a private
parse-and-resolve inside `ContentUnit`; `parameterFrom.*` is the third caller, and a third copy would have
been the first to word its refusal differently — the same defect `UnitContext.RequirePart` was extracted to
fix one level down. Twice is the signal; this was already twice before it was moved.

**A parameter set both ways is REFUSED, not resolved by precedence.** Which one an operator meant is not
knowable from the request, and picking one silently deploys a value nobody wrote down. Refused offline by
`ValidateAsync` and again at apply time, because two copies of a rule must not be able to disagree.

> ⚠ **Written here as a rule and applied to two of its three pairs.** `assetsBucketParameter` naming a
> parameter already set overwrote it in silence until **D35**, which is this paragraph finished.

**An unresolved reference is a hard failure at apply and a null during a plan.** The asymmetry is
`bucketFrom`'s and it is deliberate: planning a deployment that does not exist yet resolves nothing, which is
a legitimate answer. Reaching an APPLY with nothing is a definition problem, and passing the parameter
through empty would fail inside CloudFormation naming the parameter rather than the unit that was supposed
to produce it — sending the operator to read a template instead of their procedure.

**The staging bucket is passed, not published.** It had no public existence at all, so a CDK-style template
that must name the bucket its Lambda code sits in left the first adopter hard-coding
`{prefix}-deploy-{account}` **and making an `sts:GetCallerIdentity` call of their own** purely to fill in a
parameter value. Three answers were available — a documented-and-frozen convention, a public helper plus an
account accessor, or the provider filling the parameter itself. The third is the only one where the value
and the upload **cannot disagree**, because they come from one call; the other two leave a consumer
recomputing a convention this provider owns and could move.

**Nothing is passed unless the parameter is named**, because CloudFormation refuses a parameter the template
does not declare — a bucket supplied helpfully would break every template that did not ask for one. And it
is one setting rather than a family: `AWS::Region` and `AWS::AccountId` are already CloudFormation
pseudo-parameters, so the staging bucket is the only fact about a deployment that only Tyanor knows.

### An artifact part has one writer

**The question was open and undocumented**, which is worse than either answer: whether a part may be mutated
between units appears in none of `guide.md`, `adoption.md`, `providers.md` or this file. The adopter found
it by needing it — they bake per-route HTML and a sitemap into the web dist between "the API stack is up"
(it needs the live API URL) and "the files are synced", and as a `StepUnitDriver` that step's `PhaseAsync`
would be a latch on a directory the `content` unit owns, so its `RemoveAsync` would delete files out of
another unit's source.

**The rule: a part is a path, a unit may write to a part it OWNS, and no part has two writers.** Writing is
not forbidden — a step that produces files is a perfectly good unit, and forbidding it would push real work
back out into the scripts units exist to replace. What is forbidden is writing into a part another unit
reads as its source.

Both halves follow from it. The latch problem dissolves: give the preparing step its own output part, and
its phase latches on its own files and its remove clears its own files. And the plan stays honest: a unit
whose source changed after the plan was shown would be reported as unchanged and then deploy, which is the
plan lying about a unit that is not the one that moved.

**It is the same principle as `OwnOption`, arriving through a different door.** A unit's address must be its
own, because a shared one means every unit deploying on top of every other. A unit's output is its address
in file form.

**Not enforced, and that is stated rather than hidden.** Core cannot see that two units point at one
directory without a pass over the whole procedure comparing resolved paths — and a legitimate case reads
identically: one unit writes a part and a LATER one reads it, which is exactly how the answer above works.
So this is a review rule, like the namespace boundary since D26. If it is got wrong the symptom is specific
and worth recognising: a plan that says "no change" for a unit that then redeploys every run.

## D35 — The provider's own value does not get to win quietly (2026-08-21) — amends D34

D34 said a parameter set two ways is refused rather than resolved by precedence, and then resolved one pair
by precedence. `assetsBucketParameter` names a CloudFormation parameter for the provider to fill with the
staging bucket; `ParametersAsync` built the map, resolved the references into it, and finished with
`parameters[named] = bucket` — unconditionally. So of the three ways one key can be set, two collided loudly
and the third overwrote without a word.

**Reported by the first adopter, against the seam that had just closed their workaround.** That is what
makes it worth an entry rather than a line in the changelog: the defect is ON the upgrade path. Anyone who
hand-computed `{prefix}-deploy-{account}` before 0.1.1 already has `parameter.AssetsBucketName` set, and the
natural edit is to add `assetsBucketParameter` beside it rather than instead of it.

**Decided against: letting the bucket win, and saying so in the documentation.** It is the tempting answer
because here the provider's value is *right* — it is the bucket the upload actually went to, and the
hand-computed one is at best the same string. But "the provider knows better, so it overwrites what you
wrote" is precedence with a good excuse, and precedence is what D34 refused when it could not tell which
value was meant. The operator who wrote the other line believes it is being used. Two lines, one of them
dead, is a question worth asking them; it costs one error message and they delete a line they no longer need.

**Decided against: warning and continuing.** A warning is what a rule becomes when nobody wants to enforce
it. This one is a definition problem, offline-detectable, with a one-line fix.

**A collision is now refused BEFORE any reference is resolved.** Resolving first meant an operator who both
collided a name and mistyped a producer was told whichever the option order happened to reach — and the
collision has to be fixed either way. The test for it writes the unresolvable reference first deliberately,
because with the options in the other order the weaker implementation passes too.

**One function produces the sentences and both callers use it**, which is D34's own "two copies of a rule
must not be able to disagree" applied to the wording as well as the rule. There were already two
near-identical sentences for the one pair; three pairs would have made six.

> ⚠ **Taken at its word by D36**, which went looking for other rules enforced by hand and found the same
> defect twice more — an address read the shared way in one provider and dropped in the other.

**The shape, which this repository keeps finding.** The rule was written down, applied where it was being
thought about, and not applied one line further on — like D33 (both providers owned infrastructure and
neither removed it) and D27's two findings. What is generalisable is not "check parameter maps": it is that
a rule stated in prose and enforced by hand at each site is enforced at the sites you were looking at.

## D36 — A unit's address is read one way, and the wrong spelling is refused (2026-08-21) — amends D35

`DeploymentRequest.Address(unit, key)` reads a setting that IS a unit's identity — its path, its bucket, its
port — per unit only, and REFUSES a procedure-wide one instead of sharing it or dropping it. Both shipped
providers had an identity-bearing setting read the wrong way, in opposite directions, and neither said
anything.

**Found by taking D35's own lesson seriously rather than by a report.** D35 ended by saying the
generalisable part was not "check parameter maps" but that *a rule stated in prose and enforced by hand at
each site is enforced at the sites you were looking at*. The rule "an address is read per unit" was written
in `OwnOption`'s documentation, in `LocalOptions`, in `providers.md` and in the 0.1.0 changelog, and it was
applied at exactly one of the three settings that needed it. `OwnOption`'s own doc comment names **"which
bucket it fills"** as its example, and `aws.bucket` was read with the shared reader.

**Two defects, measured before they were fixed.**

- **AWS shared it.** Two content units and one unscoped `bucket` sent both to the same place. A sync makes
  the bucket BE the build, so the second unit pruned the first: `after site: [index.html]` →
  `after docs: [guide.html]`, `deleted: [index.html]`. A website deleted by deploying a different unit.
- **Local dropped it.** `path` was moved to `OwnOption` in the 0.1.0 review, which fixed the collision and
  left a quieter fault behind it: an unscoped `path` was then read by nothing at all, so an operator who
  wrote one had it silently ignored and their units went to the default location.

**These are the same defect, which is the point.** Not falling back is only half an answer; the other half
is saying so. A value that cannot be used has to be refused, or the fix for sharing is a new way to be
ignored.

**Decided against: fixing both providers by hand.** That is what produced this. An out-of-repo provider with
an identity-bearing setting would have to copy the read, the detection that the unscoped spelling was
written, and the sentence explaining it — and by `CLAUDE.md`'s standing question, anything an implementer
has to copy belongs in the framework. It is also twice, which is the bar D19, D20 and `Registry<T>` were
all extracted on.

**It THROWS rather than returning a result, so one call gets both halves.** `OptionException` is a
`DefinitionException`, `UnitProblems.Check` collects those, so a driver that calls `Address` inside
`ValidateAsync` reports it offline and the same call refuses at apply time. That is `RequirePart`'s shape
and it is deliberate: an offline check and the real thing must not be able to disagree, which is the rule
D34 stated and D35 had to finish.

**A unit's OWN value still wins over a stray unscoped one rather than also being refused.** The refusal
fires exactly where a value would otherwise be used or dropped in silence; a unit that named its own address
has nothing silently wrong with it. Refusing the leftover key would report it once per unit, naming every
unit except the one line to delete. Dead configuration is a lint, not a defect.

**Also fixed beside it, because it is D34's rule at the third site D35 did not reach:** `bucket` and
`bucketFrom` set together were resolved by precedence. Unlike the staging-bucket case, this one is not
benign — `bucket` winning uploads a website to the bucket an operator is migrating AWAY from while the
stack's bucket, the one the CDN is in front of, stays empty, and nothing errors. Measured: the files landed
in the old bucket, the stack's stayed empty, and `validate` reported nothing at all.

**The cost, stated plainly: this is a breaking change to a shipped surface.** An unscoped `bucket` or `path`
that "worked" for a single-unit procedure now fails. It is pre-1.0 and it is called out in the changelog,
and the alternative is keeping a spelling whose failure mode is a deleted website.

## D37 — The test target disagreed with every real one about what a deployment IS (2026-08-21)

`MemoryTarget` keyed what was deployed by unit NAME alone. So two deployments of one procedure shared a
store: after `acme` applied, a plan for `globex` read `acme`'s `db` as its own and reported `Update` where
every real provider reports `Create`. Now keyed by `(prefix, unit)`, which is what a real provider keys on.

**The prefix is not decoration and this is the one type that has to agree about it.** `DeploymentRequest`
documents it as "what lets one account host several independent deployments of the same procedure"; AWS
deploys `{prefix}-{unit}`, the local provider writes `{root}/{prefix}/{unit}`. A consumer with a deployment
per tenant, or staging beside production, is the ordinary case for that — and testing it against this target
returned the wrong answer, in the direction that hides work rather than inventing it.

**Found by disbelieving a sentence.** The class doc said *unlike a real provider it has no REQUIRED kind, and
that is the only difference*. That is a completeness claim, nothing checked it, and it was false. Same shape
as D35 and D36 — the rule was stated in prose, and prose does not go red.

**The type's own history said this would happen.** Its doc already records a defect of exactly this kind: an
unregistered unit kind used to fall back to memory behaviour, so an adopter got a green suite here and an
exception in production — and the note ends *its own test could not fail, which is why it survived*. The
same sentence applies to what was fixed here, one field over.

**The scripting helpers still take a unit name and no request**, and that is deliberate rather than
overlooked. `AlreadyDeployed("db")` means *already there for the deployment this test is about to run*,
which is the only thing a one-line helper with no request can mean, so a seeded unit answers for whichever
prefix asks. Making them take a prefix would cost every existing test a parameter to say something it never
needed to say.

**A destroy clears the seeded entry as well as the real one**, which is the half that would have rotted
quietly: without it, a destroyed unit goes on reading `Ready` from the half of the store nobody was looking
at — the exact lie a teardown must not tell (D32). It is mutation-checked, because it is behaviour defined
by a line whose absence changes nothing visible until the specific test that names it.

> ⚠ **Done by D38**, which added the check, verified it goes red against the code below, and made the
> fixture declare the exception rather than the suite guess it.

**Not done, and recorded rather than assumed: making `UnitDriverContract` check this.** That is the version
with teeth — a suite that goes red — and it needs `IUnitDriverFixture` to supply a second request under a
different prefix, which is a breaking change to an interface every out-of-repo implementer implements. It is
also the only sane way to handle the awkward case: an AWS content unit's isolation comes from the operator
pointing it at a different bucket, not from the driver, so only the implementer can supply a correctly
configured second deployment. Worth doing; worth deciding deliberately rather than as a side effect of this.
See `TASKS.md`.

**A postscript that is its own small lesson.** The sentinel this fix introduced — a prefix no request can
carry — went in as a NUL byte rather than the space its own comment claimed. It compiled, passed 876 tests,
and turned `MemoryTarget.cs` BINARY to git: every diff of it from then on would have read
`Bin 23103 -> 26734 bytes`. Nothing would have failed, and review would simply have stopped happening on a
file nobody knew had opted out — in a repository where `tests/ApiBaselines/` says *a diff here IS the API
review*. `doctor` now checks that every source file is text, and the check was verified by reintroducing the
byte and watching it go red.

## D38 — The isolation promise is now a contract check, and the fixture declares the exception (2026-08-21) — amends D37

`UnitDriverContract` deploys under one prefix and asserts the unit reads `Missing` under another, then that
removing one deployment leaves the other standing. `IUnitDriverFixture.Elsewhere` supplies the second
deployment. D37 fixed the instance and deferred this deliberately; this is the version with teeth.

**It was verified against the bug it was written for.** Reverting `MemoryTarget` to its pre-D37 keying turns
both new checks red. A suite that could not have caught the defect that motivated it is decoration, and that
is a cheap thing to check once and never wonder about again.

**The default is a working answer, not an opt-out.** `Elsewhere` defaults to the request with the prefix
swapped, which is exactly right for a unit that ADDRESSES itself: a stack is `{prefix}-{unit}`, a directory
is `{root}/{prefix}/{unit}`. So the check arrived non-vacuous for every existing fixture without one of them
being edited — the opposite of `ExpectedOutputs`, whose default means *I do not do that* (D18). Opting out
of this one takes a deliberate line, because being deployment-scoped is the ordinary case.

**The fixture supplies it rather than the suite deriving it, and both shipped providers proved why on the
first run.** A `content` unit's address is CONFIGURED — an option names its bucket — so a request differing
only in prefix is the same deployment wearing another name, and the check failed a correct driver until the
fixture returned one pointing at a different bucket. Only the implementer knows what a second deployment of
their unit looks like; a suite that guessed would fail correct drivers and pass incorrect ones.

**Null means the unit is GLOBAL, and that is a claim rather than an excuse.** The publish fixture returns
null: a published version is the registry's address, not this deployment's, so two deployments really do see
one — which is not a scoping bug, it is what a registry is. This is `IsRemovable`'s shape reappearing (D32),
and for the same reason: the one unit shape that most needs a contract is the one that cannot satisfy the
ordinary phrasing, so it declares itself and is held to everything else.

**What this does NOT check, stated rather than implied.** That a sweep is scoped to its deployment — that is
`DeploymentTargetContract`'s, and D33 already records that a generic suite cannot see it. And it cannot tell
a unit that is global from one whose author could not be bothered: `Elsewhere => null` is trusted the way
`IsRemovable => false` is trusted. Both shipped providers supply a second deployment, which is the standard
a third should be read against.
