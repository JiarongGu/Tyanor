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

### 2. `IUnitDriver` — five methods, no orchestration

| Method | Must do | Must NOT do |
|---|---|---|
| `PhaseAsync` | map YOUR status vocabulary onto `UnitPhase` | leak the raw status upward |
| `CreateAsync` | issue the create | wait for it (the engine waits, so Attach uses the same wait) |
| `UpdateAsync` | apply config; return `false` for "nothing to change" | treat no-change as an error |
| `RemoveAsync` | remove and wait until gone | fail when it is already gone |
| `AwaitSettledAsync` | poll to settled; report progress; throw if it settled badly | swallow a failure |

The phase mapping is the crux. Get these right:

- anything still converging and **not** rolling back → `Converging`
- rolling back / unwinding → `Unwinding`
- settled in a state your provider refuses to update → `Broken`
- settled and healthy → `Ready`
- absent → `Missing`

### 3. `IFailureClassifier`

Walk the whole `InnerException` chain. Classify on **codes**, never message text.

## Tests that must exist before the provider is trusted

1. **Phase mapping** — every real status string your provider emits → the expected `UnitPhase`. Table-driven.
   A mocked SDK cannot catch a status string you spelled wrong; only this can.
2. **Classification** — every credential code and every transient code you know of, plus one unrecognised
   error asserting it falls through to `Hard`.
3. **The inner-exception walk** — a credential error wrapped in a generic exception still classifies.

Live calls stay behind an env-gated integration test, skipped as a vacuous pass, so an ordinary run never
touches a cloud or spends money.

## Steps

1. `dotnet new classlib -o src/Tyanor.Providers.<Name>` and reference `Tyanor.Core`.
2. Write the classifier and its tests first — it is pure, it is where the subtle bugs are, and it needs no
   provider.
3. Write the driver. Keep every method boring; if one starts branching on run state, that logic belongs in
   the engine and probably already exists there.
4. Add the phase-mapping test table.
5. Add the env-gated live test.
6. Register the provider in the consuming app's composition root — Tyanor has no plugin discovery, on
   purpose: a deployment tool that loads code it found on disk is a security question nobody asked for.

## Related

- `../../rules/provider-boundary.md` · `../../rules/error-classification.md` ·
  `../../rules/reconcile-dont-mirror.md` · `docs/architecture/overview.md`
