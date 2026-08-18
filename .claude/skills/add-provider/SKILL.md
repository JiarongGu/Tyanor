---
name: add-provider
description: Add a Tyanor deployment provider (AWS, Kubernetes, SSH, local…) — the driver, the classifier, and the tests that must exist before it is trusted.
---

# Add a Provider

**Format**: `/add-provider <Name>` · e.g. `/add-provider Kubernetes`

## Purpose

A provider is the ONLY place vendor vocabulary lives. Everything else — ordering, reconcile, retry,
pause/fail, run history — already exists in the `Tyanor.Engine` namespace and must not be reimplemented. This skill
exists because the tempting mistake is to write a second engine inside a provider.

Read [`../../rules/provider-boundary.md`](../../rules/provider-boundary.md) and
[`../../rules/error-classification.md`](../../rules/error-classification.md) first.

**This works the same outside this repository.** A provider in your own solution references
`Tyanor`, registers in your composition root, and runs the same contract suites (D15). Nothing here is
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
    public Task<TargetIdentity> ValidateAsync(TargetCredentials? c, CancellationToken ct);
}
```

`ValidateAsync` makes a REAL call and returns the account/principal. "The fields are filled in" is not
validation, and showing the operator which account they are about to deploy into is the cheapest guard
against deploying to the wrong one.

Credentials are **nullable**: null means the target authenticates ambiently — this machine's user, an instance
role, an already-selected context. If yours needs credentials and gets none, return `TargetIdentity` with
`Ok: false` and say so rather than throwing (D13).

Take a `CustomUnits?` too, and pass it to your driver. It is two lines, and it is not optional in spirit: it
is how an application adds a service Tyanor does not support (D19), and how that service survives a move
between platforms — the adopter registers ONE `CustomUnits` instance and hands the same one to every target.
A provider that does not accept them silently makes itself the end of that road.

All three shipped targets take one, and each is tested doing it — including that an adopter's own failure
CLASSES travel too, so the same failure pauses on every platform rather than pausing on one and ending the
run on another.

### 2. `IUnitDriver` — six required, two optional, no orchestration

Every one takes a `UnitContext`: the unit, the request, progress and cancellation.

| Method | Must do | Must NOT do |
|---|---|---|
| `PhaseAsync` | map YOUR status vocabulary onto `UnitPhase` | change anything — it runs during a plan |
| `CreateAsync` | issue the create | wait for a CONTROL PLANE (the engine waits, so Attach uses the same wait) |
| `UpdateAsync` | apply config; return `false` for "nothing to change" | treat no-change as an error |
| `RemoveAsync` | remove and wait until gone | fail when it is already gone |
| `AwaitSettledAsync` | poll to settled; throw if it settled badly | swallow a failure |
| `RefreshAsync` | report what the unit OWNS, with stable ids | throw when the unit is absent — return empty |

Two more have defaults, so ignoring them costs nothing and implementing them is usually worth it:

| Method | Default | Implement it when |
|---|---|---|
| `ValidateAsync` | no problems | your unit has configuration to get wrong — **make no network calls** |
| `OutputsAsync` | nothing | your unit produces something a caller needs: a URL, an endpoint, a generated name |

`ValidateAsync` should run the same option and artifact resolution `CreateAsync` runs and collect the
`DefinitionException`s, rather than repeating the rules. Two copies of a rule is two rules, and they diverge
the first time one is edited (D18).

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

**Ask which sort of setting each option is.** `Option(unit, key)` falls back to a procedure-wide value,
which is what stops configuration being verbose. `OwnOption(unit, key)` does not — use it for anything that
IS the unit's identity: its path, its bucket, its port. A shared identity is not a default, it is two units
deploying on top of each other, and it fails by looking like it worked.

If every unit is the same kind of thing, implement `IUnitDriver` directly and ignore all of that.

### Failing on a bad definition

Use `DeploymentArtifact.RequirePart(name, ArtifactPart.Directory)` rather than resolving parts yourself —
it produces the same message every other provider produces for the same mistake. For your own configuration
errors, derive from `DefinitionException`, so a consumer can tell "you configured this wrongly" from "the
provider failed" without matching on message text. Do not classify these; returning null is correct.

## Tests that must exist before the provider is trusted

**Start with the contract suites** (`Tyanor.Testing`, in the package you already reference). They check
what the engine assumes and no signature
states, they are the same suites the built-in providers run, and `npm run doctor` refuses a provider in this
repository that does not run them — because the one kind that skipped them turned out to be failing four:

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

**Watch one fail before you trust one passing.** Break something on purpose — report `Ready` before anything
is deployed, or always return `true` from `UpdateAsync` — and check the suite goes red. A green run against a
suite you have never seen fail tells you nothing, which is why the suites themselves are tested that way.

**Declare `ExpectedOutputs` if your unit produces any.** Without it the outputs checks degenerate into
verifying that emptiness is empty — they cannot see whether the keys appear after a deploy or, the quiet
one, whether they go on answering after a remove because you read them from a stored copy instead of from
the target.

**One fixture per KIND.** `UnitDriverContract` tests one unit, so a provider with a directory and a process,
or a stack and a bucket, needs a fixture for each — and the one nobody wrote is the one that was broken. The
`providers` check cannot see this for you; it only knows whether the suite is run at all.

If a kind genuinely cannot be created in isolation because it depends on another unit's resource — an S3
bucket a stack makes — the fixture's reset supplies that resource and the contract still applies. That is
what shook out the phase bug above: "the bucket exists" and "this unit deployed something" are different
questions, and only the contract asked.

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

1. `dotnet new classlib -o src/Tyanor.Providers.<Name>` and reference `src/Tyanor/Tyanor.csproj`. (Outside
   this repository: `dotnet add package Tyanor`. Everything below is identical — including the contract
   suites, which arrive with it.)
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
