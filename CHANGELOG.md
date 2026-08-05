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

_No provider ships yet — see `TASKS.md` item 1._
