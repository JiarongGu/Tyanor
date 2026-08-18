# Guide

How to actually use Tyanor, in the order you will need it. The [README](../README.md) says what it is and
[`architecture/overview.md`](architecture/overview.md) says how it is shaped; this walks you from nothing to
a deployment you can resume.

> Every C# sample below is compiled on every build, from this file's own text. If one is wrong, the build
> is broken — see `tests/Tyanor.Docs.Tests`.

- [Install](#install)
- [1. Describe the deployment](#1-describe-the-deployment)
- [2. Pick a target](#2-pick-a-target)
- [3. Check it before touching anything](#3-check-it-before-touching-anything)
- [4. Preview, then apply](#4-preview-then-apply)
- [5. When it stops](#5-when-it-stops)
- [6. Ask what it produced](#6-ask-what-it-produced)
- [7. Drift, and repairing it](#7-drift-and-repairing-it)
- [8. Tearing it down](#8-tearing-it-down)
- [Where state lives](#where-state-lives)
- [Wiring it into an application](#wiring-it-into-an-application)
- [Testing your own deployment code](#testing-your-own-deployment-code)
- [Extending it](#extending-it)
- [Things that will bite you](#things-that-will-bite-you)

---

## Install

```
dotnet add package Tyanor                                 # everything that is not a provider
dotnet add package Tyanor.Providers.Local                 # …and at least one provider
```

| Package | When you need it |
|---|---|
| `Tyanor` | **Always.** The engine, the state stores, DI wiring, the contract suites. |
| `Tyanor.Providers.Local` | deploy to a machine: files, a process, a health check |
| `Tyanor.Providers.Aws` | CloudFormation stacks, S3/CloudFront content |

That is the whole list. Three packages, shipping in lockstep at one version, and they need **.NET 10** — a
project on net8 cannot reference them, which is worth knowing before the restore tells you.

There are four namespaces inside `Tyanor` — `Tyanor` for the contracts, `Tyanor.Engine` for the runner,
`Tyanor.Engine.State` for the stores, `Tyanor.Testing` for the contract suites — but you install one thing
and everything is there. `Tyanor` takes exactly one dependency,
`Microsoft.Extensions.DependencyInjection.Abstractions`, because `AddTyanor` is an extension method on
`IServiceCollection` and there is no other way to write one. Notably it takes **no test framework**: the
contract suites run under whichever one you already have.

## 1. Describe the deployment

A **procedure** is an ordered list of **units**. Applied first-to-last, destroyed last-to-first — so
whatever imports from a unit is gone before the unit itself.

```csharp
var procedure = new Procedure("server",
[
    new ProcedureUnit("runtime", "Application files"),
    new ProcedureUnit("service", "Server", Weight: 3),     // takes longer, so it is more of the bar
]);
```

`Name` is the unit's **address** and its **resume key** — it becomes a directory, a stack name, its entry in
state. Do not change it between runs of the same procedure, or the next run reads the unit as missing and
makes a second one. `Label` is what a person reads and is free text.

There is no dependency graph, deliberately. Express "A needs B" by putting B first. If something genuinely
seems to need both orders, change the *operation* before reaching for edges — see
[`../.claude/rules/units-not-graphs.md`](../.claude/rules/units-not-graphs.md), which has the worked case.

A **request** is what you are deploying and where:

```csharp
var request = new DeploymentRequest(
    Prefix: "acme",                                        // lets one machine host two of the same procedure
    Artifact: new DeploymentArtifact(new Dictionary<string, string>
    {
        ["app"] = publishOutput,                           // opaque named parts — YOUR names
    }),
    Options: new Dictionary<string, string>
    {
        ["runtime.kind"] = "directory",   ["runtime.source"] = "app",
        ["service.kind"] = "process",     ["service.command"] = "dotnet",
        ["service.args"] = "Server.dll",  ["service.watch"] = "runtime",
        ["service.health.port"] = "8080",
    });
```

Options are read as `"{unit}.{key}"` falling back to `"{key}"`, so a setting shared by every unit is written
once and only the exceptions are named. `["kind"] = "directory"` alone would cover every unit that does not
disagree.

A setting that IS a unit's identity — where it lives on disk, which bucket it fills — does **not** inherit
the shared value, because a shared address is not a default: it is two units deploying on top of each other.
Write `["runtime.path"] = …`, never `["path"] = …`.

**Tyanor executes a pre-built artifact; it does not synthesize one.** Run `cdk synth`, `helm template` or
your build earlier, on a machine that has that toolchain. That is what lets an operator deploy with no cloud
SDK installed.

## 2. Pick a target

```csharp
var target  = new LocalTarget("/srv");
var history = new FileRunHistory("/var/lib/myapp/runs.json");     // what was ATTEMPTED
var state   = new FileStateStore("/var/lib/myapp/state.json");    // what Tyanor OWNS

var runner = new ProcedureRunner(target, history, state);
```

`history` is required: without something durable, a run cannot be found after the process dies, and resume
is the whole point. `state` is optional but you want it — it is what lets a teardown tell what Tyanor
created from what was already there, and what makes add/change/destroy counts real.

Check who you are before you deploy, especially on a cloud:

```csharp
var identity = await target.ValidateAsync(credentials, ct);       // null credentials = ambient identity
if (!identity.Ok) return identity.Error;
Console.WriteLine($"Deploying into {identity.Account} as {identity.Principal}");
```

That is a real call, not a null check — showing the account is the cheapest guard there is against deploying
into the wrong one.

## 3. Check it before touching anything

```csharp
var validation = await runner.ValidateAsync(procedure, request);
if (!validation.Ok) Console.WriteLine(validation);      // every problem, one per line
```

**No credentials, no network, nothing created.** This works before an account exists. It returns *every*
problem across *every* unit in one pass, rather than the first one three units into a run that has already
made things.

It says nothing about the world: a valid procedure can still fail because a bucket name is taken or a quota
is reached. That is what the plan and the run are for.

## 4. Preview, then apply

```csharp
var plan = await runner.PlanAsync(procedure, request);

Console.WriteLine(plan.Summary);                  // "3 to add, 1 to change, 0 to destroy"
foreach (var step in plan.Steps) Console.WriteLine(step);

// A destroy, or a replacement of a unit holding data, is not undoable. Ask first.
if (plan.IsDestructive && !await AskTheOperator(plan)) return;

// Someone else is mid-deploy. Applying is safe — the engine attaches — but say so.
if (plan.HasWorkInFlight) Console.WriteLine("another deployment is already in flight");

// A run is recorded live with nothing converging: it stopped. Applying RESUMES it.
if (plan.HasStalledRun) Console.WriteLine("a previous run stopped; this will continue it");
```

Two different questions, deliberately not conflated: **steps are units** (what this run will do) and
**counts are resources** (what will change in your infrastructure).

```csharp
var outcome = await runner.ApplyAsync(procedure, request, report: Console.WriteLine);
```

`report` is an `Action<ProgressReport>` and is optional. Percentages are run-relative and weighted; `-1`
means the driver honestly cannot tell, and stays `-1`.

A plan is a **forecast, not a contract** — reality can move in between. Two things it cannot know: whether
an `Update` will turn out to be a no-op (only the provider knows whether anything differs), and whether a
unit that is converging now will have settled by the time apply reaches it.

## 5. When it stops

```csharp
if (!outcome.Ok && outcome.Resumable)
{
    // Credentials expired, or a transient failure outlasted the retries.
    // Fix it and call ApplyAsync again — the work so far is kept.
}
```

Cancelling is a pause, not a failure: the provider is still converging whatever it was handed, so the run
stays LIVE and applying again continues it. Only a token cancelled *before* the run starts leaves no record,
because nothing happened.

Every stop is one of three classes, because each is a different thing to do next:

| `outcome.Reason` | What happened | What you do |
|---|---|---|
| `credentials` | the provider rejected who we are | re-authenticate, apply again |
| `transient` | throttling, a 5xx, a dropped socket — retried, then paused | wait, apply again |
| *(none)* | the definition is wrong, or a quota needs a human | change something, then apply |

**Applying again IS the resume.** There is no separate call and no run id to thread through — each unit is
decided from what the provider reports now, so a finished unit is skipped, one still converging is attached
to, and one that broke is remade. A run that paused stays *live* in the history, and the next apply adopts
it rather than opening a second record.

Cancelling leaves the run live on purpose: the provider is still converging out there, and marking it failed
would hide work genuinely in flight.

## 6. Ask what it produced

```csharp
var outputs = await runner.OutputsAsync(procedure, request);
Console.WriteLine(outputs["service.url"]);
```

Read from the provider, never from state — what a deployment currently exposes is a fact about the
deployment. A procedure that is not deployed yet returns empty rather than throwing, so a UI that renders
"your site is at …" does not have to guard the call.

## 7. Drift, and repairing it

The world moves outside the tool. A plan reports it:

```csharp
var plan = await runner.PlanAsync(procedure, request);
if (plan.HasDrift)
    foreach (var d in plan.Drift)
        Console.WriteLine($"{d.Unit}: {d.Resource.Id} — {d.Change}");
```

Two ways to answer it:

```csharp
await runner.RefreshAsync(procedure, request);   // "my records are wrong" — re-read reality, change nothing
await runner.ApplyAsync(procedure, request);     // "the deployment is wrong" — put it back as described
```

They are separate on purpose: *make my records true* should never be bundled with *change my
infrastructure*. Repairing a stale mirror is re-reading it, never hand-editing the tool's bookkeeping.

### A unit you deleted from the code

Drift is *the world moved*. This is the other direction — **you** moved, and left something behind:

```csharp
foreach (var orphan in plan.Orphaned)
    Console.WriteLine($"{orphan.Unit} is not in this procedure any more, and owns {orphan.Resources.Count}");
```

Deleting a `ProcedureUnit` from your C# removes it from everything that looks — the phase read, the drift
comparison, the teardown — so without this its infrastructure would go on existing, paid for and mentioned by
no plan. Tyanor **reports** it rather than destroying it, because the kind, options and artifact parts a
driver would need to remove it were deleted along with the unit.

The way out is to put the declaration back and destroy just that unit:

```csharp
await runner.DestroyAsync(procedure.Only("cache"), request);   // …then delete the code
```

A narrowed plan reports no orphans, deliberately — `Only("web")` leaves units out on purpose, and calling
those stranded would bury the real ones ([D25](DECISIONS.md)).

## 8. Tearing it down

```csharp
var teardown = await runner.PlanAsync(procedure, request, RunKind.Destroy);
Console.WriteLine(teardown.Summary);                        // "0 to add, 0 to change, 12 to destroy"
foreach (var step in teardown.Steps) Console.WriteLine(step);   // in the order they will actually go

if (Confirmed()) await runner.DestroyAsync(procedure, request);
```

The destructive direction gets a plan because it is the one that is not recoverable by running it again. It
counts what is **actually there**, not what state once recorded — a resource someone already deleted by hand
is not something this run is about to take away.

### Doing just one part

```csharp
await runner.ApplyAsync(procedure.Only("runtime"), request);     // Terraform's -target
```

Narrows any of the six verbs. Safer than Terraform's, because there is no dependency graph to skip: a subset
of an ordered list is still ordered, so the units that run keep their relative order and the only thing
narrowing can do is leave something out — which the plan shows. An unknown name is refused, not ignored.

It narrows a **destroy** too. Preview that one.

## Where state lives

Two stores, deliberately separate. What Tyanor *owns* has to stay true; the run log is an append-only
account of attempts. Different lifetimes, and a team sharing one does not necessarily want to share the
other. **Give them different locations** — they hold different shapes, so one file would mean two stores
silently overwriting each other, and `AddTyanor` refuses it.

Name them with a descriptor — `"{kind}:{target}"` — so the location is configuration rather than code:

```
json:/var/lib/myapp/state.json        json:C:\ProgramData\myapp\state.json
sqlite:/var/lib/myapp/tyanor.db       postgres:Host=db;Database=ops        s3://bucket/tyanor/state.json
```

Only `json` ships, registered by default, because it is the only kind that needs no package and no decision
on day one. Everything else is a backend you register — see [Extending it](#extending-it).

**The kind is required and a bare path is refused.** Accepting one would have to guess, and `"sqlite/state.db"`
— a slash where a colon was meant — would silently write state to a file called `sqlite/state.db`.

Share the store and a plan can **see** runs from other machines, including one that stalled because the
machine running it went away. The limit, stated rather than implied: two machines writing at the same instant
are not coordinated. Divergence is shown, not resolved — resolving it depends on facts Tyanor does not have.

For a test or a one-shot CI run, `InMemoryRunHistory` and `InMemoryStateStore` exist. Choosing them is
choosing to give up resume and safe teardown, which is why neither is ever a default.

## Wiring it into an application

Nothing above needs a container. If you use one:

```csharp
services.AddTyanor(cfg =>
{
    cfg.UseState("json:/var/lib/myapp/state.json");
    cfg.UseHistory("json:/var/lib/myapp/runs.json");
    cfg.AddTarget(new LocalTarget("/srv"));
    cfg.AddTarget(new AwsTarget(credentials));          // several coexist, selected by Id
});
```

Then ask for what you mean:

```csharp
// one provider — ask for the runner
public Deployer(ProcedureRunner runner) { }

// several — ask for the one you mean
public Deployer(ProcedureRunners runners) => _aws = runners.For("aws");
```

Asking for "the" runner with two targets registered **throws and names them** rather than picking one.
Registering a second provider must not quietly change where a deployment goes.

Nothing is discovered from disk. A deployment tool holds credentials and mutates infrastructure, so it does
not load code it merely found — you register yours in one line, exactly like the built-in ones.

## Testing your own deployment code

Your application has its own logic around Tyanor: does the UI offer a Resume button when a run pauses, does
the pipeline stop when validation fails, does the operator see the right thing when a deployment has drifted.
Reaching those states against a real target means credentials, money and minutes — so `Tyanor.Testing` gives
you a target that deploys to a dictionary. Nothing to install: it is in the package you already have.

```csharp
using Tyanor.Testing;
```

The ordinary case is a target that just works, so the test can be about something else:

```csharp
var runner = new ProcedureRunner(new MemoryTarget(), new InMemoryRunHistory(), new InMemoryStateStore());

Assert.True((await runner.ApplyAsync(procedure, request)).Ok);
```

And the case it exists for — driving your code down a path that is expensive to reach for real:

```csharp
var target = new MemoryTarget().Fails("api", FailureClass.Credentials, "the token expired");

var outcome = await runner.ApplyAsync(procedure, request);

Assert.True(outcome.Resumable);        // …now assert YOUR application offers the resume
```

| To reach | Write |
|---|---|
| a run that pauses, resumably | `.Fails("api", FailureClass.Credentials)` |
| a run that fails terminally | `.Fails("api", FailureClass.Hard)` |
| a blip a retry rides out | `.FailsOnce("api")` |
| an error nobody classifies | `.Throws("api", new MyException())` |
| someone else's deploy in flight | `.Reports("api", UnitPhase.Converging)` |
| a unit that must be replaced | `.Reports("db", UnitPhase.Broken)` |
| a deployment that already existed | `.AlreadyDeployed("db", "api")` |
| a deployment changed behind your back | `.Drifted("db")` |
| a new build waiting to go out | `target.Revision++` |
| refused credentials | `target.Identity = new TargetIdentity(false, Error: "…")` |
| validation problems | `target.Problems["api"] = ["names no template"]` |
| outputs to render | `target.Outputs["api"] = new() { ["api.url"] = "…" }` |

Then assert on what the engine actually decided:

```csharp
Assert.Equal(["db:update", "api:await", "web:create"], target.Calls);
Assert.Equal(["db", "api"], target.Deployed);
```

**It is a provider, not a mock.** `LocalTarget` deploys to a machine; this deploys to a dictionary. It never
simulates another provider's semantics, so it cannot teach you a wrong belief about one — and it passes
`UnitDriverContract`, the same entry ticket every other implementation buys ([D24](DECISIONS.md)). The one
deliberate exception is `Reports(...)`: `Converging` and `Broken` are states a real target reaches by timing
or by failing, which a test cannot arrange. Everything else is the truth about what is in the dictionary.

Two limits, stated rather than discovered: it hosts one kind of unit, so a `CustomUnits` step cannot be
registered in it — test one of those against the provider it belongs to, or directly with
`UnitDriverContract`. And it is not safe across concurrent runs.

## Extending it

Three questions, one answer. Build it where you need it, prove it with the contract suites, upstream it if
it generalizes.

| You need | Write | Register |
|---|---|---|
| a whole new target | `IDeploymentTarget` + `IUnitDriver` | `cfg.AddTarget(…)` |
| one step of your own | `IUnitDriver` | `new AwsTarget(creds, new CustomUnits { … })` |
| state somewhere else | `IStorageBackend` | `cfg.AddStorage(…)` |

### One step of your own

A whole provider is a lot to write when what you have is one step — verify a migration applied, warm a
cache, call a health endpoint that means something only to you:

```csharp
var target = new AwsTarget(credentials, new CustomUnits
{
    ["migration"] = new VerifyMigrationUnit(http),
    Classifier = new MyClassifier(),        // so YOUR transient errors pause instead of failing
});
```

It then sits in the same procedure as the vendor's units and gets a phase, a plan, a resume and a
classification. The one thing you must supply is the thing the engine cannot guess: **a readable phase**,
answering *has this already happened?* A step that can answer is skipped when it is done. A step that cannot
is a script, and belongs outside the procedure.

#### If you know CI/CD plugins

This is that idea, with two deliberate differences:

| | A CI/CD plugin | A Tyanor unit |
|---|---|---|
| How it is found | a marketplace or plugin directory | **registered in one line of your code — never discovered** |
| What it must do | run | run, *and answer whether it already ran* |
| How it is written | YAML plus a container image | a C# type against `IUnitDriver` |
| How it fails | pass or fail | classified: pause · retry · fail, with your own classifier |

**Nothing is loaded from disk, deliberately.** A deployment tool holds credentials and mutates
infrastructure, so running code it merely *found* is a security question nobody asked for
([D6](DECISIONS.md)). Writing and registering your own is fully supported; that is a different question from
loading one.

**The entry requirement is the phase, and it is what buys everything else.** A CI step is "run this", so it
runs every time. A unit that can answer *has this already happened?* gets skipped when it is done, attached
to when someone else's run has it in flight, resumed after a crash, and shown in a plan before it happens.
That is the whole trade: one method more than a CI plugin, and the engine's entire model in return.

#### It moves between platforms with you

Register the SAME `CustomUnits` instance in every target you use. Your step knows nothing about any
provider, so nothing about it changes when the platform does:

```csharp
var mine = new CustomUnits { Classifier = new MyClassifier(), ["discovery"] = new ServiceRegistry() };

var machine = new LocalTarget("/srv", mine);
var cloud   = new AwsTarget(credentials, mine);
var forTest = new MemoryTarget(mine);
```

One procedure, one registration, three platforms — and your own failure classes travel too, so the same
failure pauses everywhere rather than pausing on one platform and ending the run on another. What differs
between them is only the OPTIONS, because a unit is a `directory` on a machine and a `stack` on AWS and that
is where vendor vocabulary is allowed to live. Your own kind is spelled the same everywhere, because it is
yours.

**The mistake this invites**: units are registered per target, so moving to a new one means remembering to
bring them. Forget, and the run is refused naming the kinds that DO exist — an error rather than a wrong
deployment, but one you avoid by keeping the registration in a single place and passing it around.

### Proving it behaves

```csharp
[Fact]
public Task My_driver_satisfies_the_contract() =>
    new UnitDriverContract(new MyFixture()).AssertAllAsync();
```

The suites check what the engine assumes and no signature states: reading a phase changes nothing, removing
what is already gone is fine, an update with nothing to change says so, a resource keeps its identity across
a refresh, a wrapped credential error still classifies, a live run record cannot be deleted. Each is easy to
get almost right and each fails quietly.

They take no test framework — they return results, so they run under xUnit, NUnit, MSTest or a console app.
Point the driver fixture at something **real** and disposable: a stub answers by agreeing with whatever your
driver already believes.

Writing a whole provider: [`../.claude/skills/add-provider/SKILL.md`](../.claude/skills/add-provider/SKILL.md).

## Things that will bite you

- **Renaming a unit** starts a fresh one and orphans the old. The name is the resume key.
- **Reusing a prefix** across two deployments merges them. It is what keeps them apart.
- **A unit whose phase is always `Missing`** gets created on every run. Make the phase readable.
- **`ValidateAsync` making a network call** turns an offline gate into an online one for everybody. Don't.
- **`PhaseAsync` repairing something** makes the plan a lie and the apply a surprise. It must be read-only.
- **A classifier reading only the outermost exception** calls an expired token a hard failure and throws away
  a deployment that was intact. Walk the whole `InnerException` chain, and classify on codes, not messages.
- **On AWS, drift is CloudFormation-known drift.** `DetectStackDrift` is a paid asynchronous call per stack,
  far too expensive per plan — so a resource edited in the console reads as unchanged. The local provider
  content-hashes what it deployed and does catch it.
- **Publish-style steps are irreversible** and nothing has been added for them yet: a destroy over one would
  call a remove that must lie or throw. Known, deliberately unbuilt — see D21.

---

Why any of this is the way it is: [`DECISIONS.md`](DECISIONS.md).
