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
                      boundary    the neutral core names no vendor, in code or in a string
                      consumer    pack, then use the packages as a stranger does
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

**That last check used to cover a fifth of the citations.** It only counted a `D<n>` when the word
"decision" appeared within 120 characters, to avoid mistaking unrelated prose for a reference — so 44 of
~250 were validated and 206 were not, and a `D99` in an ordinary code comment passed cleanly. The caution
turned out to be unfounded: the word boundary and trailing-punctuation lookahead already exclude `D3D11`,
hex and identifiers, and broadening it produced zero false positives. It now checks every one, in `.mjs` and
`.yml` as well, and reports the count so the scale is visible.

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

### `consumer`

**Because some defects only exist across an assembly boundary.** `doctor` and `release` both compile against
the SOURCE TREE, so both can be green while the thing a consumer installs is wrong. This packs, creates a
throwaway project OUTSIDE the repository, restores the packed `.nupkg` into it, and runs
`devtools/consumer/Program.cs` against nothing but the public surface.

The defect that earned it: a default interface member that compiles, reads as overridden, and never runs,
because C# fixes an interface mapping at the class naming the interface. Every implementation in this
repository is on the inside of that boundary, so nothing here could reproduce it — a stranger's project on
the published 0.2.0 hit it on the first attempt (D39).

It was a RITUAL before it was a script — 0.1.0, 0.1.1 and 0.2.0 were each checked by hand this way, *after*
publishing. That is what made a fixable thing a shipped thing. The packages exist at pack time, so `release`
runs it now and hands over the folder it already packed rather than packing twice.

Two isolations are load-bearing, and without either it passes while testing the wrong bits:

- **Source mapping, not `<clear />`.** The library's own dependency lives upstream, so the local folder
  cannot be the only source. But adding upstream back would let a restore prefer a version of the library
  that is ALREADY published — testing the previous release while reporting on this one. Mapping says it
  exactly: these ids come from the packed folder and nowhere else.
- **A private packages folder**, not the global cache, which may already hold the version being cut.

Build and run are separate steps, because "the surface a consumer compiles against changed" and "it compiles
and then misbehaves" send you to different places.

### `boundary`

**This one replaces a compiler.** The core used to be its own assembly with no reference to any provider, so
a leak did not build. Merging the packages turned the boundary into a NAMESPACE, and `CLAUDE.md` said for
several releases that "nothing but reading will catch it now" — which is precisely the kind of claim this
repository keeps discovering to be false: one guarded only by people remembering.

The defect it exists for is the one that made the original code unportable. A "generic" `DeploymentRequest`
carried `CdkOutDir` and `WebDir`, so the neutral interface named an AWS tool and assumed a single-page app.
No second provider could have implemented it, and nobody noticed, because there was only ever one.

### `sources are text` (inside `doctor`, no separate command)

**Because the diff is the review here.** `tests/ApiBaselines/` says "a diff here IS the API review", the
commit convention rests on a readable diff, and a `## Unreleased` changelog section is written for a person.
A single control character makes git treat a file as binary, and from then on every diff of it reads
`Bin 23103 -> 26734 bytes`. Nothing breaks, nothing fails, and review silently stops happening on a file
nobody knows has opted out.

Found the way most things here are: it happened. A `" any"` sentinel in `MemoryTarget` went in as a NUL
rather than a space — it compiled, passed 876 tests, and turned the file binary, while the comment beside it
said "space" so reading would not have caught it either. See `docs/DECISIONS.md` D37.

**Comments are exempt, and that is the design rather than a concession.** The core is documented by naming
what it refuses — *only the AWS provider knows this is a CloudFormation assembly*, *the way `terraform
destroy` is* — so banning the words would ban the paragraphs that make the boundary teachable, and the check
would be suppressed within a month. What is banned is a vendor in the CODE, and separately in a STRING
LITERAL, because a string reaches an operator's screen.

Telling those apart needs a scanner rather than three regexes, and the first version proved it: extracting
string literals by pattern from the raw text found `"aws"` inside `/// <c>"aws"</c>` and reported eleven
files, none of them a defect. Comments and strings each contain the other's opener, so they have to be
consumed in one left-to-right pass.

It matches camel-cased words, which is how a leak actually arrives — and the first version could not, which
cost it its own headline example. The case-insensitive flag makes the `(?![a-z0-9])` guard reject `O` as
well, so `CdkOutDir` did not match. Planting that exact field is how it was found; the word is now made
case-insensitive letter by letter and the boundary guards stay case-sensitive, because telling upper from
lower is their whole job.

It found two real leaks on its first clean run — `StorageContracts` used `AWS::RDS::DBInstance` and
`AWS::S3::Bucket` as sample resource types, in a suite whose entire audience is people writing a store for
something that is not AWS.

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

**The release is the GitHub Action** (`.github/workflows/release.yml`), dispatched by hand from the Actions
tab — never a tag push, because publishing to a permanent immutable feed should not be a side effect. Give it
a `version` (or a `bump`), and it does everything below in an order chosen so that the irreversible step
comes last:

1. writes the version into `Directory.Build.props` **and stamps `## Unreleased` in the changelog with it**
2. `doctor`, then `release`, then `notes` — against the exact commit being shipped
3. packs, uploads the artifacts, publishes to NuGet via Trusted Publishing (OIDC, no stored key)
4. only now: commits the bookkeeping, tags, and drafts the GitHub release

**Nobody stamps a version by hand.** Between releases the repository holds the LAST RELEASED number and the
changelog heads at `## Unreleased`, so `main` never claims to be a version that was never published. The
action owned the number from the start but not the changelog headline, which meant every dispatch failed at
step 2 with the version already on disk — the gate was right and the workflow was doing half the job.

The commands still run locally, and that is what a rehearsal is:

```
npm run doctor                     # is the repo healthy?
node devtools/dev.mjs release      # is it shippable right now?
node devtools/dev.mjs pack         # → artifacts/
```

Locally they read the version rather than writing it, so `release` will refuse a tree heading at
"Unreleased" — which is the correct answer to "could I ship this commit as it stands?" and not a fault. To
rehearse the real thing, dispatch the action with **publish off**.

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
