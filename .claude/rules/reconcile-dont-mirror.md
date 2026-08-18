# Reconcile Against the Provider — Never Mirror Its State

**Tyanor does not keep a model of what exists in the provider. Every run asks the provider what is true and
decides again. We record INTENT (a run happened, with this configuration); we read FACT from the target.**

> ## ⚠ Superseded in part by `docs/DECISIONS.md` **D12**
>
> Tyanor now DOES keep one set of deployment state — what it owns, per unit, local or remote — because a
> provider working with raw resources cannot tell you what Tyanor created, and without that a teardown
> cannot safely distinguish it from what was already there. `Refresh` re-syncs state from reality.
>
> **What survives, and is why the mirror is affordable:** the provider is still the arbiter of what is
> happening NOW. Reconcile reads phases live and attaches to converging work, so a stale mirror costs a
> wrong COUNT, never a wrong ACTION — and refreshing repairs it rather than requiring surgery. Read the
> rest of this rule as being about the RECONCILE loop, which is unchanged.
>
> **Two things are easy to confuse, and the distinction is the whole rule.** Tyanor **does** persist run
> state — `IRunHistory`, at a location the consuming application configures — because a run that cannot be
> found after the process dies cannot be resumed. What Tyanor never keeps is a **mirror of the provider's
> resources**. An earlier version of this rule said "there is no state file", which read as a ban on both
> and was wrong for a library; see `docs/DECISIONS.md` **D7**.
>
> The test: does the record describe **what we did**, or **what exists in the cloud**? The first is history
> and belongs here. The second is a cache of someone else's database and belongs nowhere.

## Why

This is the deliberate fork from Terraform, and it is the reason resume works.

A state file is a *cache of someone else's database*, and it inherits every problem a cache has: it drifts
when anything changes outside the tool, it needs locking so two operators do not corrupt it, and when it
does go wrong the repair is surgery on the tool's private bookkeeping (`terraform state rm`) rather than on
the infrastructure. A large share of the operational pain in state-file tools is pain about the state file.

Meanwhile the provider is *already* a database of what exists, it is authoritative, and — this is the part
that matters — **it keeps converging whether or not our process is alive**. A CloudFormation stack does not
stop because the laptop closed. So the durable state we would be mirroring is one we cannot lose.

Dropping the mirror buys three things at once:

- **Resume is a re-run.** There is no separate resume path to keep in step with the apply path, so the two
  cannot disagree. `ProcedureRunner.ApplyAsync` is the resume.
- **Concurrency stops being special.** A second operator running the same procedure reconciles against the
  same truth and attaches to whatever is in flight, instead of fighting a lock.
- **A crash is uninteresting.** Nothing needs recovering, because nothing local was authoritative.

This was not reasoned into existence — it was extracted from a deployer that survived a real crash and
rebuild mid-deploy and resumed to completion, in an app a non-technical owner runs.

## How to Apply

- **Read the phase, decide, act.** `IUnitDriver.PhaseAsync` → `Reconcile.Decide` → one action. A provider
  adapter's whole job on the read side is mapping its own vocabulary onto `UnitPhase`.
- **A teardown is decided the same way**, by `Reconcile.DecideDestroy` — so the teardown a plan SHOWED and
  the teardown that runs come from one function rather than two that can drift apart. Both directions get a
  plan; the destructive one needs it most.
- **Never re-issue against `Converging`.** The action is `Attach`: watch, issue nothing. Some providers
  reject a second operation; the dangerous ones accept it. `Reconcile.Mutates(Attach)` is `false` and a
  test pins it.
- **Record intent, not resources.** `IRunHistory` stores that a run was attempted, with what, and how it
  ended. It must never grow a list of created resources — that is the mirror coming back in disguise.
- **A `Running`/`Paused` record is LIVE and protected.** It is the operator's only handle on work that may
  still be converging. `IRunHistory.DeleteAsync` must refuse one, and a caller must check `LiveAsync`
  before starting new work on the same procedure + prefix.
- **A resume continues the same run id.** Otherwise one interrupted job appears in the history as five
  failures, and the operator learns to distrust the record.
- **Cancellation leaves the run live**, deliberately: the provider is still converging, and marking it
  failed would hide that. **Record the ending with no token.** Being told to stop is not a reason to stop
  saying WHY you stopped — writing the outcome with the token that was just cancelled means any store
  honouring it skips the write, and the run stays recorded as `Running` with no reason. A token already
  cancelled when the run is asked to start is different: nothing happens and nothing is recorded.

### Where a cache IS legitimate

Content hashes of the *artifact* — "is what I am about to deploy different from what I deployed last
time?" — are a fact about OUR inputs, not about the provider's world. That is a legitimate local record
and is how a run can skip expensive units entirely. It is not a mirror because nothing in the provider can
make it stale.

## Edge cases

- **A provider with no readable status.** Then it cannot be reconciled and does not belong behind
  `IUnitDriver` — wrap it in something that can be asked, or treat it as a one-shot step.
- **Expensive status reads.** Cache within a single run if you must; never across runs.

## Related

- [`units-not-graphs.md`](units-not-graphs.md) — the other half of staying small
- [`error-classification.md`](error-classification.md) — what a stop means
- `docs/DECISIONS.md` D1
