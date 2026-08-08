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

### Changed

- **`IDeploymentTarget.ValidateAsync` now takes `TargetCredentials?`** (breaking). Null means the target
  authenticates ambiently — this machine's user, an instance role, a context already selected. The
  non-nullable version asserted that every target has a key and a secret.
- **`DeploymentRequest.Option(unit, key)`** reads `"{unit}.{key}"` falling back to `"{key}"`, so a provider
  with heterogeneous units can configure each one without every provider inventing its own convention.

### Fixed

- `README.md`, `docs/architecture/overview.md` and `Reconcile`'s XML docs still claimed there was no state
  file and no plan/diff — both reversed by D12 several commits earlier.
