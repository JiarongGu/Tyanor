# Changelog

All packages version in lockstep from the repository-root `Directory.Build.props` (`VersionPrefix`).
From 1.0, SemVer 2.0 applies. Pre-1.0, a minor bump may carry a breaking change — each is called out here.

## Unreleased

### Added

- **A full destroy now removes what the PROVIDER created for itself.** `IDeploymentTarget.SweepAsync` runs
  once a teardown has finished with every unit. The AWS provider empties and deletes the staging bucket it
  uploads templates and assets through; the local provider removes its `.tyanor` folder of pid files and the
  deployment folder itself, when nothing else is left in it. Until now nothing removed either, so a destroyed
  deployment left them standing for ever — and `docs/adoption.md` claimed a teardown left nothing. Found by
  the first adopter's spike, against a `grep` rather than a review. Reasoning in `docs/DECISIONS.md` **D33**.
  - **Additive and defaulted**, so no implementation broke: a target that creates nothing of its own leaves
    it alone and is correct.
  - **A narrowed destroy never sweeps.** `Only("web")` is a partial teardown by request, and the units left
    out still need the scaffolding.
  - **A sweep that fails does not fail the teardown**, because every unit is already gone — but it is
    reported as an error line naming what could not be cleaned up. Silence there would be the thing D32
    refuses.
- **`DeploymentTargetContract`** in `Tyanor.Testing`, holding a target to the two promises a sweep satisfies
  by omission: tolerating nothing to sweep, and surviving a second run. Now in `doctor`'s enforced list, so
  both shipped providers run it ungated.

### Fixed

- **`docs/architecture/overview.md` still described a teardown as having "two answers"** — D32 gave it three
  four commits earlier and the table was never updated. It now shows `Retain`.

### Changed

- `AwsTarget` gained an internal constructor taking its SDK clients, so the target's own composition is
  testable without an account — D23 applied to the target the way it already was to the drivers.

## 0.1.0

The first release, in two parts.

**[What ships](#what-ships)** is the whole of what 0.1.0 gives you. Read that if you are picking this up.

**[Before it shipped](#before-it-shipped)** is what a full review found on the way here — bugs, breaks and
cleanups that no user ever met, because there was no previous version to meet them in. It is kept, and kept
separate, because the reasoning is the point: most of those mistakes are ones a provider or storage backend
written outside this repository can still make.

Test counts quoted against a particular piece are what it brought with it. The suite as a whole was **814
tests** at 0.1.0 — stated against the release rather than against the repository, so that it stays true.


### What ships

#### Three packages

```
dotnet add package Tyanor                     # everything that is not a provider
dotnet add package Tyanor.Providers.Local     # …and at least one provider
```

`Tyanor`, `Tyanor.Providers.Local`, `Tyanor.Providers.Aws`. Inside the first, four namespaces do the
layering — `Tyanor` (contracts and the reconcile decision), `Tyanor.Engine` (the runner and `AddTyanor`),
`Tyanor.Engine.State` (the stores), `Tyanor.Testing` (the contract suites and `MemoryTarget`) — and you install
one thing. One dependency in total: `Microsoft.Extensions.DependencyInjection.Abstractions`, because
`AddTyanor` is an extension method on `IServiceCollection`. No test framework, so the contract suites run
under whichever one you already have. `doctor` holds that list to exactly those, in both directions.
Reasoning in `docs/DECISIONS.md` D26.

#### The engine

- **`ProcedureRunner`** — units, the reconcile decision, failure classes, run state, the provider contracts;
  ordered units, per-unit reconcile, bounded retry on transient errors only, classified pause/fail, run
  history and state.
- **The doctrine, extracted rather than invented**: ported from a deployer that ran real infrastructure,
  survived a crash and rebuild mid-deploy, and resumed to completion. Reasoning in `docs/DECISIONS.md`.

- **Two stores, at locations the consumer chooses.** `FileStateStore` (what Tyanor OWNS) and
  `FileRunHistory` (what was ATTEMPTED) — JSON, atomic write-then-replace, and a history that refuses to
  delete a live run. `InMemoryRunHistory` and `InMemoryStateStore` for tests and one-shot CI runs. The
  default is durable: an in-memory default would look like it worked until the moment resume mattered.
  SQLite / Postgres / S3 backends are `TASKS.md` item 3.
- **A planning phase.** `ProcedureRunner.PlanAsync` returns what an apply WOULD do — created, replaced, or
  already in flight — derived from the provider rather than from a stored model, so it cannot go stale the
  way a state-file plan can. It is a forecast and says which two things it cannot know.

- **devtools.** `npm run doctor` — build, test, and the checks that keep this repo honest: supersession in
  `DECISIONS.md` must point both ways (it found five missing forward pointers on its first run), every rule
  indexed and linked, every documentation link and anchor resolving, a credential scan, and the two
  architectural claims the README makes out loud.

- **`Tyanor.Providers.Local` — the first provider, and it deploys.** A directory materialized from an
  artifact part, a long-lived process run out of it, a TCP health check, a teardown in reverse. Built before
  the AWS port on purpose: it is the target with **no control plane**, so everything the engine assumes when
  it talks to a cloud has to be built from a pid file and a marker. 45 tests that copy real files and start
  real processes. Reasoning in `docs/DECISIONS.md` D13.
  - A new build is written *beside* the running one (`{unit}/releases/{fingerprint}`) and the server
    restarts into it — which is also how a constraint that looked like it needed a dependency graph was
    absorbed without one.
  - A second run attaches to a server another run started rather than launching a competitor.
  - A recorded pid whose start time does not match is refused rather than killed. Operating systems reuse
    pids.

- **`Tyanor.Providers.Aws` — the port.** CloudFormation stacks and website files in S3 behind CloudFront,
  driven by the SDK with no CLI and no bootstrap, so an operator deploys with no cloud toolchain installed.
  Templates are already synthesized; this executes them (D5). Reasoning in `docs/DECISIONS.md` D14.
  - The phase table is tested against every CloudFormation status the SDK knows, enumerated by reflection so
    a future SDK upgrade that adds one fails the build rather than landing silently in the fallback.
    `ROLLBACK_COMPLETE` and `UPDATE_ROLLBACK_COMPLETE` are one character apart and opposite: the first must
    be replaced, the second is perfectly updatable, and conflating them deletes a working stack.
  - Every credential and transient error code from the source deployer, kept whole, with the
    `InnerException` walk.
  - Fixed in the port: the source read *every* CloudFormation exception as "the stack does not exist", so a
    throttle read as absent and the create that followed hit a stack that was there all along.
  - The driver's control flow is covered offline by recording fakes — request construction, the staging
    bucket's per-account name, no-updates versus a real validation error, teardown re-runnability, event
    de-duplication, S3 key shape and content types, CDN invalidation, delete batching. Every one was
    mutation-checked: the behaviour was broken deliberately and the suite had to fail. D23.
  - **Both** unit kinds are held to `UnitDriverContract` offline. The content unit runs against an in-memory
    bucket; the stack unit against a CloudFormation fake that models exactly two things — a created stack can
    be described, a deleted one cannot — and none of the interesting statuses. Every value either fake hands
    back is a real string, and what the suite asserts is our driver's behaviour, never AWS's. The rollbacks,
    the timing and whether a request is accepted at all stay behind `TYANOR_LIVE_AWS`, which is the line D23
    actually draws.
  - **Not run against AWS.** Whether the SDK accepts what we build is unverified in this repo. The live
    test deploys a free single-resource stack and is gated behind `TYANOR_LIVE_AWS`.

- **`MemoryTarget` — a target to test YOUR code against, without a cloud.** An application that deploys
  through Tyanor has its own logic: does the UI offer a Resume button when a run pauses, does the pipeline
  stop when validation fails, does the operator see a drifted deployment. Reaching those states against a
  real target costs credentials, money and minutes, so in practice those paths get written once and never
  exercised. One line each here — `.Fails("api", FailureClass.Credentials)`, `.Reports("api",
  UnitPhase.Converging)`, `.Drifted("db")` — and `target.Calls` says what the engine actually decided.
  **It is a provider, not a mock**: it deploys to a dictionary the way `LocalTarget` deploys to a machine,
  it never simulates another provider's semantics, and it passes `UnitDriverContract` and
  `FailureClassifierContract` like any other implementation. D24.
- **The contract suites had no negative tests, so a broken suite would have silently CERTIFIED.** Every
  use of them ran against an implementation that passes, which proves only that green is reachable. Making
  `ContractSuite` report every check as passing, or `AssertAsync` never throw, left the whole repository
  green while five implementations went on "proving" they behave — the worst shape a test can have, and the
  suites are the entry ticket D15 offers to providers written elsewhere. Eighteen tests now run each suite
  against implementations that are deliberately wrong, one broken promise at a time.
- **Retry DISCIPLINE is now tested, having been doctrine with no test.** `error-classification.md` says
  retry only `Transient` — retrying a malformed request is a lie told five times, and retrying an expired
  credential merely delays the moment somebody can fix it. Widening the retry to cover credentials passed
  the entire suite. Now pinned: credentials, hard and unrecognised errors are each tried exactly once, and a
  persistent transient is tried exactly as often as the budget allows before pausing.
- **A teardown does not ask a removed unit what it owns** — it knows. Equivalent in outcome against a
  correct driver, which is why removing it broke no test, but it is one provider call per unit and it is the
  difference between recording the truth and recording a resource on its way out. `MemoryTarget.Refreshes`
  makes it countable.
- **Four more contract checks, for promises the interface docs made and nothing verified**: that reading
  the phase of something that does not exist does not bring it into being (the old check only looked after a
  deploy, so a driver whose `PhaseAsync` created things would pass); that an undeployed unit produces no
  outputs; that outputs do not survive a remove; and that `ValidateAsync` reports rather than throws.
  `IUnitDriverFixture.ExpectedOutputs` came with them — a default member, so nothing broke — because without
  it the outputs checks could not fail at all and were decoration.
- **Contract suites, so a provider or backend written anywhere can prove it behaves** (`Tyanor.Testing`).
  `UnitDriverContract`, `FailureClassifierContract`, `RunHistoryContract`, `StateStoreContract`. They check
  what the engine assumes and no signature states: that reading a phase changes nothing, that removing what
  is already gone is fine, that an update with nothing to change says so, that a resource keeps its identity
  across a refresh, that a wrapped credential error still classifies, that a live run record cannot be
  deleted, and that a null fingerprint stays null. **No test framework dependency** — they run under any
  framework, and `doctor` enforces that. Nothing extra to install: they are in the package you referenced to
  implement the driver. Reasoning in `docs/DECISIONS.md` D15 and D26.
- **`DeploymentTargets`** — several providers coexist in one application and are selected by `Id`, with
  `ProcedureRunners` producing a runner for each over one shared history and state store.
- **`UnitKindDriver`** — the per-unit `kind` dispatch both shipped providers had hand-written, now in the
  framework so a third does not write it a third time.
- **`DeploymentArtifact.RequirePart`** — resolving an artifact part, with one voice for the failure instead
  of one per provider.
- **`DefinitionException`** — a base type for "the procedure or request is wrong", so a consumer can tell it
  from "the provider failed" without matching on message text.
- **`CustomUnits` — an application registers its own step as a unit kind inside a shipped provider.** Verify a
  migration applied, warm a cache, call a health endpoint that means something only to you: those used to live
  outside the procedure as code that ran after it, with no phase, no plan, no resume and no classification.
  Now they sit beside the vendor's units and get all four. `CustomUnits.Classifier` chains after the
  provider's via `FailureClassifiers.Chain`, so an application's transient failure can pause instead of
  ending a deployment that was fine. D19.
- **Storage is a kind and a connection.** `"json:/var/lib/app/state.json"`, `"sqlite:/var/lib/app.db"`,
  `"postgres:Host=db;…"`, `"s3://bucket/key"` — one string, resolvable from `appsettings.json` rather than
  branched on in code. `IStorageBackend` + `StorageBackends` + `cfg.UseState(descriptor)` /
  `cfg.UseHistory(descriptor)` / `cfg.AddStorage(backend)`. Only `json` ships, registered by default; every
  other kind is one a consumer registers, from a package or written themselves and upstreamed if it
  generalizes. A bare path is refused rather than guessed, and a kind must be two characters so a Windows
  path is not read as a drive-letter kind. D20.
- **`Procedure.Only(...)`** — a procedure narrowed to some of its units, in their original order. Terraform's
  `-target`, and the answer to "just push the website again": the source deployer had a dedicated method for
  one case of it because pushing a website takes seconds and reconciling three stacks takes minutes. Safer
  than Terraform's, because a subset of an ordered list is still ordered — there is no graph to skip. An
  unknown unit name is refused rather than ignored, and a narrowed run touches only its own units' state. D21.
- **`ValidateAsync` — check a whole procedure with no provider access at all.** No credentials, no network,
  nothing created, and every problem across every unit in one pass rather than the first one three units into
  a run that has already made things. Each provider implements it by running the same option and artifact
  resolution its `CreateAsync` runs, so the offline check and the real thing cannot diverge. D18.
- **`OutputsAsync` — what the deployment produced.** A URL, an endpoint, a generated name. Nothing surfaced
  these before, so an application deploying through Tyanor could not learn the address it had just created —
  which is the first consumer's entire job. Read from the provider, never from state.
- Both are **default** interface members. D16's claim that `UnitContext` makes additions additive was true of
  parameters, not of methods; a default meaning *I do not do that* is what makes a new capability additive
  for implementations outside this repository.
- **A teardown gets a plan.** `PlanAsync(procedure, request, RunKind.Destroy)` reports the units in the order
  they will go, which are already gone, and every resource the teardown will destroy — `Plan.Destroying`,
  `Plan.IsDestructive`. The destructive direction was the one without a preview. `Reconcile.DecideDestroy`
  is the pure function behind it, so the teardown shown and the teardown run come from one place.

### Before it shipped

Nothing below reached a user: 0.1.0 is the first release, so there was no version for any of it to break.
It is recorded anyway because a defect's reasoning outlives the defect — and because several of these are
mistakes the seams still allow someone else to make.

The one worth reading if you read only one: the contract suites had no negative tests, so a broken suite
would not have failed, it would have silently certified.

#### Changed

- **`IUnitDriver`'s six methods now take one `UnitContext`** (breaking) carrying the unit, the request,
  progress and cancellation. Only `AwaitSettledAsync` had a progress callback, which is right for a provider
  that polls a control plane and useless for one that does its work in `CreateAsync` — so copying a large
  directory and waiting out a stack deletion both reported nothing. Breaking once properly makes the next
  addition to the contract additive, including for implementers outside this repository. D16.
- **Registering a second `IDeploymentTarget` no longer silently changes which one deploys** (breaking, and
  the reason the rest of this exists). `AddTarget` registered `IDeploymentTarget` and the runner resolved it
  by type, so the last one registered won and there was no way to ask for a particular one — undiscoverable,
  because a plan would be computed against the wrong target too and would agree. Resolving `ProcedureRunner`
  with several targets registered now throws and names them.
- **`IDeploymentTarget.ValidateAsync` now takes `TargetCredentials?`** (breaking). Null means the target
  authenticates ambiently — this machine's user, an instance role, a context already selected. The
  non-nullable version asserted that every target has a key and a secret.
- **`DeploymentRequest.Option(unit, key)`** reads `"{unit}.{key}"` falling back to `"{key}"`, so a provider
  with heterogeneous units can configure each one without every provider inventing its own convention.
- **`DeploymentRequest.OptionSet(unit, prefix)`** gathers a whole GROUP of settings whose keys a provider
  cannot know in advance — CloudFormation parameters, Kubernetes labels, a process's environment. Encoding
  them into one value would re-invent a serialization format inside a string, which is how an untyped map
  becomes worse than typed fields rather than better.

- **Terraform's verbs, deliberately.** `ProcedureRunner.RemoveAsync` → `DestroyAsync`, `RunKind.Remove` →
  `RunKind.Destroy`, `Reconcile.DecideRemoval` → `DecideDestroy` (breaking), so the whole command set reads
  as `validate · plan · apply · destroy · refresh · output` — the same words for the same jobs, because
  inventing new ones only makes a reader translate. A *driver* still says `RemoveAsync`: it removes one unit
  rather than destroying a deployment, which is the same asymmetry Terraform has between its command and a
  provider's per-resource delete. The enum's stored value is unchanged, so existing run history still reads.
  D22.
- **`RunRecord.Resumable` removed** (breaking). It was a second name for `IsLive` with no second meaning, and
  it collided with `OperationOutcome.Resumable`, which means something genuinely different. Use `IsLive`.

#### Broken, and fixed

- **`MemoryTarget` silently deployed a dictionary entry for a unit kind it did not have.** A unit declaring
  `kind = "discovery"` with nothing registered under that name got the memory behaviour, while `LocalTarget`
  and `AwsTarget` refuse it and name the kinds they do have. So an adopter who forgot to bring their own units
  to a new platform got a green test suite and an exception in production — inverting the point of a test
  target (D24). **The test written to catch this could not fail**: it never awaited `Assert.ThrowsAsync`, so it
  asserted that a `Task` was not null. Both fixed; a unit declaring NO kind still gets memory behaviour, which
  is the case that keeps the ordinary usage one line.
- **`MemoryTarget` held its `CustomUnits` live while every real provider copies at construction.** A kind
  registered after the target was built therefore worked in a test and vanished in production. It copies now,
  and one test asserts the behaviour across both providers and the test target — because the risk was never
  the behaviour, it was the disagreement.
- **`CloudFormationPhases` and `AwsFailureClassifier` were public**, in an assembly whose csproj comment says
  the phase table and the classifier "should NOT be public API" and grants `InternalsVisibleTo` for exactly
  that reason. The local provider got the same call right, so this was drift between two providers that
  nothing compared. Now internal — one release later it would have been a breaking change.

- `README.md`, `docs/architecture/overview.md` and `Reconcile`'s XML docs still claimed there was no state
  file and no plan/diff — both reversed by D12 several commits earlier.
- **A destroy plan built without a state store reported "0 to destroy" and `IsDestructive` false**, for a run
  that was about to take everything away. A state store is optional, and the destroy count was gated behind
  one — but what a teardown will destroy is read entirely from the provider, and only *drift* genuinely needs
  state to compare against. This silently opened the confirmation gate the README tells operators to put in
  front of the one irreversible direction.
- **A resumed run had its `StartedAt` stamped over with the moment of the resume**, so one interrupted
  three-hour job reported as however long its last resume took. A resume continues a run; the record now
  keeps the moment that run began, which is the same property as keeping its id.
- **`StateDiff` wrote the "unknown is not equal" rule twice** — once in `Unchanged`, which exists to name it,
  and once inline in `ForUnit`, which is how two copies of a rule come to disagree. It also threw on a driver
  that reported one resource id twice, so the plan an operator runs to *find out* what is wrong was the thing
  that could not run. `UnitDriverContract` now has a check for duplicate ids, which is where that assumption
  belongs.
- **`ContentUnit.ValidateAsync` parsed `{unit}:{OutputKey}` references with its own copy** of the parse
  `ResolveAsync` uses at apply time — exactly the divergence D18 says validation must not have. One function
  now serves both. `StackUnit.ValidateAsync` also listed a capabilities check that could not fail, making
  validation look like it covered one more thing than it did.
- **`UseInMemoryState()` only wired the run history**, so deployment state still went to a file under the
  application's base directory — a method named for memory quietly writing to disk. It now registers both.
- **A unit deleted from the procedure stranded its infrastructure silently.** Every pass in the engine walks
  `procedure.Units` — the phase read, the drift comparison, the teardown — so removing a `ProcedureUnit` from
  the C# removed it from everything that looks, and what it had deployed went on existing: paid for,
  unmanaged, and mentioned by no plan in either direction. Deleting four lines of code leaked a database.
  `Plan.Orphaned` now names what state holds that the procedure no longer declares. Reported rather than
  destroyed, because the kind, options and artifact parts a driver would need were deleted with the unit —
  put the declaration back and run a narrowed destroy. A narrowed plan reports none, deliberately. D25.
- **`DeploymentState.Serial` could not do the job it was documented for.** D12 and D20 both lean on it for
  conditional writes, but `With(...)` incremented it — so the state handed to `Save` was always one ahead of
  what the store held, and a backend implementing the check as written would have refused EVERY save.
  `Refresh` was worse: it edits every unit before saving once, so the number ran ahead by the unit count. The
  serial is now the version a snapshot was READ at, and the STORE advances it. The contract suite asserted
  the old shape — and was satisfiable by a store that never advanced it at all — so it was rewritten.
- **State had no schema version while the run log did.** `FileRunHistory` had a DTO and a comment explaining
  that the domain type must be free to change while an older file still loads; `FileStateStore` serialized
  the record directly. The cheaper file to lose was the protected one. State now has the same DTO, a version,
  and a refusal to half-read a file written by a newer Tyanor.
- **An emptied content bucket still reported itself deployed.** `ContentUnit.PhaseAsync` asked only whether
  the BUCKET existed — but the bucket belongs to the stack that made it and outlives this unit's teardown by
  design. So a destroyed content unit read as `Ready`, `RefreshAsync` reported a phantom resource for it, a
  second teardown plan claimed there was still something to remove, and a bucket a stack had just created
  read as deployed before anything was uploaded. Empty now means Missing and owns nothing. **Found by running
  `UnitDriverContract` against the unit for the first time** — it failed four checks at once, which is the
  argument for the suite existing.
- **Deploying content before its stack blamed S3.** The raw `NoSuchBucket` sent an operator to look at their
  bucket rather than at their procedure; it now names the stack that creates it.
- **An unscoped `"path"` collapsed every local directory unit into one folder.** Options fall back from
  `"{unit}.{key}"` to `"{key}"`, which is what stops configuration being verbose — but a path is the unit's
  ADDRESS, and a shared address is not a default, it is every unit deploying on top of every other: the
  second to deploy prunes the first's releases and removing either removes both. This is the collision
  `Procedure` already refuses when two units share a name, reached through a different door.
  `DeploymentRequest.OwnOption(unit, key)` reads a setting with no fallback, and `local.path` uses it.
- **A pause whose reason this version does not recognise was explained as a failure.** `Explain` matched
  copies of the three known reasons' TEXT, so anything else — and `PauseReason` is documented as open —
  fell through to "the run failed", telling an operator their intact deployment was lost. It matches the
  values now, and an unknown pause still says the work is kept.
- **A run status a hand-edited history file could not be read as was deletable.** `Enum.TryParse` accepts a
  NUMBER, so `"Status": "9"` parsed to an undefined `RunStatus` that was neither Running nor Paused, read as
  not live, and defeated the guard that exists to protect a record we cannot classify.
- **`AddTyanor` now refuses state and the run log at one location.** They hold different shapes, so sharing
  a file does not fail loudly: each store reads the other's contents as its own type and writes the result
  back, and the two quietly destroy each other. Refused for the reason D20 refuses a bare path.
- **Two stores on one file, in one process, lost writes.** The lock belonged to the OBJECT, which quietly
  assumed a consumer keeps exactly one store per path — while the guide invites the opposite by writing
  `new FileStateStore(path)` at the point of use. Because a save rewrites the whole file, two instances
  saving two different deployments at once each read it, each added their own, and one deployment's record
  of what Tyanor owns vanished entirely. Twenty-four concurrent writers kept about half. The gate is now per
  FILE, in a non-generic holder — a static inside a generic type is per closed type, which would have given
  a state store and a run log on one path a gate each and reintroduced the bug by language rule.
- **A read racing a write threw `IOException`.** Reads bypassed the gate, and the atomic replace needs the
  destination to itself. An application polling state to show a deployment's progress while that deployment
  writes state per unit is the ordinary shape — and on a provider whose classifier does not recognise an
  IOException, which is every provider but the local one, the run would have failed outright. Reads take the
  gate now. Cross-PROCESS remains last-writer-wins, which is the documented boundary (D11, D20).
- **A name's refusal gave the wrong reason.** `Identifiers` checked "starts with a dot" before "contains
  `..`", so the sentence written for a parent-directory reference was unreachable for `..` itself — which
  was instead told about hidden files and provider bookkeeping — and only ever appeared for names like
  `v1..2`, where it is not true. Every one of those names is refused either way, so nothing but the message
  said the order mattered; the tests now pin which reason each name gets.
- **Three ways the composition root could be silently wrong**, all of them documented in comments and none
  of them tested: a history or state store the consumer SUPPLIED being overridden by the default (a plain
  `Add` in place of `TryAdd` and every run records to a file under the application's base directory
  instead of where they said); `ProcedureRunners` built without the configured state store, which costs
  nothing visible because runs still succeed and takes away drift, orphans and safe teardown; and
  registering the `json` backend explicitly producing two of it, which `StorageBackends` then refuses as a
  duplicate kind.
- **Cancelling a run failed to record that it had been cancelled.** The engine wrote the ending with the
  token that had just been cancelled, so any history honouring it — including the shipped `FileRunHistory`,
  whose gate takes the token — threw instead of writing. The run stayed recorded as `Running` with no
  reason, an operator could not tell a deliberate cancel from a process that died, and `PauseReason.External`
  never reached a record at all. On the failure path it was worse: a token cancelled around the same time as
  a failure lost the outcome entirely and left the run open for ever. Endings are now written with no token.
- **Cancelling before a run starts is now deterministic** — it leaves no record, rather than depending on
  which side of the opening history write the cancellation landed.
- **A destroy plan read deployment state it never used** — one round trip per plan whose answer was discarded.
- Public XML docs referenced the internal `Identifiers` type, so the rule they pointed at resolved to nothing
  in a consumer's IntelliSense. They now state the rule.
- **Progress had no frame of reference.** `ProgressReport.Percent` never said whose scale it was on: the
  engine emitted run-relative numbers and both providers emitted -1 for everything, so a ten-minute stack
  deploy showed no movement at all. A driver now reports 0–100 through its OWN unit and the engine rescales
  into the run, weighted by `ProcedureUnit.Weight`. -1 survives as -1.
- **`Plan.HasWorkInFlight` read the action rather than the phase**, so a teardown plan — where nothing ever
  attaches — reported an idle provider however busy it was, which made every teardown plan with a live run
  claim it had stalled and that nothing was in sync.
- **A prefix or unit name could point outside the deployment root** (breaking for anyone relying on it, which
  is nobody sane). Both flow into `Path.Combine(root, prefix, unit)` and, on teardown, into a recursive
  delete — so `"../../etc"` escaped. Both are now validated on construction and on `with`: letters, digits,
  `-`, `_`, `.`, no leading dot, no `..`, 255 characters. A `Procedure` also refuses duplicate unit names
  (case-insensitively — `Api` and `api` are one directory on Windows), no units at all, and a weight below
  one. `StackUnit` adds CloudFormation's stricter rule in its own words. D17.

- **A credential error sitting BESIDE another exception in an `AggregateException` classified as hard**, so
  the run ended instead of pausing and every unit already deployed was discarded. Both shipped classifiers
  walked the chain with `e = e.InnerException`, which on an aggregate returns only its FIRST inner exception
  — so any sibling was invisible. The contract suite did not catch it because its own check wrapped a single
  error in an aggregate, where following one link happens to be correct. Anything awaiting several operations
  together produces the shape that breaks it. Fixed by `FailureClassifiers.Walk`, which opens every branch,
  and pinned by a new contract check — *"A credential error beside a SIBLING in an aggregate still
  classifies"* — so an implementation written outside this repository is held to it too.

- **A page deleted from a website was never taken down, and the tool said the site was current.** The AWS
  `content` unit uploaded what the build produced and nothing else: it never removed objects the build had
  stopped producing, and its change check asked only *is every local file up there?*, which a bucket holding
  those files plus every deleted page satisfies. So the update reported no change, the plan called the
  deployment current, and the deleted page went on being served — permanently, because nothing else ever
  looks at it. A sync now makes the bucket **be** the build in both directions, pruning after the upload so
  an interruption leaves the site stale rather than half-gone. The local provider had always done this to
  its own files, which is what made the gap visible: one concept, two providers, only one of them
  implementing it.

  The prune brought one new way to lose a site, so it is guarded: **a build that produced no files at all is
  refused**, rather than converged on by emptying the bucket. An empty directory does describe an empty site,
  but a build step that failed quietly should not take a live website with it. The local provider needs no
  such guard — each build lands in its own release directory and the marker only moves once the copy is
  done, so an empty build costs one unused release and leaves what is serving untouched. D29, which also
  has the generalizable version: if your unit removes everything on a teardown, it owns the namespace, and a
  sync that does not prune is claiming otherwise on the one path where nobody checks.

#### Internal

- **The public API surface of every shipped assembly is a checked-in file** (`tests/ApiBaselines/`), rendered
  and compared by a test. Nothing else here could see an API change: the build succeeds, the tests pass, and a
  `public` that should have been `internal` ships permanently — after which narrowing it is itself the
  breaking change. It is a record rather than a rule: a deliberate change is `TYANOR_UPDATE_API=1 dotnet test`
  and a diff somebody reads. It found the three defects above within the hour. Reasoning in D27.

- **A teardown now has THREE answers, because some things do not come back.** A publish cannot be
  unpublished, an audit record cannot be withdrawn, a sent email cannot be recalled — yet `RemoveAsync` was
  documented as "remove and wait until it is gone" and `DecideDestroy` handed `Remove` to every phase that
  was not `Missing`. Such a unit could only lie or throw. `TASKS.md` item 4 had carried this as a known gap
  and predicted the shape of the answer; **D32** is that shape.
  - `IUnitDriver.IsRemovable(context)` defaults to true, so nothing broke. Say false and the destroy plan
    lists the unit under `Plan.Retained` **before anything runs**, `RemoveAsync` is never called, the
    teardown succeeds and says RETAINED out loud, and the resources are kept out of the "to destroy" count —
    the number lying in the direction that costs money.
  - **Its state is kept**, which is the half most likely to have been got wrong. Clearing it would make
    Tyanor forget it owns something still out there, and D25 identifies that as the worst thing state can
    do: an unowned resource is one no future plan mentions again.
  - `UnitDriverContract` grew with it, or the shape would have been untestable. A removable unit is held to
    disappearing after a remove; an irreversible one to SURVIVING, and to not having mislabelled itself — a
    driver that claims to be permanent and then vanishes is caught, because every destroy plan built on it
    would report RETAINED for something that actually goes.
  - It surfaced one C# rule worth writing down: a default interface member's mapping is fixed at the class
    that implements the interface, so a SUBCLASS declaring its own `IsRemovable` compiles and silently does
    nothing. That is why `StepUnitDriver` restates the interface's defaults as `virtual`, and it caught a
    test double in this very change.

- **The seams reviewed as a product in their own right.** Tyanor cannot ship a provider for every service on
  day one, so an adopting application has to build what it needs *ahead of* this repository and hand it back
  if it generalizes. Measured against that, the question is not only whether a seam is possible but what it
  COSTS and whether somebody can find out they got it wrong. Four things were not good enough, and only one
  of them was a missing feature. **D31.**
  - **`UnitPausedException`** — `PauseReason` always said a provider or a procedure may add its own reason,
    and nothing could: the engine produced `credentials` and `transient` from the failure classes and
    `external` only on cancellation. So an approval gate, a change window, or the deferred ACM/Route 53
    unit — whose whole model is "manual DNS is a pause that resumes" — had no way to exist. Deliberately not
    a fourth `FailureClass`: those are a provider's reading of an *error*, and a pause is not one. Never
    retried, excluded in the engine rather than trusted to each classifier.
  - **`StorageBackendContract`** — D20 says write the backend you need and hold it to the suites, but the two
    store suites cover what a backend OPENS, not the backend: descriptors, kinds, keeping two locations
    apart. The part that was actually the adopter's was the part nothing checked. It composes the other two,
    so one suite holds both the resolution and the stores, and four deliberately-broken backends prove it
    goes red.
  - **`StepUnitDriver`** — `IUnitDriver` asks for six methods because a unit that deploys infrastructure
    needs six. A step needs two and wrote the same four one-line stubs, which had happened in six places
    here including the worked example `adoption.md` puts in front of a first-time adopter. It also restates
    the two default interface members, which an editor will not offer as an `override`.
  - **A documentation defect worse than staleness.** D24, `guide.md` and `adoption.md` all said `MemoryTarget`
    hosts one kind of unit so a `CustomUnits` step cannot be registered in it. It takes `CustomUnits` and six
    tests drive units through it. All three were confidently wrong about the one harness for developing a
    unit before it meets a cloud — which is the entire point of D19's develop-here-then-upstream loop.

- **The AWS stack driver had never once been held to `UnitDriverContract`, and a check said otherwise.** The
  suite was constructed for it in exactly one place — inside the live deployment test, behind
  `TYANOR_LIVE_AWS`, which returns before doing anything when the variable is unset. Nothing has ever reached
  AWS from this repository, so the largest driver in it had run those checks zero times, while `doctor`
  reported "4 unit kinds, each held to 2 contract suites". The `providers` check could not tell RUNNING a
  suite from NAMING one in a file that returns early, so a gated file counted as coverage; it no longer does,
  and removing the new offline suite makes it say so.

  The reason it stayed gated was half right and had been stretched: a fake would have to encode
  CloudFormation's semantics, and a suite asserting those is this repository agreeing with itself. True of
  the rollbacks and the timing — and not true of the contract, which asks only about *our* driver. That a
  phase read changes nothing, that removing twice is fine, that an update over an unchanged deployment says
  so, that outputs stop answering once the unit is gone: none of those is a question about AWS, and each
  fails quietly. It now runs offline against a fake that models existence and nothing else, and a mutation of
  the no-updates path makes it fail. **This is the D23 line applied properly rather than as a blanket.**

- **`Plan.IsNoOp`'s only passing test used a state the API cannot produce.** It was pinned true against a
  plan built by hand with zero steps — which `PlanAsync` never returns, because `Procedure` refuses a
  procedure with no units. The property's real behaviour is deliberate and was pinned in the other direction
  (an apply over a settled deployment is not a no-op, because a `Ready` unit plans as `Update` and only the
  provider knows whether that changes anything), but the one reachable case where it IS true — a teardown of
  something already gone — had no test, and the summary read as though it covered the apply. Both fixed: the
  doc now says which direction it fires in and why the other cannot, and the real case has a test.

- **…and then the gate itself turned out to have a hole, which no baseline could have shown.** The renderer
  dropped every user-defined operator, including implicit and explicit conversions — the sort of member added
  without anyone thinking of it as API, and impossible to withdraw once shipped. The exclusion meant to keep
  them tested for a member named exactly `"op_"`, which no member ever is, so it was inert and the
  `op_Equality` entry in the boilerplate list below it was unreachable. **A baseline cannot check its own
  renderer**: a rendering rule that drops something produces a smaller file, and a smaller file is exactly
  what the baseline then records, agrees with for ever, and reports green. So the renderer now has tests of
  its own, against a type built to probe it — an operator is recorded, a conversion is recorded, a record's
  generated equality operators are not, and a property is still reported once through the property.

- **The local test harness disabled the retry it was testing.** `Sandbox` built its runner with
  `RetryPolicy(Attempts: 1)` — no retry at all — while the provider it exercises classifies a sharing
  violation as `Transient` precisely so the engine rides one out. So an ordinary OS hiccup, a directory
  still held by a just-exited process, failed the run instead of being retried. It surfaced as this suite
  failing roughly one run in three once a fourth test assembly increased the parallel load.
- **A release-count assertion depended on when the OS frees a directory**, and silently on the platform:
  deleting a folder that is a live process's working directory fails on Windows and succeeds everywhere
  else, so the expected number differed by OS. It now asserts what the test is named for — the new build
  lands in a DIFFERENT directory from the running one, and pruning clears the rest once nothing holds them
  — with the service stopped, so there is no timing to lose.

- **Every unit kind in both providers wrote the same `ValidateAsync` by hand** — a list, a loop over
  `(Action[])[…]`, a `catch (DefinitionException)` adding the message, a `Task.FromResult` to match the
  signature. Four copies, which is twice over the bar `UnitKindDriver` and `Registry<T>` were extracted on,
  and the standing question in `CLAUDE.md` answered itself: a provider written elsewhere would have to copy
  it. Now `UnitProblems` — `new UnitProblems().Check(() => Command(context)).Found()`. It catches
  `DefinitionException` and nothing else, deliberately: a resolver reaching for the network during a check
  documented to touch nothing is a defect, and swallowing it would report a clean procedure.
- **`FailureClassifiers.Walk` is the exception-chain walk written once**, after `LocalFailureClassifier`,
  `AwsFailureClassifier` and `MemoryTarget` had each written it — the third arrival being what settled it.
  `error-classification.md` calls reading only the outermost exception the most common way a classifier goes
  quietly wrong, so it is now a function rather than a thing to remember. It is also where the aggregate
  defect above got fixed for everyone at once.
- **`UnitContext.RequirePart(option)` finishes what `DeploymentArtifact.RequirePart` started.** Three unit
  kinds across the two providers each wrote the same two steps — read the option naming a part, then resolve
  it — and each wrote its own sentence for the first step, so an operator who forgot `source` was told three
  different things depending on where they deployed. That is precisely the defect the resolution below it was
  extracted to fix, one level up, and a fourth provider would have written a fourth sentence. The unified
  message also does something none of the three did: it names the parts the artifact actually carries, so the
  operator is told what they could have written. It throws `ArtifactException` rather than a provider's own
  type deliberately — an unset part option is not a fact about the provider, and `UnitProblems` collects it
  either way, so validation still reports it offline.
- **The "a live run record cannot be deleted" refusal was written twice and had already drifted.** One store
  told the operator what to do about it and the other stopped at the fact, so which sentence they got
  depended on where their history happened to live — and D20 exists to make a third store easy, which would
  have meant a third sentence. It is now `RunRecord.RefuseDeleteWhileLive()`. A method rather than a line in
  the interface docs, because this is a rule an implementation satisfies by OMISSION: a store that simply
  deletes passes every test that does not think to check, and the cost is stranded work with nothing left to
  say it is happening.
- **The local `directory` unit hashed the source tree twice on every changed deploy.** A fingerprint is a
  full read of every file in the tree, and the update computed one to decide whether to redeploy and then
  threw it away, so the materialize computed it again — doubling the I/O of the most expensive thing that
  unit does, on exactly the path where there IS a new build. Computed once and carried.

Not API, but the reasons are the kind that get rediscovered expensively.

- **Six test files had written the same two-line `Request()` helper**, now `Requests.Bare()` in
  `tests/Shared` — the same "written three times before it was written once" that produced `Suites`.
- **`docs/adoption.md` is compiled like the guide.** An adoption document rots faster than a guide: a guide
  is re-read by whoever changes the API, while an adoption document is read once, by someone new, who cannot
  tell that the sample they are copying stopped compiling two releases ago. Adding another such document is
  one line in `compiledSamples`.
- **`docs/providers.md` — every setting the two shipped providers read, and what each will not do.** The
  option names, defaults, phase tables, what each unit owns and produces, what a removal takes away, and both
  classifiers' real error codes existed only in the source and in scattered samples, so the way to find out
  whether `health.seconds` had a default was to read `ProcessUnit`. It is compiled like the other two, which
  matters more here than anywhere: the page is almost entirely option *names*, and a renamed constant is the
  one change nothing about a document would otherwise notice.
- **`DeploymentTargets` and `StorageBackends` were the same registry twice** — a case-insensitive dictionary,
  a blank key refused, a duplicate refused rather than resolved by order, a failure that names what IS
  registered. Two independent arrivals at one shape is the signal `UnitKindDriver` was extracted on (D15), so
  both now share one internal `Registry<T>`. The public types are unchanged.
- **`FileStateStore` and `FileRunHistory` each wrote out their own atomic read-modify-write**, which is one
  edit away from only one of them still being atomic. Both now use a shared `JsonFile<T>`.
- **The version was declared in two places** — `src/` and `tests/` — while `doctor` checked only that the
  changelog agreed with one of them. It now lives at the repository root alone, and `doctor` refuses any
  other project file that declares one. A claim is only as good as the half of it that is tested.
- **Five near-identical fake targets across the engine tests** became one, and then became none: the engine
  now tests against the shipped `MemoryTarget`. Keeping a private equivalent beside a public one would have
  been the same duplication this release keeps removing — and if the shipped target is not good enough for
  our own engine tests, it is not good enough for a consumer's.
- **The `providers` check crashed on a provider shape the framework explicitly supports.** A provider whose
  units are all the same kind of thing implements `IUnitDriver` directly and declares no kinds at all —
  `UnitKindDriver`'s own documentation says so. Given one, the check called `readFileSync` on a sentinel path
  named `.no-options` and died with a raw Node stack trace instead of a sentence. A gate that dies on a legal
  input is a gate people learn to skip.
- **`.gitignore` now keeps credentials out by construction** — `.env`, `.pem`, `.pfx`, `credentials`. The
  `sensitive` scan reads by extension, so a `.env` was never opened, and its unquoted `KEY=value` lines would
  not have matched the patterns anyway. This repo's live AWS test takes real keys from the environment, which
  makes a `.env` beside it the obvious convenience and the obvious way to publish a key permanently. Not
  committed at all beats scanned and hopefully caught; the limit is now stated in `devtools/README.md` rather
  than left to be discovered.
- **`architecture/overview.md` credited the wrong decision for the Terraform verbs** — D16, which is the
  destroy-gets-a-plan entry, rather than D22, which is the rename. Exactly the rot D22 was written to record:
  it exists because that rename shipped as a refactor and left three documents citing names that no longer
  existed. `doctor` checks that every cited `D<n>` exists, not that it is the right one, so this is the class
  of error only reading catches.
- **`LICENSE`** — MIT was claimed in the README and in every package, and the file was not there. `doctor`'s
  new `docs` check is what noticed, and now keeps noticing.
- **Packages carry their metadata**: repository and project URLs, tags, the README, and symbol packages.
- **`doctor` gained a `providers` check** — every shipped provider must run both contract suites, and every
  unit KIND must be named in a file that runs the driver contract. The add-provider skill already required
  both and nothing verified either; it is what found the content unit having none.
- **The guide's samples are compiled.** Every C# fence in `docs/guide.md` must appear verbatim in
  `tests/Tyanor.Docs.Tests`, which builds with the rest of the solution — so a renamed method breaks the
  build instead of rotting in a document a newcomer is trusting. It immediately found the guide teaching
  `if (cond) /* comment */;`, which does not compile under this repository's own warnings-as-errors.
- **`node devtools/dev.mjs release`** — the preconditions for cutting one, checked rather than remembered.
  A clean tree above all: `dotnet pack` stamps the current commit into every `.nuspec`, so packing with
  uncommitted changes ships packages whose recorded source is not the source inside them. Plus a changelog
  that names the version, and packages that actually contain their README and XML docs.
- **`.gitattributes`** — the repository never said what a line ending is, so two developers could hold
  genuinely different bytes. Not tidiness: the `docs` check parses markdown, and its first version matched a
  bare `
`, so a guide saved with CRLF matched zero fences and reported success having examined nothing.
- **Test helpers live in `tests/Shared`**, compiled into every test project with a global using. The
  xUnit adapter for contract-suite check names had been written three times, once per assembly — three
  chances to drift on the one helper whose job is making sure no check is silently skipped.
- **`docs/guide.md`** — the document the set was missing. README is a pitch, `overview.md` is a shape and
  `DECISIONS.md` is rationale; none of them walks someone from install to a deployment they can resume.
  Every sample in it is compiled against the real assemblies before it ships.
