# Nothing Provider-Shaped Crosses Into Core

**The `Tyanor` namespace must never name a vendor, a tool, or a product. If a type there mentions CDK,
CloudFormation, kubectl, an S3 bucket or a Lambda, the abstraction has already failed — whether or not a
second provider exists yet.**

> Since **D26** this is a namespace, not a separate assembly: `Tyanor` and `Tyanor.Providers.*` ship as
> separate packages, but the neutral core and the engine are one package. **The compiler no longer stops a
> leak** — it never really did, since the defect below compiled fine, but the reference graph used to make
> one obvious.
>
> **`npm run doctor` now checks it** (`node devtools/dev.mjs boundary`): no vendor word in the `Tyanor`
> namespace's code or string literals, camel-cased identifiers included. **Comments are exempt**, because
> this rule is taught by naming what it refuses and a check that banned the words would ban the explanation.
> So the boundary is enforced again for the leaks that are mechanical, and still needs reading for the ones
> that are not — a neutrally-named field that only one provider could ever fill.

## Why

This is not a hypothetical risk. It is the concrete defect that made the original code unportable: its
"generic" `DeploymentRequest` carried `CdkOutDir` and `WebDir`, so the neutral interface named an AWS tool
and assumed the deployment contained a single-page app. No second provider could have implemented it — and
nobody noticed, because there was only ever one.

That kind of leak is invisible while there is a single provider and expensive the moment there are two: by
then consumers depend on the leaked shape.

## How to Apply

- **Artifacts are opaque named parts.** `DeploymentArtifact` is `name → path`. Core does not know that
  `"infrastructure"` happens to be a synthesized CloudFormation assembly; only the AWS provider does.
- **Provider- and procedure-specific settings live in `DeploymentRequest.Options`** (a string map), not as
  new fields. The moment Options becomes typed fields it grows one per provider and stops being neutral.
- **Status vocabulary is mapped, never passed through.** A provider translates its own strings into
  `UnitPhase` inside `PhaseAsync`. No provider status string reaches the engine.
- **Errors are classified, not surfaced raw** — see [`error-classification.md`](error-classification.md).
- **Per-unit settings use `request.Option(unit, key)`**, which reads `"{unit}.{key}"` and falls back to
  `"{key}"`. A provider whose units are all the same kind of thing never needs it — every CloudFormation
  unit is a stack — but one with a directory here and a process there does, and without the convention in
  the contract each provider would invent its own.
- **A setting that IS the unit's identity uses `request.Address(unit, key)`**, which does NOT fall back and
  REFUSES a procedure-wide one. Where a unit lives on disk, which bucket it fills, which port it answers on:
  for these the shared value is never a sensible default, it is every unit deploying on top of every other.
  That is the collision `Procedure` refuses when two units share a name, arriving through a different door —
  so ask which one a setting is before reaching for the convenient reader.
  - **Not `OwnOption`, which is only half of it** (D36). Not falling back stops the sharing and leaves the
    operator's line read by *nothing*, silently — which is the second defect the local provider shipped for
    a release after the first was fixed. `Address` throws a `DefinitionException`, so one call gives you
    both the offline report and the apply-time refusal. `OwnOption` remains for a setting that is genuinely
    per-unit without being an address.
- **The test:** would a Kubernetes provider and a bare-SSH provider both implement this without pretending?
  If either has to invent a meaning for a field, it belongs in `Options` or in the provider.
- **A target may have no credentials at all**, so `ValidateAsync` takes `TargetCredentials?` — null means
  the identity is ambient (this machine's user, an instance role, an already-selected context). The
  non-nullable version was the same defect as `CdkOutDir`, just quieter: a neutral contract asserting the
  circumstances of the only provider that existed (`docs/DECISIONS.md` D13).
- **Core validates what is wrong against EVERY target; a provider validates its own, in its own words.**
  `Identifiers` refuses a prefix or unit name that could be read as a path, because that is true everywhere.
  It allows `_` and `.`, which CloudFormation stack names do not — so `StackUnit` refuses those itself and
  says why. Putting CFN's charset in Core would be the leak this rule is about; leaving the gap open would
  make an operator wait for a round trip to learn their name was invalid (D17).

## The authoring / executing split

Tyanor EXECUTES a pre-built artifact; it does not compile infrastructure at apply time. Synthesis
(`cdk synth`, `helm template`, a compile) happens earlier, on a machine that has that toolchain.

This is why an operator can deploy with no cloud SDK installed — a property the original deployer needed
because its user is a non-technical owner running a desktop app, and one worth keeping deliberately rather
than by accident.

## Related

- [`units-not-graphs.md`](units-not-graphs.md) · `../skills/add-provider/SKILL.md` · `docs/DECISIONS.md` D4
