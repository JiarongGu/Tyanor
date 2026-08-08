---
name: add-provider
description: Add a Tyanor deployment provider (AWS, Kubernetes, SSH, local…) — the driver, the classifier, and the tests that must exist before it is trusted.
---

# Add a Provider

**Format**: `/add-provider <Name>` · e.g. `/add-provider Kubernetes`

## Purpose

A provider is the ONLY place vendor vocabulary lives. Everything else — ordering, reconcile, retry,
pause/fail, run history — already exists in `Tyanor.Engine` and must not be reimplemented. This skill
exists because the tempting mistake is to write a second engine inside a provider.

Read [`../../rules/provider-boundary.md`](../../rules/provider-boundary.md) and
[`../../rules/error-classification.md`](../../rules/error-classification.md) first.

**This works the same outside this repository.** A provider in your own solution references
`Tyanor.Core`, registers in your composition root, and runs the same contract suites (D15). Nothing here is
privileged to the built-in ones. If yours passes the contracts and a consumer needs it, that is also the
path for adopting it into this repository.

## What you write

A project `src/Tyanor.Providers.<Name>/` with exactly three things:

### 1. `IDeploymentTarget`

```csharp
public sealed class <Name>Target : IDeploymentTarget
{
    public string Id => "<name>";                       // stable, lowercase
    public IUnitDriver Driver { get; }
    public IFailureClassifier Classifier { get; }
    public Task<TargetIdentity> ValidateAsync(TargetCredentials c, CancellationToken ct);
}
```

`ValidateAsync` makes a REAL call and returns the account/principal. "The fields are filled in" is not
validation, and showing the operator which account they are about to deploy into is the cheapest guard
against deploying to the wrong one.

### 2. `IUnitDriver` — six methods, no orchestration

Every one takes a `UnitContext`: the unit, the request, progress and cancellation.

| Method | Must do | Must NOT do |
|---|---|---|
| `PhaseAsync` | map YOUR status vocabulary onto `UnitPhase` | change anything — it runs during a plan |
| `CreateAsync` | issue the create | wait for a CONTROL PLANE (the engine waits, so Attach uses the same wait) |
| `UpdateAsync` | apply config; return `false` for "nothing to change" | treat no-change as an error |
| `RemoveAsync` | remove and wait until gone | fail when it is already gone |
| `AwaitSettledAsync` | poll to settled; throw if it settled badly | swallow a failure |
| `RefreshAsync` | report what the unit OWNS, with stable ids | throw when the unit is absent — return empty |

**Report progress from wherever the work actually is.** If your provider has no control plane, the work is
in `CreateAsync` and `RemoveAsync`, not in the wait — `context.Progress("copied 412 of 900 files…", 46)`.
The percent is through YOUR unit; the engine rescales it into the run. Use -1 when there is no honest
fraction; it stays -1 rather than being turned into a number.

The phase mapping is the crux. Get these right:

- anything still converging and **not** rolling back → `Converging`
- rolling back / unwinding → `Unwinding`
- settled in a state your provider refuses to update → `Broken`
- settled and healthy → `Ready`
- absent → `Missing`

### 3. `IFailureClassifier`

Walk the whole `InnerException` chain. Classify on **codes**, never message text.

### Heterogeneous units

If your provider deploys more than one KIND of thing — a directory and a process, a stack and a bucket —
derive from `UnitKindDriver` instead of writing the dispatch. Each unit declares its kind with a per-unit
option, and `DeploymentRequest.Option(unit, key)` reads it. Do not add a default kind: guessing deploys
something the operator never described.

If every unit is the same kind of thing, implement `IUnitDriver` directly and ignore all of that.

### Failing on a bad definition

Use `DeploymentArtifact.RequirePart(name, ArtifactPart.Directory)` rather than resolving parts yourself —
it produces the same message every other provider produces for the same mistake. For your own configuration
errors, derive from `DefinitionException`, so a consumer can tell "you configured this wrongly" from "the
provider failed" without matching on message text. Do not classify these; returning null is correct.

## Tests that must exist before the provider is trusted

**Start with the contract suites** (`Tyanor.Testing`). They check what the engine assumes and no signature
states, and they are the same suites the built-in providers run:

```csharp
[Fact]
public Task My_driver_satisfies_the_contract() =>
    new UnitDriverContract(new MyFixture()).AssertAllAsync();

[Fact]
public Task My_classifier_satisfies_the_contract() =>
    new FailureClassifierContract(new MyClassifierFixture()).AssertAllAsync();
```

Point the driver fixture at something real and disposable. A stub answers by agreeing with whatever your
driver believes, which is the failure the suite exists to catch.

Then the things a generic suite cannot know:

1. **Phase mapping** — every real status string your provider emits → the expected `UnitPhase`. Table-driven.
   A mocked SDK cannot catch a status string you spelled wrong; only this can. If your SDK exposes the
   statuses as an enum, enumerate it by reflection and fail when one is missing from your table — that is
   what stops it rotting on the next SDK upgrade.
2. **Classification** — every credential code and every transient code you know of, supplied to
   `FailureClassifierContract` as REAL exceptions with the codes your provider genuinely sets.
3. **Anything the contract cannot reach** — a phase that only occurs mid-rollback, a status pair that means
   opposite things.

Live calls stay behind an env-gated integration test, skipped as a vacuous pass, so an ordinary run never
touches a cloud or spends money. Gate the driver contract there too if it needs a real target.

## Steps

1. `dotnet new classlib -o src/Tyanor.Providers.<Name>` and reference `Tyanor.Core`. (Outside this
   repository: reference the `Tyanor.Core` package. Everything below is identical.)
2. Write the classifier and its tests first — it is pure, it is where the subtle bugs are, and it needs no
   provider. Run `FailureClassifierContract` against it.
3. Write the driver. Keep every method boring; if one starts branching on run state, that logic belongs in
   the engine and probably already exists there.
4. Run `UnitDriverContract` against it, pointed at something real and disposable.
5. Add the phase-mapping test table.
6. Add the env-gated live test.
7. Register the provider in the consuming app's composition root — `cfg.AddTarget(new <Name>Target(…))`.
   Several providers coexist and are selected by `Id`. Tyanor has no plugin *discovery*, on purpose: a
   deployment tool that loads code it found on disk is a security question nobody asked for (D6). Writing
   and registering your own is entirely supported (D15) — those are different questions.

## Related

- `../../rules/provider-boundary.md` · `../../rules/error-classification.md` ·
  `../../rules/reconcile-dont-mirror.md` · `docs/architecture/overview.md`
