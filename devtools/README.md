# devtools

One entry point for everything: `node devtools/dev.mjs <command>` (or `npm run dev -- <command>`).

**Before committing, run `npm run doctor`.** It is the whole checklist, so nobody has to remember it —
the step people forget is the step that breaks.

```
node devtools/dev.mjs doctor      build + test + every check below, one verdict
                      release     are we shippable RIGHT NOW? (see below)
                      build       build the solution
                      test        run the tests
                      pack [dir]  produce the NuGet packages locally (default: artifacts/)
                      decisions   validate docs/DECISIONS.md
                      rules       validate .claude/rules
                      docs        validate every .md — links, anchors, required documents
                      providers   every shipped provider is held to the contract suites
                      sensitive   scan for credentials
```

Nothing under `scripts/` names Tyanor — including the marker that silences a `sensitive` finding, which is
`allowSecret` in the config for exactly that reason. Project values live in **`project.config.mjs`**: to
reuse this toolkit in another .NET library repo, copy `devtools/` and edit that one file.

## What each check is for

Every check here exists because of a specific way this repo can quietly go wrong. None of them is
generic hygiene.

### `decisions`

A decisions log rots in one particular way: an entry is superseded, the entry that supersedes it says so,
and **the original says nothing**. A reader arrives at D1 and follows advice that was overturned.

That is not hypothetical — it happened here on day one, when D1 was superseded twice within hours. So the
check requires supersession to point **both ways**, and it caught five missing forward pointers the first
time it ran. It also verifies every decision has a date and that every `D<n>` cited anywhere in the repo
actually exists — which caught a source comment citing D20 before D20 had been written.

Once the log grew past twenty entries it got an index of hand-written anchors, so the check also verifies
every in-page link resolves to a heading that exists. A reworded title otherwise leaves the index pointing
nowhere, which is worse than having no index because it still looks like navigation.

### `rules`

A rule missing from `RULES_INDEX.md` is invisible to the workflow that reads the index, which makes it a
file nobody will ever apply. A rule with a broken link is worse: authoritative-looking, and it sends the
reader nowhere. Also checks that each rule opens with a bold one-line statement of what it enforces, so a
reader can decide in seconds whether it applies to them.

### `docs`

`decisions` already checked that one file's hand-written index resolved, because that index rotted the first
time a title was reworded. Nothing checked the other thirteen documents — so a moved page left the README
pointing nowhere and the guide's table of contents pointing at headings that had been renamed, and both went
on looking like navigation.

A broken link is worse than a missing one: it is authoritative-looking, and the reader only finds out after
they have trusted it. So this resolves every relative link across every `.md`, every in-page anchor against
that file's own headings, and the documents `requiredDocs` says must exist — which is how `LICENSE` was found
absent while the README and every package claimed MIT.

It also refuses a C# sample in `docs/guide.md` that is not present, verbatim, in `tests/Tyanor.Docs.Tests`.
A fenced code block is the part of a document nothing can invalidate — rename a method and the prose keeps
confidently teaching the old one — so the guide's samples are the same text as a project that builds. They
were being compiled by hand, which is another way of saying they were going to stop being compiled.

Two details it was nearly shipped without, both of the same species. It matches `
?
`, because the first
version required a bare newline and a guide saved with Windows line endings matched ZERO fences and reported
success having examined nothing. And it fails when a configured document has no samples at all, for the same
reason: a check that passes when it cannot read its input is worse than no check, because it is believed.

### `providers`

The add-provider skill lists the tests that must exist "before the provider is trusted", and the first of
them is the contract suites. Nothing verified that, and the gap was real: the AWS provider has two unit kinds
and only ONE was ever run through `UnitDriverContract`. When the other finally was, it failed four checks —
all the same defect, and the worst of them meant a destroyed unit still reported itself deployed.

It looks for the suites being CONSTRUCTED, not mentioned. The first version matched the bare name and passed
on a comment that said "UnitDriverContract found it", which is a check satisfiable by talking about the thing
instead of doing it.

It checks every unit KIND too, which is where the real gap was. `UnitDriverContract` tests ONE unit, so a
provider with two kinds needs two fixtures — and the kind nobody wrote one for is the kind that turned out to
be broken. A kind is a `public const string XxxKind` beside the provider, and the constant must appear in a
file that also builds the driver contract; named anywhere else it does not count.

The pattern is `\w+Kind`, not `\w*Kind`: the latter also matched the option name `Kind` itself, which then
passed because `Kind` is a substring of `DirectoryKind`. It reported six kinds where there are four, which is
how you notice a check is not reading what it thinks it is.

### `sensitive`

Tyanor holds cloud credentials by nature, so a test fixture or a debugging paste is one `git add` from
being permanent — and git history is effectively unerasable once pushed. Patterns match real credential
shapes (AWS keys, private-key blocks, JWTs, bearer tokens) rather than the word "password", which would be
all noise. Tuned to be slightly noisy and cheap to silence: a false positive costs one
`tyanor:allow-secret` comment, and the opposite tuning is the one that leaks.

**The limit, stated rather than left to be discovered: it reads by EXTENSION**, so a `.env` — the single
most likely place for a real credential — is never opened, and its unquoted `KEY=value` lines would not
match these patterns anyway. That gap is closed by `.gitignore` instead, which is the stronger answer for
this particular file: not committed at all beats scanned and hopefully caught. Widen the patterns only
with a real finding in hand; a scanner that looks like it covers a format it cannot read is worse than one
that says where it stops.

### `test` (inside `doctor`)

Reports **every** test project's summary, not the first. With one test project those are the same thing;
with two, reading only the first means a provider suite failing hides behind a core suite passing — and the
verdict line would still say which check failed, but the detail under it would name the wrong assembly.

### `dependency budget` and `version is single-sourced` (inside `doctor`)

Two claims the README makes out loud: exactly which packages the library depends on, and that the version
ships from one place. A claim nobody verifies is one that quietly stops being true — usually via a convenient
`PackageReference` that seemed harmless.

The budget is an allowlist per project, and it is checked in **both** directions: a reference that is not
budgeted means the README now overstates how little is needed, and a budgeted reference that has gone means
the budget describes a dependency nobody has. Every claim this repository has caught rotting was one checked
at a single end, so a new one arrives checked at both.

It replaced a `dependencyFree` list of three projects that took nothing at all. Merging the DI wiring into the
library (D26) gave it one real dependency, and an allowlist was the honest response — what mattered was never
zero, it was that nothing arrives unnoticed. Note what the budget still excludes: any test framework. The
contract suites are meant to run under whatever the implementer already has, and one convenient `xunit`
reference would silently make that untrue for everyone using NUnit.

The version check has two halves, and it used to have one. It compares the changelog headline against
`VersionPrefix` — *and* it refuses any project file other than the configured one that declares a
`VersionPrefix` at all. Without the second half, "single-sourced" was a claim about a thing that was
declared twice: `src/` and `tests/` each carried a copy, identical and free to drift, with nothing watching.

If one of these is failing because the claim CHANGED deliberately, change the claim: update the README and
`project.config.mjs`. Do not silence the check.

## Cutting a release

```
npm run doctor                     # is the repo healthy?
node devtools/dev.mjs release      # is it shippable right now?
node devtools/dev.mjs pack         # → artifacts/
dotnet nuget push "artifacts/*.nupkg" --source nuget.org --api-key …
```

`release` is the second question, and it is a different one. It checks what `doctor` does not:

- **The working tree is clean.** `dotnet pack` stamps the CURRENT commit into every `.nuspec`, so packing
  with uncommitted changes ships packages whose recorded source is not the source inside them — SourceLink
  then sends a debugger to code that never built the binary. This was found by opening a `.nupkg` for the
  first time, which nobody had done.
- **The changelog names the version being cut**, and is not still headed "Unreleased".
- **Every configured package builds and contains what a consumer needs** — the README that becomes its page
  on nuget.org, and the XML documentation that is most of this library's value.

Its own first version shelled out to `tar` to read a `.nupkg`, which cannot read a zip on every platform, so
it reported every package as missing its README and docs when all of them had both. It reads the
zip's entry names directly now, and says so loudly if it cannot read a package at all rather than concluding
the files are absent. A check that cannot read its input must not report an answer.

## Adding a tool

1. Write `scripts/<name>.mjs`. Read project values from `project.config.mjs`; hardcode nothing.
2. Exit non-zero on failure — `doctor` aggregates exit codes.
3. Add a row to `TOOLS` in `dev.mjs`, and a section here saying **what goes wrong without it**. A tool
   whose section reads "checks the thing is correct" has not earned its place.
