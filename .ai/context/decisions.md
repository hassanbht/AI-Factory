# AI Factory — Decision Log

Append-only. One entry per decision: date, decision, reason, alternative
considered. Do not delete or rewrite past entries — supersede them with a
new dated entry that references the old one.

## 2026-08-31 — SQLite is the sole authoritative workflow store

Decision: workflow/task/run/artifact-metadata state lives only in the
Factory's own SQLite database (`data/factory.db`), never in Hermes's own
state DB (`~/.hermes/state.db`, `kanban.db`, ...) and never in OpenViking.
Reason: three storage systems (SQLite, Git/filesystem, OpenViking) must
stay separated by responsibility — merging them into one store was
identified as a likely large mistake if made early.
Alternative considered: let Hermes's own DB double as Factory state —
rejected, breaks the ownership boundary in `AGENTS.md` § "Core principle".

## 2026-08-31 — OpenViking deferred until Orchestrator is stable

Decision: do not integrate OpenViking, Ponytail, or any learning layer
before Domain → Persistence → Workflow Engine → Task DAG are implemented
and validated.
Reason: adding a knowledge/retrieval layer before the orchestration core
is deterministic and testable would make failures hard to localize —
unclear whether a bug is in the workflow engine or the retrieval layer.
Alternative considered: build context/learning alongside the core from day
one — rejected for the same reason.

## 2026-08-31 — Concurrency = 1 for the initial Task DAG scheduler

Decision: the first Task DAG scheduler implementation executes at most one
task at a time.
Reason: reliability before performance — parallel execution adds a large
class of concurrency bugs (double-execution, race conditions on task
state) that are not worth the risk before the sequential path is proven.
Alternative considered: parallel execution from the start — deferred, not
rejected; revisit once the sequential engine is stable and tested.

## Phase-1 entity model diverged from the original draft spec

Decision: keep the already-implemented `Project`/`WorkItem` shape
(`RootPath`, `GitRepository`, `GitBranch`, `Feature`/`Requirement`
hierarchy, string-typed `WorkItem.Status`) rather than rewriting it to
match an earlier draft prompt's exact field list.
Reason: the existing entities were already in the repository and working;
`AGENTS.md` requires extending existing abstractions over duplicating them
and not rewriting unrelated files.
Alternative considered: rewrite `Project`/`WorkItem` to match the draft
spec exactly — rejected, would have discarded working fields with no
functional benefit.
