# Tyanor (天仪)

**Local-first operations and delivery for .NET.** Define a deployment once in C#; run it against pluggable
providers; resume it after anything goes wrong.

*天仪 — the celestial mechanism that brings order through coordinated operation.*

> **Status: early.** The engine is built and tested, and two providers ship: `Tyanor.Providers.Local`
> (a self-hosted server on a machine) and `Tyanor.Providers.Aws` (CloudFormation and S3/CloudFront, ported
> from a deployer that has run real infrastructure). The AWS provider's pure logic is tested against the
> real status and error strings; **its SDK calls have not yet been run against AWS** — the live test is
> gated behind `TYANOR_LIVE_AWS`. See [`TASKS.md`](TASKS.md) and [D14](docs/DECISIONS.md).

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

## The idea in one table

|  | *What* to deploy | *How* to deploy it |
|---|---|---|
| Terraform | HCL — a DSL to learn | a real engine: plan, converge, providers |
| AWS CDK | real code — typed, refactorable | delegated to CloudFormation |
| **Tyanor** | **real C#** | **its own engine** — reconcile, classify, resume |

Terraform's mechanics, CDK's authoring, and none of the resource graph. See
[`docs/DECISIONS.md`](docs/DECISIONS.md) D8 for where that line is drawn and why.

## What makes it different

**Every decision is read from the provider, never from a file.** A run does not remember what it was
doing; each unit is reconciled against what the target reports *right now*. That is the deliberate fork
from Terraform, and three things follow from it:

- **Resume is a re-run.** No separate resume path, so there is nothing for the two to disagree about.
- **A crash is uninteresting.** Nothing local was authoritative. The provider kept converging anyway.
- **A stale record costs a wrong count, never a wrong action** — which is what makes the state below
  affordable, and repairable by re-reading instead of by surgery on the tool's bookkeeping.

**One set of state, and it answers ownership.** Tyanor records what it *owns*, per unit, wherever you put
it — because a provider working with raw resources cannot tell you what Tyanor created, and without that a
teardown cannot distinguish it from what was already there. `RefreshAsync` re-reads reality and rewrites
state to match ([D12](docs/DECISIONS.md)).

**Plans are a safety gate on your infrastructure.** `PlanAsync` asks the target what each unit's phase is
and runs the same decision the apply will run, then compares recorded state against a live refresh:

```csharp
var plan = await runner.PlanAsync(procedure, request);
Console.WriteLine(plan.Summary);   // "3 to add, 1 to change, 0 to destroy"
if (plan.IsDestructive)          /* ask before taking something away */;
if (plan.HasWorkInFlight)        /* someone else is mid-deploy — applying will attach to it */;
if (plan.HasStalledRun)          /* a run is recorded live but nothing is converging */;
```

**A teardown gets one too** — it is the direction that is not recoverable by running it again, so it is the
one that most needs a gate:

```csharp
var teardown = await runner.PlanAsync(procedure, request, RunKind.Remove);
Console.WriteLine(teardown.Summary);        // "0 to add, 0 to change, 12 to destroy"
foreach (var step in teardown.Steps) Console.WriteLine(step);   // in the order they will go
```

A plan is a forecast and says which two things it honestly cannot know.

**A library, not a service.** `Tyanor.Core` and `Tyanor.Engine` take **no package dependencies**. There is
no daemon, no CLI, no ambient state and no background thread. DI is a separate, optional package — the
minimal path is three lines with no container:

```csharp
var runner = new ProcedureRunner(target, new FileRunHistory("runs.json"));
var plan   = await runner.PlanAsync(procedure, request);
await runner.ApplyAsync(procedure, request);          // progress callback optional
```

**State lives where you put it.**

```csharp
services.AddTyanor(cfg =>                            // optional package, if you use a container
{
    cfg.UseFileState("/var/lib/myapp/runs.json");    // or SQLite, Postgres, S3, your own IRunHistory
    cfg.AddTarget(new AwsTarget(credentials));
});
```

Share that store and a plan can **see** runs from other machines — including one that stalled because the
machine running it went away. Note the limit, stated rather than implied: two machines writing at the same
instant are not coordinated, so divergence is shown rather than resolved. Resolving it is yours, because
the right answer depends on facts Tyanor does not have ([D12](docs/DECISIONS.md)).

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
| `Tyanor.Engine` | `ProcedureRunner` — ordering, reconcile, bounded retry, classified outcomes, history, state. |
| `Tyanor.Testing` | Contract suites — runnable proof an implementation behaves as the engine assumes. **No package dependencies.** |
| `Tyanor.Providers.Local` | This machine: a directory from an artifact, a process run out of it, a health check. |
| `Tyanor.Providers.Aws` | CloudFormation stacks, and website files in S3 behind CloudFront. |
| `Tyanor.Providers.*` | One per target. The only place vendor vocabulary exists. |

Between them the engine has been driven by a target *with* a control plane and one with none. Neither
needed a change to it, and there is no `if (provider == …)` in `Tyanor.Core` or `Tyanor.Engine`.

## Write your own provider

The built-in providers get no shortcut. Yours references `Tyanor.Core`, implements `IUnitDriver` and
`IFailureClassifier`, and registers in your composition root in one line:

```csharp
services.AddTyanor(cfg =>
{
    cfg.AddTarget(new AwsTarget(credentials));       // several coexist…
    cfg.AddTarget(new MyOwnTarget(…));               // …and are selected by Id
});

var runner = runners.For("my-own");                  // ProcedureRunners, injected
```

**Then prove it behaves.** `Tyanor.Testing` ships the contract suites the built-in providers run —
the things the engine assumes that no signature states:

```csharp
[Fact]
public Task My_driver_satisfies_the_contract() =>
    new UnitDriverContract(new MyFixture()).AssertAllAsync();
```

Reading a phase must change nothing. Removing what is already gone must be fine. An update with nothing to
change must say so. A resource must keep its identity across a refresh. A wrapped credential error must
still classify. Each is easy to get almost right and fails quietly — as a duplicate deployment, a teardown
that will not re-run, or a plan reporting drift that is not there.

The suites take **no test framework**: they return results, so they run under xUnit, NUnit, MSTest or a
console app. Passing them is also how a provider written elsewhere earns its way into this repository
([D15](docs/DECISIONS.md)).

There is no plugin *discovery*, deliberately — a deployment tool holds credentials and mutates
infrastructure, so it does not load code it merely found ([D6](docs/DECISIONS.md)). Authoring one and
loading one are different questions.

### Deploying a self-hosted server

```csharp
var procedure = new Procedure("server",
[
    new ProcedureUnit("runtime", "Application files"),
    new ProcedureUnit("service", "Server", Weight: 3),
]);

var request = new DeploymentRequest("acme",
    new DeploymentArtifact(new Dictionary<string, string> { ["app"] = publishOutput }),
    new Dictionary<string, string>
    {
        ["runtime.kind"] = "directory",   ["runtime.source"] = "app",
        ["service.kind"] = "process",     ["service.command"] = "dotnet",
        ["service.args"] = "Server.dll",  ["service.watch"] = "runtime",
        ["service.health.port"] = "8080",
    });

await new ProcedureRunner(new LocalTarget("/srv"), history, state)
    .ApplyAsync(procedure, request, Console.WriteLine);
```

A new build lands beside the running one and the server restarts into it; a second run started while it is
still booting **attaches** rather than launching a competitor. Settings are per unit, falling back to
unscoped — `["kind"] = "directory"` once covers every unit that does not disagree.

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
