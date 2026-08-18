# Changelog

All packages version in lockstep from the repository-root `Directory.Build.props` (`VersionPrefix`).
From 1.0, SemVer 2.0 applies. Pre-1.0, a minor bump may carry a breaking change — each is called out here.

## 0.1.0

The first release, in two parts.

**[What ships](#what-ships)** is the whole of what 0.1.0 gives you. Read that if you are picking this up.

**[Before it shipped](#before-it-shipped)** is what a full review found on the way here — bugs, breaks and
cleanups that no user ever met, because there was no previous version to meet them in. It is kept, and kept
separate, because the reasoning is the point: most of those mistakes are ones a provider or storage backend
written outside this repository can still make.

Test counts quoted against a particular piece are what it brought with it. The suite as a whole is **623
tests**, none of which touch a cloud.

### What ships

#### The engine

- **`Tyanor.Core`** — units, the reconcile decision, failure classes, run state, the provider contracts.
  **`Tyanor.Engine`** — `ProcedureRunner`: ordered units, per-unit reconcile, bounded retry on transient
  errors only, classified pause/fail, run history and state.
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
  - The content unit is held to `UnitDriverContract` offline, against an in-memory bucket. S3 has no state
    machine to model — what you put is what you list — so the line D23 draws lets this one run without a
    cloud where the stack driver's cannot.
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
- **`Tyanor.Testing` — contract suites, so a provider or backend written anywhere can prove it behaves.**
  `UnitDriverContract`, `FailureClassifierContract`, `RunHistoryContract`, `StateStoreContract`. They check
  what the engine assumes and no signature states: that reading a phase changes nothing, that removing what
  is already gone is fine, that an update with nothing to change says so, that a resource keeps its identity
  across a refresh, that a wrapped credential error still classifies, that a live run record cannot be
  deleted, and that a null fingerprint stays null. **No package dependencies** — they run under any test
  framework, and `doctor` enforces that. Reasoning in `docs/DECISIONS.md` D15.
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

#### Internal

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

Not API, but the reasons are the kind that get rediscovered expensively.

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
