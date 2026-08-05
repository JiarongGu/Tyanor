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

_No provider ships yet — see `TASKS.md` item 1._
