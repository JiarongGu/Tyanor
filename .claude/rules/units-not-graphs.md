# Units Are an ORDERED LIST, Not a Dependency Graph

**A procedure is a list of units applied in order and removed in reverse. There is no DAG, no dependency
resolution, and no plan/diff over a resource universe. This is a deliberate ceiling, not a gap.**

## Why

The resource graph is where tools of this kind become large. A DAG needs dependency declaration, cycle
detection, partial-order execution, a diff engine over every resource type, and a plan format to render it
— and each of those grows escape hatches once real infrastructure fails to fit.

Ordering covers the overwhelming majority of real deployments: data before compute before edge. The
deployer Tyanor was extracted from ships three units in a fixed order and never needed more. Reverse order
for teardown then falls out for free, and is *always* right, because whatever imports from a unit is
removed before the unit itself.

The property being protected is that a person can read a procedure and know what will happen. A list has
that property. A graph does not.

## How to Apply

- Declare units in `Procedure.Units` in apply order. `Reverse()` is the teardown order — never maintain a
  second list, which can disagree with the first.
- Express "A needs B" by putting B first. If that is genuinely impossible, **change the operation before
  you reach for a second ordering.** The case that will tempt you looks like this: replacing the files a
  server runs out of needs the server stopped, so the service unit must come both after the runtime unit
  and before it. That is unorderable as stated — and it dissolves the moment each build is written to its
  own directory instead of over the last one, because then nothing conflicts and the restart falls out of
  the fingerprint changing. Only when no operation works are they one unit. (`docs/DECISIONS.md` D13.)
- Weight units so a progress bar does not lie: a ten-minute unit and a ten-second one should not each be
  a third of the run.
- `ProcedureUnit.Name` is the resume key. It must not change between runs of the same procedure, or the
  reconcile will read the unit as `Missing` and create a duplicate.

## When to revisit

Only when a real consumer has a real fan-out that ordering genuinely cannot express — not when one seems
imaginable. Sibling libraries in this family defer such work until at least two real consumers need it;
the same bar applies here. If it ever happens, add edges to `Procedure` — do not add a plan format.

The bar has already survived one real attempt: a second provider shape produced a genuine
must-be-both-before-and-after constraint, and it was absorbed without edges (D13). Bring the next one to
that standard before adding any.

## Related

- [`reconcile-dont-mirror.md`](reconcile-dont-mirror.md) · `docs/DECISIONS.md` D3
