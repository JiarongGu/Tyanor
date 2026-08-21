# CLAUDE.md — Tyanor

Operating rules for every Claude Code session in this repo. Read top-to-bottom on session start. These
**override generic knowledge** — they encode decisions that were expensive to reach.

> **Tyanor (天仪) is a local-first operations and delivery platform.** A procedure — restore, build, test,
> package, publish, deploy, validate — is defined once in C# and executed against pluggable providers.
> Cloud providers are implementations; the operational doctrine does not change when the infrastructure
> does.

---

## 1. Read the rules before writing code

[`.claude/rules/RULES_INDEX.md`](.claude/rules/RULES_INDEX.md) lists every rule and when it applies. Read
the matching ones **first**. The four today are short and all load-bearing:

- **[`reconcile-dont-mirror.md`](.claude/rules/reconcile-dont-mirror.md)** — the RECONCILE loop reads the
  provider live and attaches to work in flight. (Its "no state file" opening is superseded by **D12**:
  Tyanor does keep one set of deployment state. Read D12 first.)
- **[`error-classification.md`](.claude/rules/error-classification.md)** — credentials / transient / hard.
- **[`units-not-graphs.md`](.claude/rules/units-not-graphs.md)** — ordered list, reverse teardown, no DAG.
- **[`provider-boundary.md`](.claude/rules/provider-boundary.md)** — the `Tyanor` namespace names no
  vendor. Since D26 that is a namespace, not an assembly, so the compiler no longer helps.

Reasoning behind each lives in [`docs/DECISIONS.md`](docs/DECISIONS.md). A decision there is not
re-litigated per feature — but it *can* be overturned by appending a new entry that says why.

## 2. What this library is, in one paragraph

An operator declares a `Procedure` (an ordered list of `ProcedureUnit`). `ProcedureRunner` walks it: for
each unit it asks the provider what phase that unit is in, calls `Reconcile.Decide`, and carries out the
one resulting action. Failures are classified by the provider into three classes, which the engine turns
into a pause (resumable) or a failure (terminal). Runs are recorded in `IRunHistory`; what Tyanor OWNS is
recorded in `IStateStore` and re-synced from reality by `RefreshAsync`, which is what makes a safe teardown
and honest add/change/destroy counts possible.

Every driver method takes a `UnitContext` — the unit, the request, progress and cancellation — so the
contract can grow without breaking every implementation again (D16).

The operator-facing surface is deliberately Terraform's, because it names the same jobs: **validate** (no
provider access at all), **plan** (either direction), **apply** (which is also the resume), **destroy**,
**refresh**, **output**. `procedure.Only("web")` narrows any of them, which is `-target` (D21). Where state
lives is a descriptor — `"sqlite:/var/lib/app.db"` — resolved through registered backends (D20).

**Resume is not a feature; it is the absence of one.** Applying and resuming are the same call, because
each unit is decided from what is true now rather than from what a previous run remembered.

## 3. Layout

**Three packages** — `Tyanor`, and one per provider (D26). Folders under `src/Tyanor/` mirror namespaces, so
a type's `using` is its path.

```
src/
  Tyanor/           ONE package, four namespaces.
    *.cs                  namespace Tyanor — contracts + the reconcile decision. No I/O, no provider.
    Engine/               namespace Tyanor.Engine — ProcedureRunner: ordering, reconcile, retry,
                          classified outcomes. Plus AddTyanor, the one thing that needs a dependency.
    Engine/State/         namespace Tyanor.Engine.State — run history + deployment state, file and
                          in-memory, and the storage backends.
    Testing/              namespace Tyanor.Testing — contract suites: what an IUnitDriver /
                          IFailureClassifier / IRunHistory / IStateStore must DO, runnable by whoever
                          wrote one, including outside this repo (D15). Plus MemoryTarget: a real
                          provider that deploys to a dictionary, so a CONSUMER can test its own code.
                          It passes the suites (D24).
  Tyanor.Providers.Local/   this machine: a directory from an artifact, a process, a health check.
                            The worked reference — read it before writing a second provider.
  Tyanor.Providers.Aws/     CloudFormation stacks + S3/CloudFront content. Ported; NOT yet run
                            against AWS — the live test is gated behind TYANOR_LIVE_AWS (D14).
  Tyanor.Providers.*/   one per target. The ONLY place vendor vocabulary exists.
tests/
  Tyanor.Tests/         pure tests for the decision logic. Always-on, no cloud, no mocks of an SDK.
  Tyanor.Docs.Tests/    every C# sample in the consumer-facing docs, compiled. `doctor` refuses a fence
                        in one of them that is not in here — so the two cannot drift.
  Shared/               compiled into EVERY test project by tests/Directory.Build.props, with a global
                        using. Put a helper here rather than copying it per assembly — `Suites` was
                        written three times before it was written once. `ApiSurface` renders a shipped
                        assembly's public API as text.
  ApiBaselines/         one file per shipped assembly: its public surface, checked in. A diff here IS the
                        API review. Regenerate with TYANOR_UPDATE_API=1 (D27).
  Tyanor.Providers.Local.Tests/   real files, real processes. A mocked filesystem would agree with
                                  whatever the driver believed, which is the opposite of a test.
  Tyanor.Providers.Aws.Tests/     the phase table and the classifier against the REAL strings; the driver's
                                  own control flow against recording fakes; one env-gated live deploy.
                                  A fake REPLAYS real strings, it never invents one (D23).
```

**`Tyanor` has a dependency BUDGET, and `doctor` enforces it in both directions:** exactly
`Microsoft.Extensions.DependencyInjection.Abstractions`, because `AddTyanor` is an extension method on
`IServiceCollection` and there is no other way to write one. Adding a second reference fails the build; so
does removing that one without updating the budget. Notably absent, and it must stay absent: **any test
framework** — the contract suites must run under whichever one the reader already has.

**The namespace boundary stopped being a compiler boundary at D26, and `doctor boundary` is what replaced
it.** A type in the `Tyanor` namespace that names a vendor is refused — in code and in string literals,
camel-cased identifiers included, because `CdkOutDir` is the historical defect. **Comments and doc comments
are exempt on purpose**: the core is documented by naming what it refuses, and a check that banned those
words would ban the paragraphs that make the boundary teachable. The other half — that nothing there needs a
package — is the dependency budget. This paragraph read "nothing but reading will catch it now" for several
releases, which is the shape of claim this repo keeps finding to be false.

**Build settings live in the root `Directory.Build.props`, and the version lives there alone.** `src/` adds
package metadata; `tests/` adds `IsPackable=false`. `doctor` refuses a second `<VersionPrefix>` anywhere,
because there were two of them for a while and the claim that there was one was checked at only one end.

**Every seam is public and third-party-implementable.** When adding to a seam, ask what an out-of-repo
implementer would have to copy — if the answer is anything, it belongs in the framework. Both shipped
providers hand-wrote the kind dispatch and artifact-part resolution before they became `UnitKindDriver` and
`RequirePart`; `DeploymentTargets` and `StorageBackends` each hand-wrote the same registry before it became
the internal `Registry<T>`. **Twice is the signal.** By the third time, the copies have already diverged in
one of the four things they all do.

The same answer has now been right three times, so reach for it before inventing a fourth shape:

| Someone needs | They write | They register | |
|---|---|---|---|
| a whole new target | `IDeploymentTarget` + `IUnitDriver` | `cfg.AddTarget(…)` | D15 |
| one step of their own | `StepUnitDriver` — two methods | `new AwsTarget(creds, new CustomUnits { … })` | D19, D31 |
| state somewhere else | `IStorageBackend` | `cfg.AddStorage(…)` | D20 |

**Build it where you need it, prove it with the contract suites, upstream it if it generalizes.** Nothing is
discovered from disk in any of the three (D6): authoring a plugin and *loading* one are different questions.

**Growing `IUnitDriver` costs everyone.** Adding a parameter is free — it goes on `UnitContext` (D16). Adding
a METHOD is not, so it arrives with a default meaning *I do not do that*, which is how `ValidateAsync`,
`OutputsAsync` (D18), `IsRemovable` (D32) and `IDeploymentTarget.SweepAsync` (D33) were all additive.

**Ask what a provider creates for ITSELF.** Not for a unit — for the deployment: a staging bucket, a folder
of pid files, a namespace. No unit can remove it (every unit uses it, so removing it reaches sideways), so
it needs `SweepAsync`, which runs after the last unit of a FULL destroy. Both shipped providers had one and
neither removed it, for months, while `adoption.md` promised a teardown left nothing — see D33.

## 4. Before you commit: `npm run doctor`

One command — build, test, and every check the repo can make cheaply. It exists so the checklist is not
something anyone has to remember, because the step people forget is the step that breaks.

```
npm run doctor                    build + test + the knowledge layer + the claims
node devtools/dev.mjs decisions   supersession points both ways; every cited D<n> exists
node devtools/dev.mjs rules       every rule indexed, every link resolves
node devtools/dev.mjs docs        every .md link and anchor resolves; the guide's samples compile
node devtools/dev.mjs providers   every provider AND every unit kind is held to the contract suites
node devtools/dev.mjs boundary    the neutral core names no vendor, in code or in a string
node devtools/dev.mjs sensitive   credential scan
```

Four of doctor's checks verify **claims this repository makes out loud** — that the library depends on
exactly the packages its budget names and no others, that the version ships from one place, that nothing
vendor-shaped has crossed into the neutral core, and that every source file is TEXT. The last one is there
because so much here rests on a readable diff — `tests/ApiBaselines/` calls a diff the API review — and one
control character makes git call a file binary and stop diffing it, silently. That happened (D37).

If one fails because the claim changed deliberately, change the claim. Do not silence the check. (D26 is
what that looks like done properly: the dependency-free claim genuinely stopped being true, so the check
became a narrower one that still fails, rather than a check that was deleted.)

**The samples in the three consumer-facing documents are compiled.** Every C# fence in `docs/guide.md`,
`docs/adoption.md` and `docs/providers.md` must appear verbatim in `tests/Tyanor.Docs.Tests`, which builds — so a renamed method
breaks the build rather than rotting quietly in a document a newcomer is trusting. Edit one and you must edit
the other; that is the point. Adding another such document is one line in `compiledSamples`.

**Cutting a release is the GitHub Action**, dispatched by hand with a version — `.github/workflows/release.yml`.
It writes the version into `Directory.Build.props` AND stamps `## Unreleased` in the changelog, runs `doctor`
and `release` against that, packs, publishes, and only then commits the bookkeeping and tags. So **nobody
stamps a version by hand**: between releases the repo holds the last released number and heads at
`## Unreleased`, and `main` never claims to be a version that was never published.

Locally, `doctor` then `node devtools/dev.mjs release` is the rehearsal. The second answers "is this
shippable right now?", which is mostly about things `doctor` has no reason to care about: a clean tree (the
commit is stamped into every package), and packages that actually contain their README and XML docs. It
reads the version rather than writing it, so it will refuse a tree heading at "Unreleased" — that is the
right answer to "could I ship this commit as it stands?", not a fault.

`devtools/README.md` says what goes wrong without each check. Everything is driven by
`devtools/project.config.mjs`; no tool names Tyanor.

## 5. Conventions

- **Never commit without explicit approval.** Branch off `main`; Conventional Commits.
- **`TreatWarningsAsErrors` is on, with XML docs required.** A public type without a `<summary>` fails the
  build. That is deliberate: this library's value is largely in *why*, and the why belongs where the reader
  is, not in a wiki.
- **Comments say why, never what.** `// increment i` is noise; `// Attach: re-issuing here starts a second
  conflicting operation` is the reason the line exists.
- **A public API change must show up in `tests/ApiBaselines/`.** The test fails, tells you what moved, and
  writes the new surface beside the baseline. If the change is deliberate, `TYANOR_UPDATE_API=1 dotnet test`
  and commit the diff — that diff is the review, and it is the only place an accidental `public` is visible.
  It found two real defects the hour it was added (D27), both of the shape this repo keeps hitting: behaviour
  defined by absence, guarded by a check that could not go red.
- **Test the decisions, not the plumbing.** The reconcile table and the classifiers are where a silent bug
  hides — a wrong decision looks like a differently-ordered deployment, and `Attach` fails by *succeeding*
  at something it should never have started.
- **A test that has never failed is decoration, so break the code and watch it go red.** That is how the
  forty AWS control-flow tests were accepted (D23) and how every check added since has been. **A mutation
  run lies in two ways, and both were hit in one afternoon** — so if a result surprises you, suspect the
  harness before the code:
  - **A mutation that does not COMPILE reads as a mutation nothing caught.** `TreatWarningsAsErrors` is on,
    so `if (false) return;` is an unreachable-code error, and a run that never built reports no test
    failures. Check the build succeeded. Inconclusive, never a pass. This produced a false "unguarded"
    finding for `A_NARROWED_destroy_does_not_sweep`, which two tests in fact pin.
  - **Restoring the file must give it a NEW mtime, or the next run tests the MUTANT.** A restore that
    preserves the original timestamp — `shutil.move` of a backup, `cp -p`, anything rename-based — leaves
    the source older than the compiled assembly, MSBuild skips the rebuild, and the mutated binary is what
    runs. That is worse than the first trap because it fails AFTER the experiment, in unrelated work: it
    presented as a passing test suddenly failing, and was nearly recorded as a regression in the change
    being made at the time. Restore by rewriting the file, or `dotnet build --no-incremental`.
- **Live provider calls stay behind an env-gated integration test**, skipped as a vacuous pass, so an
  ordinary run never touches a cloud or spends money.

## 6. Ecosystem

Four independent libraries; none depends on another.

| | |
|---|---|
| [Lyntai](https://github.com/JiarongGu/Lyntai) (灵台) | AI cognition — providers, routing, memory |
| [Shenora](https://github.com/JiarongGu/Shenora) (神阙) | Desktop runtime — the shell an app is built in |
| [Daoris](https://github.com/JiarongGu/Daoris) (道衍) | Engineering doctrine — how the work is done |
| **Tyanor** (天仪) | **Operations & delivery — how the work ships and runs** |

## 7. Where knowledge lives

| Information | Location |
|---|---|
| Enforced conventions | `.claude/rules/*.md` (indexed in `RULES_INDEX.md`) |
| Why a decision was made | `docs/DECISIONS.md` |
| How to USE the library, in order | `docs/guide.md` |
| How to ADOPT it into an app that already deploys | `docs/adoption.md` |
| Every setting a shipped provider reads | `docs/providers.md` |
| System shape | `docs/architecture/overview.md` |
| How to add a provider | `.claude/skills/add-provider/SKILL.md` |
| The public API, as a reviewable file | `tests/ApiBaselines/*.txt` |
| What is left to build | `TASKS.md` |
| What changed, and what broke | `CHANGELOG.md` |

Each answers a different question, and a change usually touches two. `guide.md` is the one that goes stale
invisibly — nothing checks that a code sample still compiles, so when a signature changes, look there.
