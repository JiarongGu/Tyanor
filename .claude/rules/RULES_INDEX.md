# Rules Index

Every rule in `.claude/rules/` is registered here. Read the ones matching your task **before** writing
code — they encode decisions that are expensive to rediscover. A rule that is not listed here is invisible
to the workflow, so add a row when you create one.

| Rule | When it applies |
|---|---|
| [`reconcile-dont-mirror.md`](reconcile-dont-mirror.md) | ANY engine or provider work — read the provider, decide, act; resume is a re-run; state records what Tyanor OWNS and never feeds the decision; a live run record is protected |
| [`error-classification.md`](error-classification.md) | Any failure handling — credentials / transient / hard, the different response each earns, and why `Credentials` is not about tokens |
| [`units-not-graphs.md`](units-not-graphs.md) | Anything touching `Procedure` — ordered list, reverse teardown, no DAG, no DSL for the pipeline |
| [`provider-boundary.md`](provider-boundary.md) | Adding or changing a provider, and ANY change to the `Tyanor` namespace — nothing vendor-shaped crosses in; the core checks what is universal, a provider checks its own |
