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

## If you know Terraform, you know the verbs

Deliberately the same words, because they name the same jobs and inventing new ones would only make you
translate:

| Terraform | Tyanor |
|---|---|
| `terraform validate` | `runner.ValidateAsync(…)` — no provider access at all |
| `terraform plan` | `runner.PlanAsync(…)` — in **either** direction |
| `terraform apply` | `runner.ApplyAsync(…)` — which is also the resume |
| `terraform destroy` | `runner.DestroyAsync(…)` |
| `terraform refresh` | `runner.RefreshAsync(…)` |
| `terraform output` | `runner.OutputsAsync(…)` |
| `terraform state show` | `IStateStore.GetAsync(…)` |

A *driver* still says `RemoveAsync`, because it removes one unit rather than destroying a deployment — the
same asymmetry Terraform has between its command and a provider's per-resource delete.

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
var teardown = await runner.PlanAsync(procedure, request, RunKind.Destroy);
Console.WriteLine(teardown.Summary);        // "0 to add, 0 to change, 12 to destroy"
foreach (var step in teardown.Steps) Console.WriteLine(step);   // in the order they will go
```

A plan is a forecast and says which two things it honestly cannot know.

**Check the definition before an account exists.** `ValidateAsync` reads every unit's configuration and
resolves every artifact part with **no provider access at all** — no credentials, no network, nothing
created — and returns every problem in one pass rather than the first one, three units in, after a run has
already made things:

```csharp
var validation = await runner.ValidateAsync(procedure, request);
if (!validation.Ok) Console.WriteLine(validation);   // one problem per line, all of them
```

It says nothing about the world: a valid procedure can still fail to deploy because a name is taken. That is
what the plan and the run are for.

**Ask what the deployment produced.**

```csharp
var outputs = await runner.OutputsAsync(procedure, request);
Console.WriteLine(outputs["web.url"]);               // read from the provider, never from state
```

**A library, not a service.** `Tyanor.Core`, `Tyanor.Engine` and `Tyanor.Testing` take **no package
dependencies**. There is no daemon, no CLI, no ambient state and no background thread. DI is a separate,
optional package — the minimal path is three lines with no container:

```csharp
var runner = new ProcedureRunner(target, new FileRunHistory("runs.json"));
var plan   = await runner.PlanAsync(procedure, request);
await runner.ApplyAsync(procedure, request);          // progress callback optional
```

**State lives where you put it.** Two stores, deliberately separate: what Tyanor *owns* has to stay true,
while the run log is an append-only account of attempts — different lifetimes, and a team sharing one does
not necessarily want to share the other.

```csharp
services.AddTyanor(cfg =>                                  // optional package, if you use a container
{
    cfg.UseFileState("/var/lib/myapp/state.json");         // what Tyanor owns — or your own IStateStore
    cfg.UseFileHistory("/var/lib/myapp/runs.json");        // what was attempted — or your own IRunHistory
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
| `Tyanor.Extensions.DependencyInjection` | `AddTyanor`. Optional — the engine works without a container. |
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

// …then, wherever you deploy from:
public MyDeployer(ProcedureRunners runners) => _runner = runners.For("my-own");
```

Asking for "the" runner with several registered throws and names them rather than picking one — registering
a second provider must not quietly change where a deployment goes.

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

## Or just add one step of your own

A whole provider is a lot to write when what you have is one step: verify a migration applied, warm a cache,
call a health endpoint that means something only to you. Register it as a unit kind inside the provider you
are already using, and it sits in the same procedure as that vendor's units:

```csharp
var target = new AwsTarget(credentials, new CustomUnits
{
    Classifier = new MyClassifier(),                 // so YOUR transient errors can pause rather than fail
    ["migration"] = new VerifyMigrationUnit(http),
});

var procedure = new Procedure("site",
[
    new ProcedureUnit("db",  "Database"),
    new ProcedureUnit("api", "API"),
    new ProcedureUnit("migration", "Database changes"),   // ["migration.kind"] = "migration"
    new ProcedureUnit("web", "Website"),
]);
```

It is then planned, reconciled, resumed and classified like everything else. The only thing you must supply
is the thing the engine cannot guess — a readable phase, answering *has this already happened?* A step that
can answer that gets skipped when it is done instead of re-running on every deploy. A step that cannot is a
script, and belongs outside the procedure.

That is the intended way to use anything Tyanor does not support yet: build it where you need it, prove it
with the contract suites, and upstream it if it generalizes ([D19](docs/DECISIONS.md)).

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
