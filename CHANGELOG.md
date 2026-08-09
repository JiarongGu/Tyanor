# Changelog

All packages version in lockstep from `src/Directory.Build.props` (`VersionPrefix`).
From 1.0, SemVer 2.0 applies. Pre-1.0, a minor bump may carry a breaking change — each is called out here.

## Unreleased

### Added

- **Initial engine.** `Tyanor.Core` (units, the reconcile decision, failure classes, run state, the
  provider contracts) and `Tyanor.Engine` (`ProcedureRunner` — ordered units, per-unit reconcile, bounded
  retry on transient errors only, classified pause/fail, run history). 24 tests.
- **The doctrine, extracted rather than invented**: ported from a deployer that ran real infrastructure,
  survived a crash and rebuild mid-deploy, and resumed to completion. Reasoning in `docs/DECISIONS.md`.

- **Run state, at a location the consumer chooses.** `AddTyanor(cfg => cfg.UseFileState(path))`,
  `TyanorOptions.StatePath`, `FileRunHistory` (JSON, atomic write-then-replace, refuses to delete a live
  run) and `InMemoryRunHistory`. The default is durable — an in-memory default would look like it worked
  until the moment resume mattered. SQLite / Postgres / S3 backends are `TASKS.md` item 3.
- **A planning phase.** `ProcedureRunner.PlanAsync` returns what an apply WOULD do — created, replaced, or
  already in flight — derived from the provider rather than from a stored model, so it cannot go stale the
  way a state-file plan can. It is a forecast and says which two things it cannot know.

- **devtools.** `npm run doctor` — build, test, and the checks that keep this repo honest: supersession in
  `DECISIONS.md` must point both ways (it found five missing forward pointers on its first run), every rule
  indexed and linked, a credential scan, and the two architectural claims the README makes out loud.

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
  - **Not run against AWS.** The pure logic is tested; the SDK plumbing is unverified in this repo. The live
    test deploys a free single-resource stack and is gated behind `TYANOR_LIVE_AWS`.

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
- **A teardown gets a plan.** `PlanAsync(procedure, request, RunKind.Remove)` reports the units in the order
  they will go, which are already gone, and every resource the teardown will destroy — `Plan.Destroying`,
  `Plan.IsDestructive`. The destructive direction was the one without a preview. `Reconcile.DecideRemoval`
  is the pure function behind it, so the teardown shown and the teardown run come from one place.

### Changed

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

### Fixed

- `README.md`, `docs/architecture/overview.md` and `Reconcile`'s XML docs still claimed there was no state
  file and no plan/diff — both reversed by D12 several commits earlier.
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
