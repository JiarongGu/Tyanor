# devtools

One entry point for everything: `node devtools/dev.mjs <command>` (or `npm run dev -- <command>`).

**Before committing, run `npm run doctor`.** It is the whole checklist, so nobody has to remember it —
the step people forget is the step that breaks.

```
node devtools/dev.mjs doctor      build + test + every check below, one verdict
                      build       build the solution
                      test        run the tests
                      pack [dir]  produce the NuGet packages locally (default: artifacts/)
                      decisions   validate docs/DECISIONS.md
                      rules       validate .claude/rules
                      sensitive   scan for credentials
```

Nothing under `scripts/` names Tyanor. Project values live in **`project.config.mjs`** — to reuse this
toolkit in another .NET library repo, copy `devtools/` and edit that one file.

## What each check is for

Every check here exists because of a specific way this repo can quietly go wrong. None of them is
generic hygiene.

### `decisions`

A decisions log rots in one particular way: an entry is superseded, the entry that supersedes it says so,
and **the original says nothing**. A reader arrives at D1 and follows advice that was overturned.

That is not hypothetical — it happened here on day one, when D1 was superseded twice within hours. So the
check requires supersession to point **both ways**, and it caught five missing forward pointers the first
time it ran. It also verifies every decision has a date and that every `D<n>` cited anywhere in the repo
actually exists.

### `rules`

A rule missing from `RULES_INDEX.md` is invisible to the workflow that reads the index, which makes it a
file nobody will ever apply. A rule with a broken link is worse: authoritative-looking, and it sends the
reader nowhere. Also checks that each rule opens with a bold one-line statement of what it enforces, so a
reader can decide in seconds whether it applies to them.

### `sensitive`

Tyanor holds cloud credentials by nature, so a test fixture or a debugging paste is one `git add` from
being permanent — and git history is effectively unerasable once pushed. Patterns match real credential
shapes (AWS keys, private-key blocks, JWTs, bearer tokens) rather than the word "password", which would be
all noise. Tuned to be slightly noisy and cheap to silence: a false positive costs one
`tyanor:allow-secret` comment, and the opposite tuning is the one that leaks.

### `dependency-free core` and `version is single-sourced` (inside `doctor`)

Two claims the README makes out loud: that `Tyanor.Core` and `Tyanor.Engine` take **no package
dependencies**, and that the version ships from one place. A claim nobody verifies is one that quietly
stops being true — usually via a convenient `PackageReference` that seemed harmless.

If one of these is failing because the claim CHANGED deliberately, change the claim: update the README and
`project.config.mjs`. Do not silence the check.

## Adding a tool

1. Write `scripts/<name>.mjs`. Read project values from `project.config.mjs`; hardcode nothing.
2. Exit non-zero on failure — `doctor` aggregates exit codes.
3. Add a row to `TOOLS` in `dev.mjs`, and a section here saying **what goes wrong without it**. A tool
   whose section reads "checks the thing is correct" has not earned its place.
