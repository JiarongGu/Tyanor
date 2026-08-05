# Rules Index

Every rule in `.claude/rules/` is registered here. Read the ones matching your task **before** writing
code — they encode decisions that are expensive to rediscover. A rule that is not listed here is invisible
to the workflow, so add a row when you create one.

| Rule | When it applies |
|---|---|
| [`reconcile-dont-mirror.md`](reconcile-dont-mirror.md) | ANY engine or provider work — no state file; read the provider, decide, act; resume is a re-run; a live run record is protected |
| [`error-classification.md`](error-classification.md) | Any failure handling — credentials / transient / hard, and the different response each earns |
| [`units-not-graphs.md`](units-not-graphs.md) | Anything touching `Procedure` — ordered list, reverse teardown, no DAG |
| [`provider-boundary.md`](provider-boundary.md) | Adding or changing a provider, and ANY change to `Tyanor.Core` — nothing vendor-shaped crosses in |
