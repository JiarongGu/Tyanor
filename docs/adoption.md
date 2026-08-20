# Adoption

How to put Tyanor into an application that already exists, and already deploys somehow.

[`guide.md`](guide.md) teaches the API in the order you meet it, and [`providers.md`](providers.md) is the
settings reference you will want open beside this page. This is the other question — the one neither can
answer, because it is about *your* situation: what to move first, what to decide before writing any code,
what to do about infrastructure that is already running, and how to know it actually worked.

> Every C# sample below is compiled on every build, from this file's own text — the same rule the guide is
> held to. If one is wrong, the build is broken. See `tests/Tyanor.Docs.Tests`.

- [Adopt in four steps, not one](#adopt-in-four-steps-not-one)
- [Decide these before you write code](#decide-these-before-you-write-code)
- [Where Tyanor sits in your build](#where-tyanor-sits-in-your-build)
- [Wrapping a step you already have](#wrapping-a-step-you-already-have)
- [Infrastructure that already exists](#infrastructure-that-already-exists)
- [Who runs it: desktop, CI, or a service](#who-runs-it-desktop-ci-or-a-service)
- [What to put in front of an operator](#what-to-put-in-front-of-an-operator)
- [Proving the resume before you rely on it](#proving-the-resume-before-you-rely-on-it)
- [What Tyanor deliberately does not do](#what-tyanor-deliberately-does-not-do)
- [Limits worth deciding about up front](#limits-worth-deciding-about-up-front)
- [You have adopted it when](#you-have-adopted-it-when)

---

## Adopt in four steps, not one

The failure mode here is rewriting a working deployment as a Tyanor procedure in one go, discovering three
things at once, and being unable to tell which of them broke it. Each step below leaves you with something
that runs.

**1. Describe what you already do, and run nothing.** Write the `Procedure` and the `DeploymentRequest`, then
call `ValidateAsync` and stop. It touches no provider and needs no credentials, so this step cannot break
anything — and it is where you find out whether your deployment actually is an ordered list of units.

> **If your own code already has a `DeploymentRequest`, expect `CS0104` here.** Tyanor's is
> `Tyanor.DeploymentRequest`, and the collision is real rather than a mistake: an existing deployer names
> the same idea, which is usually the reason it is adopting this one. Alias whichever is the visitor —
> `using TyanorRequest = Tyanor.DeploymentRequest;` — in the few files that see both. Renaming the type
> here was considered and refused: the name is right, and the ambiguity is confined to a composition root.

**2. Plan against the real target, and still change nothing.** `PlanAsync` reads live phases. Compare what it
says against what you know is deployed. A plan that disagrees with reality means a `PhaseAsync` you have not
got right yet, and finding that out *now* costs nothing.

**3. Apply into a throwaway prefix.** The prefix is what keeps two deployments of one procedure apart, so
`"yourname-test"` gives you a complete parallel deployment beside production. Then run this exact sequence,
because each step exercises a path the one before it does not:

| | Proves |
|---|---|
| apply | it deploys |
| apply **again**, with nothing changed | **the resume path** — every unit reads `Ready`, updates report no change, and the run is a no-op |
| destroy | it comes apart in reverse, and leaves nothing — including what the provider made for itself |
| destroy **again** | a teardown is re-runnable, which is how an interrupted one is finished |
| apply | it rebuilds from nothing after having existed |

The second row is the one worth dwelling on. A driver whose update always reports a change looks perfect on
the first apply and redoes finished work on every resume afterwards — and the plan will have claimed a
redeploy would change things when it would not.

**On the third row, look at the account rather than at the report — the first time, at least.** A teardown
speaks for the units it removed, and a deployment usually creates more than its units: the AWS provider
stages templates and assets in a bucket it makes itself, the local one keeps a folder of pid files. Those
are swept when the last unit goes ([D33](DECISIONS.md)), and a provider written elsewhere is held to the
same promise by `DeploymentTargetContract` — but this page claimed "leaves nothing" for a while when it did
not, so check rather than believe. **A NARROWED destroy deliberately does not sweep**, because the units
you left out still need that scaffolding; only a full one does.

**4. Move production over, narrowed.** `procedure.Only("web")` deploys one unit. Move the least dangerous unit
first, leave the rest on the old path, and widen once each is boring.

Steps 1 and 2 are free and reversible. Do not skip them to get to step 3 faster; they are where the cheap
mistakes are found.

## Decide these before you write code

Four decisions, and only the first is hard to change later.

| Decision | If you get it wrong |
|---|---|
| **Unit names** | The name is the resume key and the address. Renaming later orphans what the old name deployed. |
| **Where state lives** | Movable, but a teardown before you move it cannot tell what Tyanor owns. |
| **Where history lives** | Movable. Losing it loses the ability to resume in flight, not anything deployed. |
| **Prefix scheme** | Two deployments sharing a prefix merge. Pick one now — `"{customer}"`, `"{env}"`. |

**Unit names deserve the thought.** They become a directory, a stack name, an entry in state, and the key a
resume matches on. `api` is a good name; `api-v2` is a name that will be wrong in six months, and changing it
makes Tyanor read the unit as missing and build a second one beside the first.

State and history are two stores and must be two locations — they hold different shapes, so one file would
mean each silently overwriting the other, and `AddTyanor` refuses it rather than letting you find out.

```csharp
services.AddTyanor(cfg =>
{
    cfg.UseState("json:/var/lib/myapp/state.json");     // what Tyanor OWNS — survives, must stay true
    cfg.UseHistory("json:/var/lib/myapp/runs.json");    // what was ATTEMPTED — an account of runs
    cfg.AddTarget(new LocalTarget("/srv"));
});
```

With neither configured, both go to JSON files under the application's base directory. That is a real durable
default rather than a placeholder — but for anything an operator re-enters, name the locations, because a
default under an install directory is a default that an upgrade can move.

## Where Tyanor sits in your build

**Tyanor executes an artifact that has already been built.** It does not run `cdk synth`, `helm template`,
`dotnet publish` or a compiler. Getting this boundary wrong is the commonest way an adoption goes sideways,
because it looks like a limitation until you see what it buys:

```
your build  ──►  an artifact on disk  ──►  Tyanor  ──►  the target
(needs the toolchain)   (named parts)      (needs neither)
```

An operator running the deployment needs no cloud SDK, no Node, no CDK — which is the property that lets a
desktop application deploy its own infrastructure on a machine that has none of them.

So the artifact is the handover, and it is **opaque named parts** — your names, pointing at paths:

```csharp
var artifact = new DeploymentArtifact(new Dictionary<string, string>
{
    ["app"] = publishOutput,                  // dotnet publish wrote this
    ["infrastructure"] = synthOutput,         // cdk synth wrote this
});
```

Nothing in Tyanor knows that `"infrastructure"` is a CloudFormation assembly. Only the AWS provider does, and
only because a unit's `template` option said so. Which options each provider reads, and what each does with
the part it is handed, is [`providers.md`](providers.md).

If a step of your pipeline genuinely has to run at deploy time — a migration, a smoke test — that is a unit,
not a reason to move synthesis. The next section is how.

## Wrapping a step you already have

Most existing deployments have steps that are nobody's vendor's business: verify a migration applied, warm a
cache, call a health endpoint, register with a service directory. Before adopting, those live in a script that
runs after the deploy and gets none of what the engine gives.

Making one a unit costs one method more than a CI plugin: **a readable phase, answering *has this already
happened?*** That is the entry requirement, and it is what buys everything else — the step is skipped when it
is done, attached to when someone else's run has it in flight, resumed after a crash, and shown in a plan
before it happens.

```csharp
internal sealed class SmokeTestUnit(HttpClient http) : StepUnitDriver
{
    // The method that is not optional. Everything else the engine does follows from being able to ask this.
    public override async Task<UnitPhase> PhaseAsync(UnitContext context)
    {
        try
        {
            var response = await http.GetAsync(Url(context), context.Cancellation);
            return response.IsSuccessStatusCode ? UnitPhase.Ready : UnitPhase.Missing;
        }
        catch (HttpRequestException)
        {
            return UnitPhase.Missing;      // not answering yet is a fact, not a failure
        }
    }

    // The check IS the whole of it — a step has no control plane to hand work to, so this is where it runs.
    public override async Task CreateAsync(UnitContext context)
    {
        if (await PhaseAsync(context) is not UnitPhase.Ready)
            throw new SmokeTestFailed($"{context.Label}: {Url(context)} is not answering.");
    }

    // Resolve exactly what the apply resolves, and REPORT the refusal instead of throwing it.
    public override Task<IReadOnlyList<string>> ValidateAsync(UnitContext context) =>
        new UnitProblems().Check(() => Url(context)).Found();

    private static string Url(UnitContext context) =>
        context.OwnOption("url") ?? throw new SmokeTestMisconfigured(
            $"Unit '{context.Name}' is a smoke test but names no 'url'.");
}
```

**`StepUnitDriver` is why that is three methods and not seven.** A unit that deploys infrastructure needs all
six of `IUnitDriver`'s — something to update, something to remove, a control plane to wait on, resources to
report. A step has none of that: it answers *has this already happened?* and it does the thing. The other
four are the same four lines every time, and they were written that way six times in this repository before
the base class existed — including here, in the example somebody adopting copies first. Implement
`IUnitDriver` directly when your unit really does own something; reach for this when it does not.

The one pairing to get right is **`PhaseAsync` and `RemoveAsync` must agree**. The default remove does
nothing, which is correct here because a smoke test stops reporting `Ready` on its own once the endpoint
stops answering. If your phase is a *latch* — a row you wrote, a flag you set — override the remove to clear
it, or a destroy will leave the unit claiming to still be deployed. `UnitDriverContract` catches exactly that.

Three more things in there are the whole convention, and each is worth a sentence:

- **`OwnOption`, not `Option`.** A URL is this unit's identity. A procedure-wide `"url"` would not be a
  sensible default for every smoke test, it would be every one of them checking the same address.
- **`SmokeTestMisconfigured` derives from `DefinitionException`.** That is what makes it a *configuration*
  problem rather than a failure — terminal, nothing touched, and `UnitProblems.Check` collects it so
  validation reports it instead of throwing.
- **`ValidateAsync` calls the apply's own resolver.** Writing the check a second time gives you two rules,
  and they diverge the first time one is edited.

Register it once and hand the same instance to every target, so the step does not belong to the platform you
first ran it on:

```csharp
var mine = new CustomUnits
{
    Classifier = new SmokeTestClassifier(),        // so YOUR transient errors pause instead of failing
    ["smoke"] = new SmokeTestUnit(http),
};

var machine = new LocalTarget("/srv", mine);
var forTest = new MemoryTarget(mine);
```

Give it a `Classifier` if it can fail in a way worth retrying or resuming. Without one, anything it throws is
unrecognised, and unrecognised means terminal — correct, but it means your step can never pause.

## Infrastructure that already exists

The question everyone asks first: **will pointing Tyanor at a running deployment destroy it?** No — but read
this before finding out, because the honest answer has a shape.

Tyanor decides from what the provider reports. Units that already exist read as `Ready`, which reconciles to
`Update`, not `Create`. What it does *not* know is that it owns them, because state is empty — so a teardown
could not tell what Tyanor made from what was already there, and the counts would report every existing
resource as an addition.

The move is to adopt them into state **without changing anything**:

```csharp
// Reads the provider and rewrites state to match. Touches no infrastructure whatsoever.
await runner.RefreshAsync(procedure, request);

// Now the plan is about real differences rather than about an empty state file.
var plan = await runner.PlanAsync(procedure, request);
Console.WriteLine(plan.Summary);
```

`RefreshAsync` needs a state store, and throws if the runner was built without one. Run it first, read the
plan, and only then apply.

**Check the plan says what you expect before the first apply.** A unit that reads `Create` for something you
know is deployed means its `PhaseAsync` is not finding it — a wrong prefix, a wrong unit name, a wrong region.
That is the moment to find out, and it is free.

## Who runs it: desktop, CI, or a service

The engine is the same; what differs is where the two stores go and who sees a pause.

| | State | History | The pause question |
|---|---|---|---|
| **Desktop app** | beside the app's data | beside the app's data | a Resume button — this is what the model was built for |
| **CI pipeline** | shared and durable (not the workspace) | shared and durable | the job fails; the *next* job resumes |
| **Long-lived service** | shared store | shared store | resume on a schedule, or on an operator's request |

**The CI mistake worth naming.** Putting either store in the build workspace means every run starts with no
state and no history, so nothing can ever be resumed and every teardown is blind about what it owns. Both
belong somewhere that outlives the job.

A pipeline gets the gates in order, and each one is a place to stop:

```csharp
var validation = await runner.ValidateAsync(procedure, request);
if (!validation.Ok)
{
    Console.Error.WriteLine(validation);          // every problem, one per line
    return 1;
}

var plan = await runner.PlanAsync(procedure, request);
Console.WriteLine(plan.Summary);

// Unattended and about to take something away: stop, and let a person look.
if (plan.IsDestructive) return 2;

var outcome = await runner.ApplyAsync(procedure, request, report: Console.WriteLine);
return outcome.Ok ? 0 : outcome.Resumable ? 75 : 1;      // 75 is EX_TEMPFAIL — try again
```

Distinguishing the two failure exits matters more than it looks: a resumable stop means re-running the same
job finishes the work, and a terminal one means re-running it changes nothing.

## What to put in front of an operator

Six verbs, and they map onto what a person actually asks. If you are building a UI, this table is the UI.

| They ask | You call | Show them |
|---|---|---|
| "is this set up right?" | `ValidateAsync` | every problem at once, before anything exists |
| "what will this do?" | `PlanAsync` | `plan.Summary`, then `plan.Steps` |
| "do it" | `ApplyAsync` | live progress; percentages are run-relative and weighted |
| "it stopped — now what?" | `outcome.Resumable` | a Resume button, or the reason it is terminal |
| "where is my site?" | `OutputsAsync` | read live from the provider, never from state |
| "take it away" | `PlanAsync(…, RunKind.Destroy)` then `DestroyAsync` | the teardown plan, behind a confirmation |

Three signals are worth surfacing loudly rather than hiding, because each changes what the operator should
expect:

- **`plan.IsDestructive`** — this will take something away that exists. The only gate that really matters.
- **`plan.HasWorkInFlight`** — someone else is mid-deploy. Applying is safe, the engine attaches; but they
  should know why their change is not the one being applied.
- **`plan.HasStalledRun`** — a run is recorded live with nothing converging. It stopped, possibly on a
  machine that is not coming back, and applying resumes it rather than starting fresh.

**A pause must read as a pause.** The single most valuable thing you can carry through to a person is that
the work so far is kept. A UI that shows "Deployment failed" for an expired token teaches people to fear the
button, which is the exact failure `FailureClass` exists to prevent.

**And a wrong configuration must not read as a failed deployment.** Everything derived from
`DefinitionException` means *you have configured this wrongly, and nothing was touched* — a different screen,
a different tone, and not a support conversation. The guide has
[the taxonomy and which calls surface it](guide.md#5-when-it-stops); the short version is that
`ValidateAsync` reports these rather than throwing, which is why it is the gate to put first.

**The history is the status screen.** `RecentAsync` for what has happened, `LiveAsync` for whether anything
is outstanding right now — see [reading the run log](guide.md#reading-the-run-log). A live record cannot be
deleted, in any store, so a "clear history" button is safe to offer.

## Proving the resume before you rely on it

Resume is the claim Tyanor makes, so it is the claim to test in your application rather than take on trust.
It is also the cheapest thing to test, because `MemoryTarget` reaches states that cost money to reach for
real:

```csharp
var target = new MemoryTarget().Fails("api", FailureClass.Credentials, "the token expired");
var runner = new ProcedureRunner(target, new InMemoryRunHistory(), new InMemoryStateStore());

var stopped = await runner.ApplyAsync(procedure, request);

Assert.False(stopped.Ok);
Assert.True(stopped.Resumable);                 // …so YOUR code must offer a resume here

target.Faults.Remove("api");                    // the operator re-authenticated
Assert.True((await runner.ApplyAsync(procedure, request)).Ok);
Assert.Equal(["db", "api", "web"], target.Deployed);
```

That is the whole model in one test: it stopped, nothing was lost, the same call finished it. Write it against
your own code, not against Tyanor's — what you are checking is that *your* application offers the resume.

**Then do it for real, once.** Start an apply against a scratch prefix and kill the process mid-run. A new
process must find the live run through `LiveAsync` and finish it. That is the acceptance test a contract suite
cannot express, and it is the one worth doing by hand before you depend on it.

## What Tyanor deliberately does not do

Knowing the edges up front is cheaper than discovering them:

- **It does not build or synthesize.** See [above](#where-tyanor-sits-in-your-build).
- **It does not manage secrets.** `TargetCredentials` is a parameter. Where credentials come from is your
  application's business, and a library that decided would be wrong for most of its users.
- **It does not schedule, notify, or log.** No daemon, no background thread, no logging opinion. Progress is
  an `Action<ProgressReport>` you own.
- **It does not discover plugins.** A deployment tool holds credentials and mutates infrastructure, so it does
  not load code it merely found. Writing and registering your own is fully supported; that is a different
  question from loading one.
- **It has no dependency graph.** Ordering, and reverse for teardown. If something seems to need both orders,
  change the operation — [`units-not-graphs.md`](../.claude/rules/units-not-graphs.md) has the worked case.

## Limits worth deciding about up front

Each of these is stated rather than hidden, and each is a decision you should make deliberately:

- **Two machines writing the same store at the same instant are not coordinated.** Divergence is *shown* —
  `HasStalledRun`, `InSync` — and not resolved, because resolving it needs facts Tyanor does not have. The
  shipped `json` backend is last-writer-wins. If anything automated will gate on the history, that is the
  point at which you want a backend that can check `DeploymentState.Serial`.
- **On AWS, drift is CloudFormation-known drift.** `DetectStackDrift` is a paid asynchronous call per stack,
  far too expensive per plan, so a resource edited in the console reads as unchanged. The local provider
  content-hashes what it deployed and does catch it.
- **Only a FULL destroy sweeps what the provider made for itself.** A narrowed one leaves the staging
  bucket and the pid folder alone on purpose, because the units you did not remove still need them — so a
  deployment torn down one unit at a time with `Only` never sweeps, and the last narrowed destroy leaves the
  scaffolding standing. Finish with a full one, or clean up by hand. ([D33](DECISIONS.md).)
- **A publish-style step is irreversible and nothing has been built for it.** A destroy over one would call a
  remove that must lie or throw. Known and deliberately unbuilt — bring the real case rather than a
  hypothetical one.
- **`MemoryTarget` is not safe across concurrent runs.** A test that needs that is testing the engine rather
  than using it. It *does* host your own `CustomUnits` steps — that is how you drive one through a whole
  procedure before it ever meets a cloud.

## You have adopted it when

A checklist you can actually run, in the order that finds problems earliest:

- [ ] `ValidateAsync` returns clean, with no credentials configured at all.
- [ ] `PlanAsync` against the real target agrees with what you know is deployed.
- [ ] An apply into a scratch prefix succeeds, and a second apply straight after reports no changes.
- [ ] A destroy of that prefix leaves nothing, and running it a second time is fine.
- [ ] You have looked at what that destroy left *behind*, with your own eyes — the difference between a
      clean account and one you believe is clean is that somebody looked once.
- [ ] A misconfigured unit reaches the operator as a configuration problem, not as a failed deployment.
- [ ] Killing the process mid-apply and re-running finishes the deployment.
- [ ] A pause reaches the operator as *resumable*, with the work kept — not as "failed".
- [ ] State and history are in locations that outlive the machine, the workspace, and an upgrade.
- [ ] Every custom unit you wrote passes `UnitDriverContract`.

The last one is the entry ticket the built-in providers buy too — same contracts, same registration, same
suites, no shortcut.

---

Next: [`guide.md`](guide.md) for the API in order, [`providers.md`](providers.md) for every setting the
shipped providers read, [`DECISIONS.md`](DECISIONS.md) for why any of it is like this, and
[`architecture/overview.md`](architecture/overview.md) for the shape in one page.

**Found something the docs did not answer?** That is exactly what
[`TASKS.md`](../TASKS.md) asks adopters to write down — the specific question you had is the fix.
