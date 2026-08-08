# TASKS — Backlog

> ## Where day one landed
>
> **The expensive part was the learning, and it is banked.** What came across from the source deployer is
> the part that is hard to *discover* — which reconcile branches exist, that only a terminal event may fail
> a call, that a credential error must pause rather than fail, that a plan can be read from the provider.
> None of that was designed here; it was learned the expensive way, elsewhere.
>
> Measured against the source (`Aurelia.Deployment`, 2,597 lines):
>
> | | State |
> |---|---|
> | Operational doctrine (~700 lines of `AwsDeployer` + the contracts) | ✅ ported, generalized, tested |
> | AWS mechanics (CloudFormation / S3 / CloudFront) | ✅ ported — **never run against AWS**, see D14 |
> | ACM + Route 53 domain setup (~350 lines) | deferred on purpose — item 1 |
> | `cdk synth` post-processing (`DeploymentBundler`) | stays in Aurelia; it is authoring, not operations (D5) |
> | DB snapshot, migration verification, prerenderer | stay in Aurelia; application policy, not operations |
> | Host IPC (`DeployModule`, 585 lines) | stays in Aurelia; UI wiring, not operations |
>
> **Two providers ship.** `Tyanor.Providers.Local` deploys a self-hosted server to a machine and was built
> before AWS on purpose, to test the contracts against a shape they were not extracted from (**D13**).
> `Tyanor.Providers.Aws` is the port (**D14**). Between them the engine has been driven by a target with a
> control plane and one with none, and needed no change for either.
>
> **What is still not true:** nothing has been deployed to a cloud from this repo — the AWS live test exists
> and is gated behind `TYANOR_LIVE_AWS`, and until it runs, "ported" is the honest word. And no real
> consumer ships on Tyanor yet.

> **Pre-1.0 priority: the STRUCTURE, then providers one at a time as they are needed.** The seams are the
> expensive thing to change later; a provider is not. So anything that would make a provider or a storage
> backend written *outside* this repository second-class gets fixed first — see **D15**, and the standing
> question in `CLAUDE.md`: what would an out-of-repo implementer have to copy?

Open work, worked one item at a time, top first. Implement fully (rules → code → tests), update the docs
it touches, **remove the item**, then commit. Discovered work is added here, never dropped.

---

## 1. Run the AWS provider against AWS, then add the domain unit

Two halves of finishing D14, in this order — the second is not worth designing until the first has told us
whether the plumbing is right.

**Run the live test.** `TYANOR_LIVE_AWS=1` plus key, secret and region. It deploys a free single-resource
stack, plans, re-applies (the "No updates are to be performed" path, which resume depends on), refreshes and
tears down. Everything testable without a cloud already is; what this covers is whether the SDK calls are
wired correctly, and nothing else can tell us.

Expect it to find something. A port that has never run never does exactly what it looks like it does.

**Then the domain unit** — ACM certificate issuance and Route 53 validation, ~350 lines still in Aurelia
(`Aws/AwsDomainSetup.cs`, `Domains/AwsRoute53DomainProvider.cs`). It maps onto the model well: a certificate
pending validation is `Converging`, and manual DNS is a `PauseReason.External` that resumes.

The open question, and the reason it is deferred rather than half-built: **when a deploy needs a human to add
a DNS record, does the whole procedure pause or only that unit?** The source paused the whole thing and
returned the records to show. That works because it had one consumer with one UI. Decide it with a real
consumer, not from the armchair.

- Acceptance: the live test passes against a real account and leaves nothing behind; the domain unit's pause
  carries the records the operator has to add.

## 2. Put a real consumer on it

D13 proved a second *shape* fits, using a provider and tests inside this repo. It did not prove a second
*consumer* fits, and that is a different claim: a real application brings a lifecycle, a UI, a logging
opinion and a configuration story, and those are where a library gets pushed on.

- **Daoris self-hosting a server** is the closest fit — `Tyanor.Providers.Local` was built to its shape,
  so this is now mostly wiring rather than design.
- **Aurelia** is the other half. `Tyanor.Providers.Aws` covers its infrastructure and its website; what it
  keeps is `DeploymentBundler` (authoring, D5), the DB snapshot and migration check, the prerenderer and
  `DeployModule`. Needs item 1's domain unit before it is a straight swap.

Expect this to move `DeploymentRequest` again. That is not a failure of D13; a test cannot want something
a person will.

- Acceptance: one of the two ships a deployment through Tyanor, with the composition root in the
  application and no Tyanor change required to make it work.

## 3. State backends beyond a local file — SQLite, Postgres, S3

`FileRunHistory` ships and is the default; the seam is `IRunHistory` and the choice is the consumer's
(`AddTyanor(cfg => cfg.UseFileState(...))`). What is missing is everywhere else state needs to live.

**Each one now has an entry ticket**: `RunHistoryContract` and `StateStoreContract` in `Tyanor.Testing`
(D15). A backend that passes them behaves the way the engine assumes, including the two that are easy to
miss — refusing to delete a live record, and keeping a null fingerprint null rather than helpfully turning
it into an empty string, which would silently convert "unknown" into "unchanged" and lose the drift.

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

- Acceptance: the backend passes its contract suite, AND — the thing a contract cannot check — kill the
  process mid-run and a new process finds the live record via `LiveAsync` and resumes. For a shared backend,
  from a DIFFERENT machine.

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
- **Plugin DISCOVERY.** Providers register in the composition root (D6). Writing your own and registering it
  is fully supported and first-class (D15) — loading code found on disk is the part that is refused.
- **A third provider** (Kubernetes, SSH, a container host). A target with a control plane and one without
  have both driven the engine unchanged (D13, D14); a third shape proves nothing further until a consumer
  asks for it.
- **`DetectStackDrift`.** It is what would catch an AWS resource edited in the console, and it is a paid
  asynchronous call per stack — too expensive for every plan. So AWS drift is CloudFormation-known drift,
  stated rather than hidden (D14).
- **Anything a provider could orchestrate for itself.** The local provider was tempted twice — stopping a
  process before replacing files, and retrying its own health check — and both belong to the engine, which
  already has them. A provider that grows run-state logic is writing a second engine inside itself.
