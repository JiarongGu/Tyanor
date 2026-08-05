# Decisions

Load-bearing choices, with the reasoning that produced them. A decision recorded here is not re-litigated
per feature — but it can be *overturned*, by appending a new entry that says why. Never edit one to say
something it did not say.

Each entry names what was decided, what it was decided **against**, and what evidence exists. "It seemed
cleaner" is not evidence.

---

## D1 — Reconcile against the provider; keep no state file (2026-08-06)

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
