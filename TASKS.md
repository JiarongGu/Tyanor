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
> | AWS mechanics (CloudFormation / S3 / CloudFront) | ✅ ported, control flow tested offline — **never run against AWS**; D14, D23 |
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
> consumer ships on Tyanor yet. What the live test still owes us is now much narrower than it was: the
> driver's own control flow is covered offline (**D23**), so what remains is whether AWS accepts what we
> build and answers the way the phase table believes.

> **0.1.0 is the first release.** Cut with `npm run doctor` and `node devtools/dev.mjs release`; three
> packages — `Tyanor`, `Tyanor.Providers.Local`, `Tyanor.Providers.Aws` — versioned in lockstep from the
> repository-root `Directory.Build.props` (D26).
>
> **What a consumer gets, and what they do not.** The engine, both providers, the contract suites and a test
> target are all real and covered. The AWS provider's SDK calls have still never reached AWS — that is the
> one claim this release does not make, it is stated in the README, and item 1 below is how it stops being
> true.

> **The review before it.** A full review before it went out found one bug that mattered — a destroy plan built
> without a state store reported "0 to destroy" and `IsDestructive` false, silently opening the confirmation
> gate in front of the only irreversible direction — plus a resumed run losing its original start time, an
> unscoped `path` collapsing every local directory unit into one folder, two rules written twice, and a
> version declared in two places while `doctor` claimed one. All fixed; see the CHANGELOG.
>
> **The most useful thing it found was an untested surface, not a bug.** "Mocking the SDK proves nothing"
> was true of CloudFormation's vocabulary and had been stretched to cover the driver's own control flow,
> leaving the largest unexercised code path in the repository. Forty tests and one injectable poll interval
> later, it is covered — and every one was mutation-checked, because a test that has never failed is
> decoration. **D23.** When "it can only be tested against the real thing" gets said again, check whether it
> is a fact about the target or about a hard-coded constant.

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

**Run the live test.** Four variables, one command:

```bash
TYANOR_LIVE_AWS=1 \
TYANOR_LIVE_AWS_KEY=AKIA… \
TYANOR_LIVE_AWS_SECRET=… \
TYANOR_LIVE_AWS_REGION=ap-southeast-2 \
dotnet test tests/Tyanor.Providers.Aws.Tests --filter FullyQualifiedName~AwsLiveDeploymentTests
```

```powershell
$env:TYANOR_LIVE_AWS=1; $env:TYANOR_LIVE_AWS_KEY='AKIA…'
$env:TYANOR_LIVE_AWS_SECRET='…'; $env:TYANOR_LIVE_AWS_REGION='ap-southeast-2'
dotnet test tests/Tyanor.Providers.Aws.Tests --filter FullyQualifiedName~AwsLiveDeploymentTests
```

**With the gate on but a variable missing it FAILS rather than skipping** — silently doing nothing when
somebody deliberately switched this on is how a live test comes to be believed without ever having run.

**What it costs and what it touches.** Two tests. The first plans, creates, re-applies (the *No updates are
to be performed* path that resume depends on), refreshes and tears down; the second runs the whole
`UnitDriverContract` against the real service, creating and destroying the stack several times. The stack
holds one `AWS::SSM::Parameter` — free, and not globally named, so nothing collides and nothing survives.
Each run uses a fresh random prefix, and a staging bucket `{prefix}-deploy-{account}` is created for the
template.

**What it needs permission to do:** CloudFormation create/describe/delete, `s3:CreateBucket` and
`PutObject` for staging, `ssm:PutParameter`/`DeleteParameter`, and `sts:GetCallerIdentity`. Use a scratch
account, not one holding anything.

**If it fails, that is the point.** Everything it exercises is offline-covered already *except* the two
questions in the right-hand column below, so a failure here is information about AWS rather than about the
control flow — which is what makes it worth running rather than reading.

Everything testable without a cloud now IS tested, and that sentence was an overstatement until recently —
the stack driver had never once been held to `UnitDriverContract`, because the only place it ran was inside
this gated test. **The last mile is now exactly this list and nothing else:**

| Verified offline | Left to the live run |
|---|---|
| the phase table, against every status the SDK enumerates | whether a real create settles into the status the table believes |
| the classifier, against real error codes | whether AWS emits a code this list has never seen |
| which request we build — fields, staging bucket, keys, capabilities | whether AWS **accepts** that request |
| the driver's control flow: throttles, teardown re-runnability, event de-duplication, batching | rollback behaviour, `UPDATE_ROLLBACK_FAILED`, timing |
| **both** unit kinds against `UnitDriverContract` | that the same contract holds against the real service |

The offline contract runs against a fake that models exactly two things — a created stack can be described,
a deleted one cannot — and deliberately none of the interesting statuses, so it cannot start certifying AWS's
behaviour by accident. That is **D23** applied properly rather than as a blanket: what the suite asserts is
our driver's behaviour, and the questions only a cloud can answer stay in the right-hand column.

Expect the live run to find something anyway. A port that has never run never does exactly what it looks
like it does — but the surface where it can surprise us is now small and named, which is the difference
between a gap and an unknown.

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

### What to watch for while adopting, and write down below

The review before 0.1.0 was thorough about what the code DOES. It could say nothing about what a consumer
WANTS, and those are the questions adoption answers. Worth noticing as they happen, because they are
invisible in hindsight:

- **Anything you had to work around.** A workaround is a missing feature that has already been paid for
  once. Note the workaround, not the feature you think it implies.
- **Anything you had to read the source for.** The guide is meant to be enough; each time it was not is a
  gap in it, and the specific question you had is the fix.
- **Where the composition root fought you.** D10 says Tyanor decides nothing about lifecycle, logging or
  configuration. Every place that turned out to be false is a real defect.
- **What the operator asked that Tyanor could not answer.** "Where is my site", "what will this change",
  "why did it stop" all have answers now; the next one on that list comes from a person, not from here.
- **Whether a pause was actually resumable in practice**, not just in the record. That is the whole claim.
- **Anything in `DeploymentRequest.Options` that wanted to be typed.** D4 says the untyped map stays; a
  real consumer straining against it is the evidence that would revisit that, and nothing else is.
- **Every unit kind you had to register yourself, and whether it MOVED.** `CustomUnits` (D19) is the growth
  path for a service Tyanor does not support, and the claim is that the step is yours rather than the
  platform's — one registration, handed to every target. It is proved across `LocalTarget`, `AwsTarget` and
  `MemoryTarget` in this repo, which is the weaker version of the claim. The real one is an adopter moving
  their own step between platforms and not editing it. If they edit it, what they had to change is the defect.

### From adoption

*Nothing yet — the first consumer has not shipped.* Add findings here as they happen, one line each with
enough context to act on later. Promote anything that earns it into a numbered item above, and record a
decision in `docs/DECISIONS.md` if it changes a load-bearing choice rather than adding work.

## 3. A storage backend somebody actually needs — SQLite, Postgres or S3

**The seam is done (D20); the backends are not, and that is deliberate.** Storage is named by a descriptor —
`"sqlite:/var/lib/app.db"`, `"postgres:Host=db;…"`, `"s3://bucket/key"` — resolved through registered
`IStorageBackend`s. `json` ships and is registered by default. Nothing else does.

So this item is no longer "build three packages". It is: **when a consumer needs one, write it where they
need it** (D19/D20's path), and upstream it here if it generalizes. `StateStoreContract` and
`RunHistoryContract` are the entry ticket — a backend that passes them behaves the way the engine assumes,
including the two that are easy to miss: refusing to delete a live record, and keeping a null fingerprint
null rather than helpfully turning it into an empty string, which would silently convert "unknown" into
"unchanged" and lose the drift.

Likely order, when someone asks:

- **`sqlite`** — a single-machine operator with more than a file's worth of history.
- **`postgres`** — a team, or a service that already has a database.
- **`s3`** — CI and several machines sharing one store, and the first one where conditional writes matter.

**Cross-machine CHECKING is supported; cross-machine SYNCING is not — D9 as scoped by D11.**
`PlanAsync` reads the shared history, so a second machine sees `ActiveRun`, `HasStalledRun` ("a run is
recorded live but nothing is converging — it stopped, possibly on a machine that is not coming back") and
`InSync`. `ApplyAsync` adopts a live run rather than opening a competing one. **No lease, no lock** — the
provider arbitrates by attachment, and the plan makes the situation visible. Do not add locking here
without a case that attachment demonstrably cannot cover.

**What each backend must decide deliberately: concurrent writes.** The `json` backend is last-writer-wins
with no cross-process lock, so two machines writing at the same instant can lose a record. That is bounded
to visibility — the provider is still the arbiter, so infrastructure stays correct — but it stops being
acceptable the moment anything automated gates on the history. `DeploymentState.Serial` exists for a backend
that can check it: refuse a save derived from state someone else has since replaced. S3 preconditions and a
Postgres transaction are the cheap correct answers, and they belong in the backend, not as a new concept in
the engine.

Until one exists, the honest word stays *checking* rather than *syncing*.

- Acceptance: the backend passes both contract suites, AND — the thing a contract cannot check — kill the
  process mid-run and a new process finds the live record via `LiveAsync` and resumes. For a shared backend,
  from a DIFFERENT machine.

## 4. Build a real pipeline out of unit kinds — and find out what breaks

**Answered on paper, not yet in practice (D21).** This item used to ask what a procedure should be *authored*
as, on the premise that restore → build → test → package → publish → deploy → validate is broader than
deployment units. Checked phase by phase, it is not: all seven can answer "has this already happened?", which
is the only thing being a unit requires. So the answer is **keep authoring in C#** — no DSL, no `Pipeline`
type beside `Procedure` — and a pipeline is a procedure whose units happen to be builds and tests.

`CustomUnits` (D19) means a consumer can do this today without a single change here. So what is left is not
design work, it is USE:

- Build one, in the application that wants it. Daoris is the obvious candidate — it needs restore/build/test
  before it self-hosts anything.
- Report what the contract could not express. The analysis found one gap already and there will be others;
  the point of building it for real is the "others".

**The gap already known:** publish is IRREVERSIBLE. You cannot unpublish a version, but
`IUnitDriver.RemoveAsync` must "remove and wait until it is gone" and `Reconcile.DecideDestroy` hands `Remove`
to every phase that is not `Missing`. Nothing has been added for it, deliberately — D3's bar is that a shape
is earned by someone needing it, not by someone imagining it. The likely answer, when it is needed: a unit
declaring itself unremovable and a destroy plan reporting it as RETAINED rather than skipping it in silence.

- Acceptance: a real pipeline runs in a real consumer, and whatever it could not express is written down here.

---

## 5. Decide what a destroy should do about a live APPLY run

Found by the pre-release review; recorded rather than guessed at, because either answer is defensible and
only a real consumer can say which is right.

With no explicit run id, `ProcedureRunner` adopts the live run for a procedure + prefix — D9's "the caller
should not have to know whether they are starting or continuing", and what stops two live records existing
for one deployment. But adoption does not check the run's **kind**. So a `DestroyAsync` after a paused apply
continues the apply's record and rewrites it as a destroy, and the paused apply stops appearing in the
history as something that was interrupted.

Three candidate answers, in order of how much they cost:

- **Leave it.** The live record gets resolved rather than dangling forever, and what the operator is doing
  now IS a destroy. This is what ships.
- **Finish the adopted run first**, marking it failed or superseded, then open a destroy run. Honest history,
  one more record, and a new question about what "superseded" means.
- **Refuse**, making the operator resolve the apply before destroying. Safest, and the most annoying — it
  turns "just tear it down" into a two-step.

The information that decides it is what an operator's history is FOR in a real consumer, which is item 2.

- Acceptance: a real consumer's UI shows a run history that reads correctly after an interrupted apply
  followed by a destroy, and whichever answer that needs is the one implemented.

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
