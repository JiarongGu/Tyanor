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

> **0.1.1 SHIPPED** (2026-08-20), tagged `v0.1.1`; **0.1.0** before it at `cecd8da`. Three packages —
> `Tyanor`, `Tyanor.Providers.Local`, `Tyanor.Providers.Aws` — versioned in lockstep from the
> repository-root `Directory.Build.props` (D26), and cut by the GitHub Action rather than by hand: it writes
> the version AND stamps `## Unreleased`, so between releases this repository holds the last RELEASED number
> and never claims to be a version nobody published.
>
> **Verified from the published artifact, not from the build that made it — both times.** A fresh console
> project outside this repository restores the package from nuget.org and drives the seams that release
> added. 0.1.0: a `StepUnitDriver` step, a `CustomUnits` unit in `MemoryTarget`, a `UnitPausedException`
> pausing resumably, and an `IsRemovable` unit reported as retained (`resumable=True reason=approval
> retained=1`). 0.1.1: a stranger's own `IDeploymentTarget` with a `SweepAsync` — apply sweeps nothing, a
> full destroy sweeps once scoped to `site/acme`, a narrowed destroy sweeps nothing, a failing sweep leaves
> the run `Ok` and says so out loud, and `DeploymentTargetContract` passes a correct target and fails a
> broken one 2/2.
>
> **The 0.1.1 check had a specific question behind it, which is why it was worth running.** `SweepAsync` is a
> DEFAULT INTERFACE MEMBER, and D32 recorded the C# rule that bites there: a class fixes the interface
> mapping at itself, so a member that looks overridden can compile and silently never be called. Nothing
> inside this repository can prove that holds across an assembly boundary for somebody else's class. It does.
>
> **From here the public surface is a promise rather than a draft.** Someone has these packages. The
> baselines in `tests/ApiBaselines/` stop being a diff for a reviewer and become the record of what a
> consumer's build depends on — pre-1.0 still allows a breaking change, but each one now costs somebody
> something and must be called out in the CHANGELOG.
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

**A number is a position, not a name — cite an item by its title when the reference has to outlive the
backlog.** The 2026-08-20 adoption pass promoted two findings above the existing items and then closed both
the same day, so the numbers moved twice and came back; the "item 3" and "item 4" in `CHANGELOG.md`,
`docs/DECISIONS.md` and `src/Tyanor/StepUnit.cs` are accidentally correct again. Next time they will not be,
because those documents are append-only and this one is ordered by priority. Neither is wrong; they just
cannot both be stable.

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
holds one `AWS::SSM::Parameter` — free, and not globally named, so nothing collides. Each run uses a fresh
random prefix, and a staging bucket `{prefix}-deploy-{account}` is created for the template.

**The staging bucket used to survive, and this line used to deny it.** Nothing removed it, and because the
prefix is fresh per run, every live run would have left a *new* bucket holding the template it staged.
Closed by [D33](docs/DECISIONS.md): the walk-through's `DestroyAsync` sweeps, and the contract test — which
drives the driver directly and so never reaches the engine — sweeps explicitly in its `finally`. Check the
account after the first run anyway; that is what turned this line from a claim into a fact.

**What it needs permission to do:** CloudFormation create/describe/delete, `s3:CreateBucket` and
`PutObject` for staging plus `s3:ListBucket`, `DeleteObject` and `DeleteBucket` to sweep it afterwards,
`ssm:PutParameter`/`DeleteParameter`, and `sts:GetCallerIdentity`. Use a scratch account, not one holding
anything. The three delete permissions are new with [D33](docs/DECISIONS.md) — without them the run still
passes and reports one error line saying the bucket could not be removed, which is the design working, but
you will want to grant them rather than read that every time.

**`ssm:*` is the one you will not already have, and its absence lies to you.** The test's stack holds an
`AWS::SSM::Parameter`, CloudFormation acts with the caller's permissions, and an existing *deployment*
policy has no reason to carry SSM — the first adopter's scoped policy grants CloudFormation, S3,
CloudFront, RDS, ACM, Route 53, Lambda, API Gateway and IAM, and no SSM at all. Reusing credentials like
those fails mid-create with an `AccessDenied` that reads exactly like AWS rejecting what we build, which is
the single wrong conclusion this test exists to prevent. Grant it, or pick a resource type an
infrastructure deployer already has rights to.

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

**The half that used to block this is built.** A certificate ARN exists only once the run is under way and
has to reach the CloudFront distribution's parameters in a later unit, and nothing could carry it there.
`parameterFrom.CertificateArn = "domain:CertificateArn"` now can ([D34](docs/DECISIONS.md)) — so what is
left for the domain unit is issuing the certificate and waiting on DNS, not inventing a way to hand the
result on.

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

*Still nothing SHIPPED, but the first consumer has started.* Aurelia — the consumer named above — read
0.1.0's public surface, both providers' docs and the AWS provider's source against its own existing
deployer, and then wrote the spike and took it through **step 1 of `adoption.md`** (2026-08-20). Nothing has been
migrated and nothing has been applied, so this is not yet the "what a consumer WANTS" evidence this item is
after — but it is no longer only a read. Add findings here as they happen, one line each with enough
context to act on later. Promote anything that earns it into a numbered item above, and record a decision
in `docs/DECISIONS.md` if it changes a load-bearing choice rather than adding work.

---

**0.1.1 ADOPTED the day it shipped, and it closed every finding below.** Reported back because "your fix
worked" is worth more than the finding was, and because what it DELETED is the measurable part:

- **`assetsBucketParameter` removed the workaround entirely.** The spike no longer recomputes
  `{prefix}-deploy-{account}` from your internal convention, no longer makes its own
  `sts:GetCallerIdentity` call to fill a parameter value, and **no longer references the AWS SDK at all** —
  the only reason it had one is gone. Passing the bucket rather than publishing its name is better than what
  was asked for: the value and the upload cannot disagree, where the ask had settled for merely being able
  to read it.
- **`SweepAsync`** removes the residue this adopter had planned to delete by hand after every teardown.
- **`parameterFrom`** is the seam the domain unit needed, so that gap now waits on the unit rather than on a
  missing mechanism.
- **"An artifact part has exactly one writer"** settled the question and was ACTIONABLE, which is the test of
  a documentation answer: this adopter's deploy-time SEO prerender writes per-route HTML into the web dist
  between the API stack coming up and the files being synced. Under that rule it cannot become a unit that
  writes into the content unit's source, so it stays host-side — which is where it already was. A documented
  "no" ended the question at zero cost.
- Step 1 still returns clean on 0.1.1 with the workaround deleted. **Still step 1**: `SweepAsync` and
  `parameterFrom` are unexercised here, because nothing from this consumer has reached AWS either.

**ONE new finding, and it is on the upgrade path 0.1.1 just created.** `assetsBucketParameter` wins
SILENTLY over an explicitly-set parameter of the same name: `Parameters(...)` builds the map from the
`parameter.*` group, resolves `parameterFrom.*` into it, then does `parameters[named] = bucket`
unconditionally. So one key can be set three ways, two of which collide loudly while the third overwrites
without a word — the "resolved by precedence" **D34** refused for the other pair, arriving through the back
door. The `ValidateAsync` check added beside it catches only `assetsBucketParameter` with no `assets` part,
not this.

It matters because it is exactly what migrating off 0.1.0 looks like: an adopter who worked around the
missing seam already has `parameter.AssetsBucketName = "<hand-computed>"`, and adding `assetsBucketParameter`
beside it without deleting the old line is the natural edit. The hand-computed value is then ignored —
benignly here, since Tyanor's bucket is the right answer, but silently, and it would read as "my parameter
is being dropped" to anyone whose value differed.

→ **FIXED** (2026-08-21), exactly as the finding asked: refused offline and again at apply, so all three
ways are now consistent. One function produces the sentence and both callers use it, because three pairs
enforced by hand would have been six near-identical sentences — and the pair that was already written twice
is how this one came to be missed. A collision is also refused BEFORE any reference is resolved, so a name
set twice is not reported as whichever mistake the option order reached first.
[D35](docs/DECISIONS.md), which is D34's own rule finished rather than a new one.

**Checked on the consumer's machine, outside this repository:** `Tyanor.Providers.Aws.Tests` is 204/204
green; the live gate behaves as item 1 documents (`TYANOR_LIVE_AWS=1` with no key set fails both tests in
33 ms rather than skipping); and **`ValidateAsync` returns clean over the real thing** — Aurelia's actual
deployment described as a four-unit procedure (`db` → `api` → `web` → `content`), with a five-part
artifact built from its own `cdk synth` output: three transformed stack templates, a directory of Lambda
asset zips, and the built SPA. Adoption step 1 therefore holds against a real consumer's real artifact,
with no provider access and no credentials. Steps 2–4 are waiting on credentials, not on anything here.

**The composition root did not fight — so far, which is as far as `validate`.** The whole spike is one
file: our bundler runs, its output becomes `DeploymentArtifact` parts, and `new ProcedureRunner(target,
history, state)` takes it. No lifecycle, logging or configuration opinion had to be worked around, which
is D10 holding up under a consumer that has its own of all three. An apply may yet find something this
did not; the claim is bounded to what ran.

> **How to read the findings below.** Each is marked **[measured]** (something was run, here, and this is
> what it did), **[read]** (verified against your source, with the file and line) or **[reasoned]** (a
> consequence argued from the two, not observed). The distinction is not decoration — a first draft of
> this list asserted a failure mode as fact that turned out to be wrong when checked, so anything not
> actually run is labelled as such.

- **The staging bucket has no public existence, so a template that must NAME it makes the consumer
  recompute your convention.** Found by building, not by reading — the first real workaround. The
  composition root hard-codes `$"{prefix}-deploy-{account}".ToLowerInvariant()` and makes its own
  `sts:GetCallerIdentity` call, purely to fill in a CloudFormation parameter. **[read]**
  `StackUnit.cs:380`. → **FIXED**: `assetsBucketParameter` names the parameter and the provider fills it
  with the bucket it actually uploaded to, so the value and the upload cannot disagree.
  [D34](docs/DECISIONS.md).

- **A destroy leaves the staging bucket standing, and by your own doctrine no unit can be the one to
  remove it.** **[read]** `StackUnit.cs:286` creates it; `grep -rn "DeleteBucket" src/` returned nothing at
  all. The source deployer empties and deletes it, so it was a regression as well as a gap. → **FIXED**, and
  it was structural rather than a missing call: provider-owned infrastructure had no lifecycle at all. Both
  shipped providers turned out to have something no unit could remove. [D33](docs/DECISIONS.md).

- **A `stack` unit cannot consume a value an earlier unit produced, and item 1 will hit this before
  Aurelia does.** **[read]** `content` can (`bucketFrom` / `invalidateFrom` take `"{unit}:{OutputKey}"`);
  `parameter.*` is verbatim text. The case is a custom domain's certificate ARN. → **FIXED**:
  `parameterFrom.*` takes the same reference, and all three callers now share one resolver instead of two
  copies. [D34](docs/DECISIONS.md).

- **A step that prepares another unit's payload has no honest phase**, and **[read]** whether an artifact
  part may be mutated between units is addressed in none of `guide.md`, `adoption.md`, `providers.md` or
  `DECISIONS.md` — searched, not assumed. A hole in the docs before it is a hole in the design. →
  **DECIDED and written down**: a part has exactly one writer, which is the unit that owns it. A preparing
  step gets its own output part; the consuming unit's `source` points at it. Not enforceable by Core, so it
  is a review rule with a named symptom. [D34](docs/DECISIONS.md).

- **Item 3 evidence: the backend a consumer needs first is the one their app already uses.** **[read]**
  Aurelia's run log (`deploy_history`) and a typed current-state row (`deployment_state`) are two tables in
  one SQLite database, and the UI's guard against deleting a live run is built on those shapes. Adopting today means Tyanor's two `json` stores beside them — two records
  of the same run, and the older one still driving the screen. Not a defect, a cost, and the answer to
  "when a consumer needs one, write it where they need it": SQLite, and the pressure is the existing
  schema rather than the format.

- **The exception fix in the port is confirmed against the source, and it was worth making.** **[read]**
  The CHANGELOG says the source read every CloudFormation exception as "the stack does not exist". It
  does, still: `AwsDeployer.cs` catches a bare `AmazonCloudFormationException` in **three** places that
  then claim absence — `StackExistsAsync` returns false, `StackStatusAsync` returns null,
  `DeleteStackAsync` returns as though the stack were gone — plus a fourth in `StreamNewEventsAsync`
  that is not an existence claim but silently stops streaming events. (An earlier draft of this line said
  "four places" without that distinction; the fourth is a different bug, not the same one.) **[reasoned]**
  Two consequences, failing in opposite directions: a throttle during pre-flight reads as *absent*, so the
  create that follows hits a stack that was there; a throttle while polling a delete reads as *gone*, so a
  teardown reports success over a stack still standing. Both are invisible until a slow day on the API.

- **An artifact part is a whole directory, so a producer that emits mixed kinds into one output dir has to
  re-lay it out.** Aurelia's bundler writes `{prefix}-{service}.template.json` and `<hash>.zip` side by
  side in one directory. The zips are already named exactly as their object keys — but **[read]**
  `StackUnit.cs:294` walks the part with `Directory.EnumerateFiles(assets, "*", AllDirectories)`, so
  pointing `assets` at that directory uploads the templates as assets too. Harmless here, three lines to fix, and
  noted only because `providers.md` describes a part as "your names, pointing at paths" and a reader can
  come away thinking a part is a set of files rather than a directory root. → **`providers.md` now says a
  part that names a directory is a ROOT and is used whole.** The behaviour was right; the sentence was not.

- **`Tyanor.DeploymentRequest` collides with the consumer's own `DeploymentRequest`.** **[measured]** —
  it is a `CS0104 ambiguous reference` the first time you reference both. Unavoidable and
  not worth renaming — they describe the same idea because one was extracted from the other — but every
  file that touches both needs an alias, and the first adopter will be the deployer it came from. Worth
  one sentence in `adoption.md` so it reads as expected rather than as a mistake. → **Now in
  `adoption.md`**, beside step 1, which is where you first write both.

- **For this consumer item 1 outranks item 2, and one line of item 1 deserves a warning.** Aurelia's own
  repository records its deployer as live-verified against real infrastructure — its rules describe a
  crash mid-deploy recovered by resume — though that predates this checkout and is not something this
  spike re-measured, so take it as their record rather than as a result reported here. Either way no
  amount of offline coverage makes an unrun port a swap, so the live run is the gate. **[read]** Practical
  note for the permissions list: the test's stack is an `AWS::SSM::Parameter`, CloudFormation acts with the
  caller's permissions, and an adopter's *existing* deployment credentials are unlikely to carry `ssm:*` —
  Aurelia's scoped policy (`docs/aws-deploy-policy.json`) grants CloudFormation, S3, CloudFront, RDS, ACM,
  Route 53, Lambda, API Gateway, IAM and others, and **no SSM at all**. Reusing them yields an
  `AccessDenied` mid-create that reads exactly like AWS rejecting what you build. Say so beside the
  permission list, or pick a resource type an infrastructure deployer already has rights to. → **Now said,
  beside the permission list in item 1.**

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

**The gap already known — now BUILT, and it landed where this note predicted.** Publish is irreversible: you
cannot unpublish a version, yet `IUnitDriver.RemoveAsync` was documented as "remove and wait until it is
gone" and `Reconcile.DecideDestroy` handed `Remove` to every phase that was not `Missing`. This note said
the answer, when someone needed it, would be *a unit declaring itself unremovable and a destroy plan
reporting it as RETAINED rather than skipping it in silence*. That is exactly what shipped — see
[D32](docs/DECISIONS.md).

What earned it was the framing rather than an imagined case: Tyanor is a **code-driven CI/CD framework** as
much as a deployment library, an adopting application builds the steps we do not ship, and publish is one of
the seven phases in the brief. D3's bar is a real need, and a pipeline that cannot express its own publish
step is one.

`IUnitDriver.IsRemovable(context)` defaults to true, so nothing broke. Say false and: the destroy plan lists
the unit under `Plan.Retained` before anything runs, `RemoveAsync` is never called, the teardown succeeds and
says RETAINED out loud, and the unit's **state is kept** — because Tyanor still owns it, and forgetting that
is how a resource becomes unmanaged. `UnitDriverContract` holds such a unit to the opposite promise of a
removable one: that it survives, and is not lying about itself.

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
