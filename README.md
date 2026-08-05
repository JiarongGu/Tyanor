# Tyanor (天仪)

**Local-first operations and delivery for .NET.** Define a deployment once in C#; run it against pluggable
providers; resume it after anything goes wrong.

*天仪 — the celestial mechanism that brings order through coordinated operation.*

> **Status: early.** The engine is built and unit-tested; the first provider is being ported from a
> deployer that has run real infrastructure. See [`TASKS.md`](TASKS.md).

## What it is

```csharp
var procedure = new Procedure("site",
[
    new ProcedureUnit("db",  "Database"),
    new ProcedureUnit("api", "API"),
    new ProcedureUnit("web", "Website"),
]);

var runner = new ProcedureRunner(target, history);
var outcome = await runner.ApplyAsync(procedure, request, report);

if (outcome.Resumable)
    // credentials expired, or a transient blip outlasted the retries.
    // Fix it, call ApplyAsync again — the work so far is kept.
```

Units are applied in order and removed in reverse. Each one is reconciled against what the provider
reports *right now*, so a unit that already finished is skipped, one still converging is attached to, and
one that broke is remade.

## What makes it different

**There is no state file.** Tyanor records what was *attempted*; it reads what *exists* from the provider.
That is the deliberate fork from Terraform, and everything else follows from it:

- **Resume is a re-run.** No separate resume path, so there is nothing for the two to disagree about.
- **No drift, no locking, no `state rm`.** There is no local belief about the world to go stale.
- **A crash is uninteresting.** Nothing local was authoritative. The provider kept converging anyway.

**Every stop is classified.** An expired credential and a malformed template both end a run, but only one
means the work so far was wasted. Credentials and transient errors *pause* — resumable, progress kept.
Only a genuinely hard failure is terminal.

**Ordered units, not a dependency graph.** Data before compute before edge covers the overwhelming
majority of real deployments, and reverse-order teardown then falls out for free. The graph is where tools
like this become large; see [`docs/DECISIONS.md`](docs/DECISIONS.md) D3.

**It executes, it does not synthesize.** Tyanor consumes a pre-built artifact, so an operator can deploy
with no cloud SDK installed. Synthesis happens earlier, on a machine that has the toolchain.

## Layout

| | |
|---|---|
| `Tyanor.Core` | Contracts and the reconcile decision. No I/O, no provider, **no package dependencies**. |
| `Tyanor.Engine` | `ProcedureRunner` — ordering, reconcile, bounded retry, classified outcomes, history. |
| `Tyanor.Providers.*` | One per target. The only place vendor vocabulary exists. |

## Ecosystem

Four independent libraries; none depends on another.

| | |
|---|---|
| [Lyntai](https://github.com/JiarongGu/Lyntai) (灵台) | AI cognition |
| [Shenora](https://github.com/JiarongGu/Shenora) (神阙) | Desktop runtime |
| [Daoris](https://github.com/JiarongGu/Daoris) (道衍) | Engineering doctrine |
| **Tyanor** (天仪) | **Operations & delivery** |

> **Infrastructure changes. Operations endure.**

## License

MIT
