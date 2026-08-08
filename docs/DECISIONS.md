# Decisions

Load-bearing choices, with the reasoning that produced them. A decision recorded here is not re-litigated
per feature — but it can be *overturned*, by appending a new entry that says why. Never edit one to say
something it did not say.

Each entry names what was decided, what it was decided **against**, and what evidence exists. "It seemed
cleaner" is not evidence.

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

### Deferred deliberately: the domain unit

ACM certificate issuance plus Route 53 validation is the natural third kind, and it maps onto Tyanor's model
suspiciously well — a certificate pending validation is `Converging`, and manual DNS is a
`PauseReason.External` that resumes. That is exactly why it is being left alone: the interesting question is
whether waiting for a human to add a DNS record should pause the whole procedure or only that unit, and the
answer depends on what a real consumer wants to show its operator. Guessing it is how a wrong abstraction
gets built confidently.
