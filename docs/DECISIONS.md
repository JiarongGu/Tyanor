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
