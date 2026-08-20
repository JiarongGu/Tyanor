# Provider reference

Every setting the two shipped providers read, what each unit kind does, and what each will and will not
tell you.

[`guide.md`](guide.md) teaches the API in the order you meet it and [`adoption.md`](adoption.md) is about
moving an existing deployment onto it. This is the page you keep open while writing a
`DeploymentRequest` — it is a reference, not a tutorial, and it is deliberately exhaustive because the
alternative is reading the provider's source, which is what everyone was doing before it existed.

> Every C# sample below is compiled on every build, from this file's own text — the same rule the guide and
> the adoption document are held to. If one is wrong, the build is broken. See `tests/Tyanor.Docs.Tests`.

- [How settings are read](#how-settings-are-read)
- [The local provider](#the-local-provider)
  - [A `directory` unit](#a-directory-unit)
  - [A `process` unit](#a-process-unit)
  - [How this machine's failures are classified](#how-this-machines-failures-are-classified)
- [The AWS provider](#the-aws-provider)
  - [A `stack` unit](#a-stack-unit)
  - [A `content` unit](#a-content-unit)
  - [The CloudFormation phase table](#the-cloudformation-phase-table)
  - [How AWS's failures are classified](#how-awss-failures-are-classified)
- [What is verified, and where the last mile starts](#what-is-verified-and-where-the-last-mile-starts)
- [What neither provider does](#what-neither-provider-does)

---

## How settings are read

Everything below is a key in `DeploymentRequest.Options`, which is a flat `string → string` map. It is
untyped on purpose: the moment it becomes fixed fields it grows one field per provider and stops being
neutral ([D4](DECISIONS.md)). Three reading rules cover every setting on this page.

| Read as | Looks for | Used for |
|---|---|---|
| **shared, per-unit override** | `"{unit}.{key}"`, then `"{key}"` | almost everything — write it once, name the exceptions |
| **per unit only** | `"{unit}.{key}"` and nothing else | a setting that IS the unit's address. Only `local`'s `path` |
| **a group** | every `"{unit}.{prefix}.*"` over every `"{prefix}.*"` | sets whose keys the provider cannot know: `parameter.*` |

The distinction in the middle row is the one worth understanding, because getting it wrong is silent. A
shared `"path"` does not mean *every unit defaults to this directory* — it means every unit deploys **on top
of** every other, and removing one removes them all. So a setting that is a unit's identity never falls back.
Everything else does, which is what keeps a request short.

**`kind` is required on every unit and has no default**, in both providers, even where only one kind would
make sense. Guessing would deploy something the operator never described, and the moment a second kind
exists every unit that relied on the default changes meaning without changing text. A unit that declares
none is refused with the list of kinds that provider has.

**`source`, `template` and `assets` name a part of the artifact, not a path.** The artifact is opaque named
parts — your names, pointing at paths — so these settings say *which part*, and Tyanor resolves it. A part
that is not in the artifact, or that points at nothing on disk, is refused before anything is touched, with
one sentence naming what the artifact does carry. That refusal is `ArtifactException`, which is a
`DefinitionException`, so `ValidateAsync` reports it offline rather than throwing.

**A part that names a directory is a ROOT, and it is used whole** — recursively, every file under it, with
paths relative to the root as the keys or names at the far end. It is not a selection of files that happen
to live there. This matters when a build writes several kinds of output into one directory: a bundler
emitting `*.template.json` beside `*.zip` and a part pointed at that directory ships both, wherever only
one was meant. Give each kind its own directory in the build, and its own part.

---

## The local provider

```
dotnet add package Tyanor.Providers.Local
```

```csharp
var target = new LocalTarget("/srv");            // Id "local"
```

Deploys to **this machine**: a tree of files materialized from the artifact, a long-lived process run out of
it, and a TCP health check that says whether the thing is actually up.

`root` is where deployments live. Each gets `{root}/{prefix}`, so one root hosts several deployments of one
procedure. `ValidateAsync` ignores the credentials argument — pass `null`, this target authenticates as
whoever is running the process — but it is still a real check: it writes a file into `root` and deletes it,
and reports the machine name and user. *Can I write where you are about to deploy?* is the local form of
*are these keys valid*, and showing the host is the local form of showing the account.

**What it writes, and where.** Worth knowing before you point it at a directory:

```
{root}/{prefix}/{unit}/releases/{fingerprint}/…    the files, one directory per build
{root}/{prefix}/{unit}/.tyanor-unit.json           which build is in service
{root}/{prefix}/.tyanor/{unit}.pid.json            the process record, OUTSIDE the unit directories
```

The bookkeeping is outside the unit directories deliberately, so removing a unit removes exactly what was
deployed and none of Tyanor's own. **A unit owns its directory**: applying prunes releases it no longer
needs and removing deletes the lot — so anything your application writes, logs and data especially, belongs
somewhere else.

**A full destroy takes `.tyanor` and the `{prefix}` folder with it** ([D33](DECISIONS.md)) — the same
sweep the AWS provider uses for its staging bucket, because that bookkeeping is the provider's and no unit
could remove it. `{root}` itself is never touched: it is yours. **The prefix folder goes only if it is
empty**, which is the answer rather than caution — anything left in it means something is still deployed
(a unit whose `path` points elsewhere, or one that was retained), and removing it would take away what the
teardown deliberately did not.

### A `directory` unit

A tree of files, materialized from one named part of the artifact.

| Setting | Required | Read as | Meaning |
|---|---|---|---|
| `kind` | **yes** | shared | `"directory"` |
| `source` | **yes** | shared | which artifact part the files come from. Must be a directory on disk |
| `path` | no | **per unit only** | where the unit lives. Default `{root}/{prefix}/{unit}` |

**Each build lands in its own directory** under `{path}/releases/{fingerprint}`, and a marker file records
which one is in service. This is not tidiness. A process holds its working directory and the assemblies it
loaded, so replacing those files in place fails — on Windows outright, and everywhere else eventually.
Writing beside the running release and letting the process restart into it is what makes a redeploy work at
all, and it is also what keeps the no-dependency-graph rule honest: *stop the server, replace the files,
start it again* looks unorderable until you change the operation so nothing conflicts ([D13](DECISIONS.md)).

| Phase | When |
|---|---|
| `Missing` | the directory is absent or empty |
| `Broken` | files are there with no usable marker — a first copy was interrupted, or someone put them there |
| `Ready` | the marker names a release that exists |
| `Converging` | **never**, and that is honest rather than an omission — see below |

There is no converging state because a copy is converged only by the process doing it. A cloud unit can be
left mid-flight and attached to later precisely *because* a server elsewhere is still working; a filesystem
has no such server. What an interrupted copy leaves behind is not work in progress, it is wreckage — so it
reads as `Broken` and the engine remakes it.

**An update asks two questions and both have to be no**: is there a new build, and has anyone edited what I
deployed? The second is why the provider content-hashes the deployed release rather than trusting its own
marker — a hand-patched server that survives every redeploy is how a machine drifts away from its recipe.

| | |
|---|---|
| **Owns** | one resource: the unit directory, fingerprinted by the *actual* contents of the current release |
| **Produces** | `{unit}.path` — the release directory in service, so you can point something at it |
| **Removing** | deletes `{path}` and everything under it |

### A `process` unit

A long-lived process, started detached so it outlives the deploy that created it, and supervised by its pid.

| Setting | Required | Read as | Meaning |
|---|---|---|---|
| `kind` | **yes** | shared | `"process"` |
| `command` | **yes** | shared | the executable to run |
| `args` | no | shared | arguments, as one command line |
| `watch` | no | shared | the **`directory` unit** whose contents this process runs |
| `workDir` | no | shared | working directory. Defaults to the watched unit's current release |
| `health.port` | no | shared | a TCP port on loopback that means "this server is up" |
| `health.seconds` | no | shared | how long it may be alive-but-not-answering. Default **60** |

**`watch` is how the ordering dependency becomes visible** instead of implied. Naming a directory unit puts
that unit's content into this one's fingerprint, so a new build restarts the server and a re-run of the same
build does not — and a plan says the service will be restarted, before it happens.

| Phase | When |
|---|---|
| `Missing` | nothing recorded a process — never started, or cleanly removed |
| `Converging` | alive but not yet answering, and still inside the grace window |
| `Ready` | answering on `health.port` — or merely alive, if no port was configured |
| `Broken` | recorded but not running (it crashed), or alive and silent past the grace window |
| `Unwinding` | **never** — nothing here rolls back |

**Without `health.port`, `Ready` means only that the process is alive.** Stated plainly rather than dressed
up: with nothing to probe, that is the most Tyanor can honestly claim, and pretending to a health check
nobody configured would be worse than admitting the limit.

**The grace window runs from when the process started, not from when this wait did.** A second run attaching
to a server that has been failing to boot for five minutes is not granted a fresh sixty seconds.

**Output is not redirected.** The server's stdout and stderr go wherever the caller's do. A library that
captures an operator's logs into a pipe nobody reads has decided something that is not its to decide — and
an unread pipe eventually blocks the process it was meant to supervise.

| | |
|---|---|
| **Owns** | one resource, identified by its pid **file** rather than its pid — identity has to survive a restart, and a pid does not. Nothing, when the record exists but the process does not |
| **Produces** | `{unit}.pid`, and `{unit}.url` = `http://localhost:{port}` when a port was configured |
| **Removing** | kills the process **tree** (a forked worker still holding the port would break the next create), waits for exit, deletes the record |

**Pids are reused, and the record stores the process's own start time to survive that.** A tool that kills
whatever currently holds a remembered number will eventually kill something unrelated, on the machine of
whoever left a deployment lying around longest.

### How this machine's failures are classified

Classification is on **codes** — `Win32Exception.NativeErrorCode`, `SocketError`, HRESULTs — never on
message text, which is localized and is not API surface.

| Class | What lands here | Why |
|---|---|---|
| `Credentials` | `UnauthorizedAccessException`, `SecurityException`, Win32 `5` / errno `13` | the OS refused **this identity** |
| `Transient` | sharing and lock violations, `AddressAlreadyInUse`, `TimedOut`, `ConnectionRefused`, `TimeoutException`, a health check that ran out of grace | busy, not wrong |
| `Hard` | `FileNotFoundException`, `DirectoryNotFoundException`, Win32 `2` / `3`, a process that exited during startup | the definition names something that is not there |

**`Credentials` on a machine with no credentials is not a stretch, it is the point.** The OS decides whether
this identity may write to that directory or start that process, and when it refuses, the operator's next
move is exactly the credential move — *be someone allowed to do this, then resume; the work so far is
kept*. That the class was named for expired cloud tokens is an accident of which provider came first
([D13](DECISIONS.md)).

A file held open by the process you are about to replace is the single most common way a redeploy fails on
Windows, and it clears on its own — which is the definition of transient. So the run pauses, the operator
stops the service, and resuming finishes it.

---

## The AWS provider

```
dotnet add package Tyanor.Providers.Aws
```

```csharp
using var target = new AwsTarget(
    new TargetCredentials("AKIA…", "…", Region: "ap-southeast-2"));    // Id "aws"
```

Deploys CloudFormation stacks, and a bucket of website files in front of them. **The region is required** —
every call needs one, and defaulting it would deploy somewhere the operator did not name. `AwsTarget` owns
its SDK clients and is `IDisposable`.

`ValidateAsync` ignores its credentials argument, because the target was built with the ones it uses; it
makes a real `GetCallerIdentity` call and reports the account and the principal. Showing the account is the
cheapest guard there is against deploying into the wrong one — a mistake that is trivial to prevent
beforehand and expensive to unpick afterwards.

**Templates are already synthesized.** This provider uploads and deploys them; it does not run `cdk synth`,
`helm template` or a compiler ([D5](DECISIONS.md)). That is what lets an operator deploy with no cloud
toolchain installed.

### A `stack` unit

One CloudFormation stack, deployed as `{prefix}-{unit}`.

| Setting | Required | Read as | Meaning |
|---|---|---|---|
| `kind` | **yes** | shared | `"stack"` |
| `template` | **yes** | shared | the artifact part naming the template **file** |
| `assets` | no | shared | an artifact part naming a **directory** whose whole contents the template expects in the staging bucket — Lambda zips and the like |
| `parameter.*` | no | **group** | CloudFormation parameters: `"api.parameter.MemorySize" = "512"` |
| `capabilities` | no | shared | comma-separated. Default `CAPABILITY_IAM,CAPABILITY_NAMED_IAM` |

`DeploymentRequest.Tags` become the stack's tags.

**The stack name is `{prefix}-{unit}`, and this provider checks it before AWS does.** CloudFormation wants a
name starting with a letter, using only letters, digits and hyphens, at most 128 characters — while Core
allows `_` and `.` because a filesystem does. Core naming a vendor's charset would be the leak
[D4](DECISIONS.md) is about, so closing the gap belongs here, and closing it locally turns an opaque
`ValidationError` from AWS into a sentence naming what is wrong ([D17](DECISIONS.md)). The 128 limit is
reached sooner than anyone expects, because the prefix is in every stack name.

**Templates go up by URL, not inline**, because a real template exceeds CloudFormation's inline limit. The
staging bucket is `{prefix}-deploy-{account}` — per account so two operators never collide, and derived
rather than configured so there is nothing to get wrong. Assets keep their names *relative to the `assets`
part's root*: those are the object keys the synthesized template refers to.

**A full destroy empties and deletes that bucket** ([D33](DECISIONS.md)). It belongs to the provider rather
than to any unit — every stack stages through the same one — so no unit could be the one to remove it, and
until D33 nothing did: a torn-down deployment left it standing, holding every template and Lambda asset it
had ever uploaded. A **narrowed** destroy leaves it alone, deliberately, because the stacks you did not
remove still need it.

**Nothing tells a template what that bucket is called**, and that gap is still open — see
[`TASKS.md`](../TASKS.md). A synthesized template that takes the bucket as a CloudFormation parameter has to
be handed the name, and `parameter.*` values are passed through verbatim: there is no token to substitute
and no option that exposes it. Deriving `{prefix}-deploy-{account}` in your own composition root works, and
costs you an `sts:GetCallerIdentity` call to learn the account, but you are reimplementing a convention this
provider owns and could move.

| | |
|---|---|
| **Phase** | mapped from the stack status — [the table below](#the-cloudformation-phase-table) |
| **Update** | returns "no change" on *No updates are to be performed*, which on a resume is the ordinary answer |
| **Owns** | one resource per stack resource: physical id, resource type, and the CloudFormation status as its fingerprint |
| **Produces** | the stack's CloudFormation outputs, by output key |
| **Removing** | `DeleteStack`, then polls until the stack is gone. `DELETE_FAILED` throws, naming the first resource that failed |

Created with `OnFailure = ROLLBACK` rather than `DELETE`, deliberately: on failure the stack record and its
events survive, so the cause is still readable — which is the difference between telling an operator *the
API stack failed because the role name was taken* and telling them it failed.

**Status is read every 6 seconds** while waiting, and each settled resource event is reported as it happens.

**Drift is CloudFormation-known drift.** The fingerprint is the resource's CloudFormation status, not a
content hash, and `DetectStackDrift` — which is what would catch a resource edited in the console — is a
paid asynchronous operation per stack, far too expensive to run on every plan. So a resource someone changed
by hand reads as unchanged, and the honest place to find out is CloudFormation's own drift detection.

### A `content` unit

A directory of files synced into an S3 bucket, optionally invalidating the CDN in front of it. A website.

| Setting | Required | Read as | Meaning |
|---|---|---|---|
| `kind` | **yes** | shared | `"content"` |
| `source` | **yes** | shared | the artifact part naming the **directory** to sync |
| `bucket` | one of | shared | the destination, when it is known up front |
| `bucketFrom` | one of | shared | `"{unit}:{OutputKey}"` — read the destination out of a stack's outputs |
| `invalidateFrom` | no | shared | `"{unit}:{OutputKey}"` naming a CloudFront distribution id to invalidate after a sync |

**The bucket usually belongs to another unit.** A stack creates it and exports its name; this unit reads
that output at apply time. So the dependency is expressed by declaring the stack first and never by an
edge — [`units-not-graphs.md`](../.claude/rules/units-not-graphs.md) working on the provider it was written
for. If the bucket does not exist, the refusal says so in those terms rather than passing on S3's *the
specified bucket does not exist*, which sends an operator to look at S3 instead of at their procedure.

**Without `invalidateFrom` the files go up and the CDN keeps serving the old ones until they expire** —
which looks exactly like a deployment that silently did nothing.

| Phase | When |
|---|---|
| `Missing` | no bucket, or the bucket is empty |
| `Ready` | the bucket has something in it |
| `Converging` | **never** — an S3 sync is converged only by the process doing it |

The phase asks whether this unit has put *anything* there, not whether what is there is current. Reporting
`Missing` for a site that is up and serving would render in a plan as *create (nothing there now)*, which is
worse than saying nothing; whether the files are current is what the update is for.

**A sync makes the bucket be the build, in both directions.** Files are compared by name and **size**, never
by content — comparing bodies means downloading them, and you asked for a deployment rather than a bandwidth
bill. So an edit that preserves a file's byte count is not noticed. Everything the build produces is
uploaded, and then **everything the build no longer produces is removed**: without that, deleting a page
from a site never took it down, because nothing else ever looks.

The prune runs *after* the upload, so an interruption leaves the site stale and serving rather than
half-gone. It covers the whole bucket, because that is what this unit fills — give it a bucket of its own,
which is what `bucketFrom` naming a stack's output produces.

**A build that produced no files at all is refused** rather than converged on. An empty directory does
describe an empty site, but the prune would then empty a live website because a build step failed quietly,
and that is not a trade to make silently. (The local provider needs no such guard: each build lands in its
own release directory and the marker only moves once the copy is done, so an empty build costs one unused
release and leaves what is serving untouched.) Both halves are [D29](DECISIONS.md).

| | |
|---|---|
| **Owns** | one resource, `s3://{bucket}`, fingerprinted by object count and total size — enough to catch a site that lost files or was replaced, not an edit that preserves the byte count. Nothing when the bucket is empty |
| **Produces** | nothing. The URL belongs to the stack that made the distribution |
| **Removing** | **empties** the bucket. The bucket itself belongs to the stack that created it, and that stack's own removal takes it — a unit that deleted another unit's resource would break reverse-order teardown by reaching sideways |

Content types are set per extension on upload. Not polish: S3 defaults every object to
`application/octet-stream`, which makes a browser download a page instead of rendering it, so a site
uploaded without them does not work.

### The CloudFormation phase table

The only place a CloudFormation status string is interpreted, and the place where a wrong answer is
invisible: a status read as `Ready` that is not becomes an update against a stack that refuses it, one read
as `Converging` that is not becomes a wait that never ends, and one read as `Broken` that is not becomes a
stack — and a database — deleted and remade for nothing.

| Status | Phase | |
|---|---|---|
| *stack does not exist*, `DELETE_COMPLETE` | `Missing` | a tombstone CloudFormation keeps for a while; reading it as a `_COMPLETE` would skip the create |
| `REVIEW_IN_PROGRESS` | `Broken` | a shell left by a change set that was never executed. It ends in `_IN_PROGRESS` and nothing is happening |
| `ROLLBACK_IN_PROGRESS`, `DELETE_IN_PROGRESS` | `Unwinding` | heading for a state CloudFormation will not update |
| `ROLLBACK_COMPLETE` | `Broken` | the rollback of a failed **create**. CloudFormation refuses to update it; it can only be deleted |
| anything ending `_FAILED` | `Broken` | |
| anything else ending `_COMPLETE` | `Ready` | including `UPDATE_ROLLBACK_COMPLETE` |
| anything else ending `_IN_PROGRESS` | `Converging` | including `UPDATE_ROLLBACK_*`, which settle into something usable |
| anything unrecognised | `Broken` | a replace, which a plan shows before it happens — safer than an update against a state nobody understood |

**The subtlest line is `ROLLBACK_COMPLETE` against `UPDATE_ROLLBACK_COMPLETE`.** One character apart, and
opposite answers. The first is a create that failed and can only be deleted. The second is an *update* that
failed and reverted, leaving the stack at its previous good configuration, which CloudFormation is perfectly
happy to update again — treating the two alike would delete a working stack because one template change was
wrong.

**`UPDATE_ROLLBACK_FAILED` is `Broken`, and it is the expensive one.** Recovering it properly needs
`ContinueUpdateRollback`; the action this phase produces is a delete and recreate. That is deliberate — it
is what the deployer this was ported from did — and it is safe only because a plan shows `REPLACE` before
anything happens. **Read `plan.Replacements` before applying to a stack holding data.**

A wait treats *any* rollback as a failure, including the ones that leave a usable stack: a stack that
reverted is at the wrong configuration, and reporting success over it would tell the operator their change
shipped when it did not.

### How AWS's failures are classified

Every code below is one a real deployment actually hit, in an application a non-technical owner runs
unattended. None was reasoned into the list — which is why none should be removed for looking redundant.

| Class | Codes |
|---|---|
| `Credentials` | `ExpiredToken`, `ExpiredTokenException`, `InvalidClientTokenId`, `UnrecognizedClientException`, `RequestExpired`, `InvalidAccessKeyId`, `SignatureDoesNotMatch`, `AuthFailure`, `TokenRefreshRequired`, `InvalidSecurityToken`, `InvalidSecurity` |
| `Transient` | anything the SDK itself marks retryable, plus `Throttling`, `ThrottlingException`, `RequestLimitExceeded`, `RequestTimeout`, `RequestTimeoutException`, `ServiceUnavailable`, `InternalFailure`, `InternalError`, `PriorRequestNotComplete`, any 5xx, `429`, and `HttpRequestException` / `SocketException` / `TimeoutException` below the SDK |
| `Hard` | everything else, by not being recognised — a malformed template, a quota that needs a human, an unverified account. No amount of retrying or re-authenticating resolves any of them |

The SDK's own `Retryable` verdict is checked first, because it knows about codes this list has never seen.

---

## What neither provider does

Each of these is a decision, not an omission, and each is cheaper to know now than to discover:

- **Neither builds or synthesizes.** Tyanor executes an artifact that already exists. Run `cdk synth`,
  `helm template` or `dotnet publish` earlier, on a machine that has that toolchain ([D5](DECISIONS.md)).
- **Neither manages secrets.** `TargetCredentials` is a parameter; where credentials come from is your
  application's business.
- **Neither orchestrates.** Ordering, reconcile, bounded retry and the pause/fail decision are the engine's.
  A provider that grows run-state logic is writing a second engine inside itself — the local one was tempted
  twice, and both times the engine already had the answer.
- **Neither is discovered from disk.** You register a target in your composition root, in one line
  ([D6](DECISIONS.md)). Writing your own is fully supported and is a different question from loading one.

## What is verified, and where the last mile starts

The local provider deploys real files and starts real processes in its own tests, so there is no gap between
what is tested and what ships.

The AWS provider is different and the difference is worth being exact about, because "ported" and "proven"
are not the same word. **No request from this repository has reached AWS.** Everything up to that point is
covered offline — the phase table against every status the SDK enumerates, the classifier against real error
codes, which request each call builds, the driver's control flow, and **both** unit kinds against
`UnitDriverContract`. What is left is only what a fake cannot answer without inventing it: whether AWS
accepts what we send, and whether it answers the way the phase table believes.

That last mile is deliberately not simulated. A fake that asserted a create settles into `CREATE_COMPLETE`,
or that `UPDATE_ROLLBACK_FAILED` behaves as assumed, would be this repository agreeing with itself — so the
fakes model only that a created stack exists and a deleted one does not, and the rest sits behind
`TYANOR_LIVE_AWS` ([D23](DECISIONS.md)). The full split is in [`TASKS.md`](../TASKS.md), item 1.

**Need something neither has?** A whole provider is a lot to write when what you have is one step. Register
it as a unit kind inside the provider you are already using — see *Extending it* in
[`guide.md`](guide.md#extending-it), and [`../.claude/skills/add-provider/SKILL.md`](../.claude/skills/add-provider/SKILL.md)
when it really is a whole provider.

---

## A complete request, per provider

Both compile. Neither is a fragment.

**A self-hosted server on this machine** — files, then the process that runs them:

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
        ["runtime.kind"] = LocalOptions.DirectoryKind,
        ["runtime.source"] = "app",

        ["service.kind"] = LocalOptions.ProcessKind,
        ["service.command"] = "dotnet",
        ["service.args"] = "Server.dll --urls http://localhost:8080",
        ["service.watch"] = "runtime",              // restart when runtime's content moves
        ["service.health.port"] = "8080",
        ["service.health.seconds"] = "90",          // a slow first boot, said out loud
    });

var runner = new ProcedureRunner(new LocalTarget("/srv"), history, state);
await runner.ApplyAsync(procedure, request, Console.WriteLine);
```

**A site on AWS** — three stacks, then the files that go in the bucket the last one made:

```csharp
var procedure = new Procedure("site",
[
    new ProcedureUnit("db", "Database", Weight: 4),
    new ProcedureUnit("api", "API", Weight: 3),
    new ProcedureUnit("web", "Website"),
    new ProcedureUnit("content", "Website files"),
]);

var request = new DeploymentRequest("mysite",
    new DeploymentArtifact(new Dictionary<string, string>
    {
        ["db-template"] = "bundle/mysite-db.template.json",
        ["api-template"] = "bundle/mysite-api.template.json",
        ["web-template"] = "bundle/mysite-web.template.json",
        ["lambda"] = "bundle/assets",
        ["site"] = "dist/web",
    }),
    new Dictionary<string, string>
    {
        ["kind"] = AwsOptions.StackKind,            // all but one unit, so it is written once

        ["db.template"] = "db-template",
        ["db.parameter.InstanceClass"] = "db.t4g.micro",

        ["api.template"] = "api-template",
        ["api.assets"] = "lambda",                  // the Lambda zips the template refers to

        ["web.template"] = "web-template",

        ["content.kind"] = AwsOptions.ContentKind,  // the exception
        ["content.source"] = "site",
        ["content.bucketFrom"] = "web:webbucketname",
        ["content.invalidateFrom"] = "web:distributionid",
    },
    Tags: new Dictionary<string, string> { ["Application"] = "mysite" });

using var aws = new AwsTarget(credentials);
var runner = new ProcedureRunner(aws, history, state);

var plan = await runner.PlanAsync(procedure, request);
if (!plan.IsDestructive) await runner.ApplyAsync(procedure, request, Console.WriteLine);
```

---

Back to [`guide.md`](guide.md), or [`adoption.md`](adoption.md) if you are moving an existing deployment.
Why any of this is the way it is: [`DECISIONS.md`](DECISIONS.md).
