# Phase 18.26 — Production Hardening, Model Routing & Live Worker Recovery

## 1. Defects found by inspection (not assumed)

| # | Defect | Consequence |
|---|---|---|
| 1 | **Nothing recovered AgentRuns.** `WorkflowRecoveryHostedService` swept WorkItems (`ITaskStateManager`) and OrchestrationRuns (`RecoverStaleLeasesAsync`) only. | A Worker that died mid-run left its AgentRun `Running` forever. `IProjectCompletionGate` blocks on `HasActiveRunsForProjectAsync`, so the owning Project could **never** reach Testing/HumanReview again — a permanent deadlock after any crash. The two layers also disagreed: the WorkItem moved to `Repairing` and a *second* AgentRun was dispatched while the first was still nominally active. |
| 2 | **The resolved model was never persisted.** `AgentOrchestrator.ResolveRoleModel` wrote the model into the transient snapshot passed to the runtime, not onto the row. | `AgentRun.Model` / `AgentRun.Provider` were always `null` in the database. §13 telemetry was unreconstructable except from transient logs. |
| 3 | **No usage data exists on the Hermes path.** `HermesAgentRuntime` hard-codes `PromptTokens/CompletionTokens/TotalTokens: null` on every return path, and `AgentExecutionResult` has no usage field. A full live run's stdout contained zero usage tokens. | Per-AgentRun cost is *not obtainable*. §14 forbids inventing it, so the honest answer is `usage unavailable`. |

Confirmed unchanged and not recreated: `IWorkflowEngine` / `WorkflowEngine` / `WorkflowStateStore` / `WorkflowTransitionValidator` remain absent.

## 2. Workstream A — persistent lifecycle observability

`ProjectCompletionGate` now writes durable audit rows through the existing `IAuditLogger`. No new event store, no new `AuditEventType` values.

| Event | `EventType` | `Outcome` | Actor |
|---|---|---|---|
| Implementation → Testing | `WorkflowStateChanged` | `Testing` | `System` / `ProjectCompletionGate` |
| Project verification | `ValidationResult` | verification status | `System` / `ProjectCompletionGate` |
| → Reviewing | `WorkflowStateChanged` | `Reviewing` | `System` / `ProjectCompletionGate` |
| → HumanReview | `WorkflowStateChanged` | `HumanReview` | `System` / `ProjectCompletionGate` |
| Fail-closed | `WorkflowFailed` | `Failed`/`Cancelled` | `System` / `ProjectCompletionGate` |

Every transition funnels through one `TryTransitionAsync`, so that is the single insertion point. Only the Factory's own bounded summary (the same text `Project.CurrentError` already holds) is carried — never raw process or model output. `IAuditLogger` is an **optional** constructor parameter so every pre-existing direct caller still compiles.

## 3. Workstream B — real Worker recovery

### AgentRun orphan recovery

New `IAgentRunRepository.RecoverOrphanedRunsAsync(DateTime? startedBeforeUtc, …)`, mirroring `ITaskStateManager`'s existing two-mode design exactly:

- `null` cutoff — startup sweep. A full-process restart proves nothing this process owns is executing.
- a cutoff — periodic sweep, restricted to runs that started before it, so a genuinely executing run in the live process is never touched.

Recovery is **terminal, never a silent re-execution**: `Running → Failed` through the validated `AgentRun.TransitionTo` edge, `FailureCategory.Infrastructure`, and an `AgentRunRecovery.OrphanedMarker`-prefixed reason. Deciding what runs next stays with the existing WorkItem-repair and Project-lifecycle owners rather than being duplicated in recovery.

Wired into both modes of `WorkflowRecoveryHostedService`.

### Crash-scoped Planner retry

WorkItem-backed runs get a second chance from the WorkItem repair path. A Planner run has no such layer, so a routine Worker restart would otherwise permanently fail a healthy Project. `ProjectLifecycleOrchestrator` therefore re-dispatches the Planner **only** when:

- the previous Planner run was terminated by orphan recovery (`AgentRunRecovery.WasOrphaned`), **and**
- the Project has had at most `MaxPlannerCrashRetries + 1 == 2` Planner runs.

A genuinely failed Planner, and a second crash in a row, still fail closed. The retry is audited as `Retry` / `Warning`.

## 4. Workstream C — model routing policy

`CategoryProfiles[category]` was **always** an ordered candidate list; only the winner was exposed. So the policy needed no new configuration shape:

- `IModelRouter.SelectCandidates(request)` returns the ordered list — index 0 is the role's primary, the rest its fallbacks.
- `SelectModel` is now `SelectCandidates(request)[0]`.

`AgentOrchestrator` resolves candidates **before** execution, persists `Model`/`Provider` on the AgentRun (fixing defect 2, zero migrations), and may make exactly one fallback attempt within the same AgentRun (`Attempt++`, never a second run).

### Fallback is permitted only for

`FailureCategory.Provider`, `FailureCategory.Model`, `FailureCategory.Infrastructure`.

### Fallback is forbidden for

`Validation`, `Build`, `Test`, `Agent`, `Review`, `Approval`, `Timeout`, `Cancelled` — every one of those is a *result*, and re-running it on another model would launder a failed acceptance contract or a failed test into a retry.

An explicit `AgentRun.Model` wins and disables fallback entirely: a caller that named a model chose it deliberately.

### Live profile (§11 hypothesis, env-overridable)

| Role | Primary | Fallback |
|---|---|---|
| Planner, Review | `z-ai/glm-5.3-flash` | `openrouter/auto-beta` |
| Implementation, Repair, Test | `qwen/qwen3-coder` | `poolside/laguna-s-2.1` |

`AI_FACTORY_LIVE_<ROLE>_FALLBACK_MODEL` overrides; empty string removes a role's fallback. Laguna advertises `response_format: false`, which is exactly why it is a coding fallback and never a Planner primary. No `:free` route is a primary (§12).

## 5. Workstream D — cost visibility

Per-AgentRun usage is **unavailable** on the Hermes path and is reported as such rather than fabricated. Two deterministic tests lock this in: nulls stay null when the runtime supplies nothing, and supplied usage is persisted verbatim when a future runtime does supply it.

Aggregate project cost is measured honestly from the OpenRouter credits delta around a run, and labelled as aggregate rather than per-run.

## 6. Live evidence (OBSERVED, 2026-09-18)

### Worker recovery — `LiveWorkerRecoveryE2ETests`

```
── Before Worker stop ──
ProjectId       = 4426ba70-ba39-4fc2-a965-0927ca8a14e2
Project Status  = Planning
AgentRunId      = e8dccffc-af22-4800-9dda-f69bda9afe2c
AgentRun Status = Running
AgentRun Model  = z-ai/glm-5.3-flash
── Worker stopped ──   AgentRun after stop = Running        (the orphan state)
── Worker restarted ── AgentRunOrphanRecovery Mode=Startup RecoveredCount=1
── After recovery ──
recovery decision = RESUMED EXISTING WORK (orphan terminalized; recovery created no new authoritative run)
AgentRun outcome  = Failed / Infrastructure
Project outcome   = Implementation                          (lifecycle resumed on its own)
duplicate check   = 2 Planner AgentRun(s) = original + exactly 1 crash retry (bounded)
```

No duplicate WorkItem plan keys. The interruption is a `StopAsync` with an already-cancelled token: the shutdown drain is abandoned rather than allowed to complete, so the in-flight AgentRun is never terminalized by the dying Worker — the state a real process kill leaves behind.

### Final Golden E2E — full durable audit trail

| TimestampUtc | Event | Outcome | Actor |
|---|---|---|---|
| 12:30:33.563 | WorkflowStarted | Analyzing | Human/qa-lead@factory.local |
| 12:30:35.307 | WorkflowStateChanged | Specification | System/ProjectLifecycleOrchestrator |
| 12:30:37.260 | WorkflowStateChanged | Architecture | System/ProjectLifecycleOrchestrator |
| 12:30:39.304 | WorkflowStateChanged | Planning | System/ProjectLifecycleOrchestrator |
| 12:30:41.775 | AgentStarted | Planning | System/ProjectLifecycleOrchestrator |
| 12:34:51.256 | WorkflowStateChanged | Implementation | System/ProjectLifecycleOrchestrator |
| 12:44:47.279 | WorkflowStateChanged | Testing | System/ProjectCompletionGate |
| 12:44:55.992 | ValidationResult | Passed | System/ProjectCompletionGate |
| 12:44:55.996 | WorkflowStateChanged | Reviewing | System/ProjectCompletionGate |
| 12:44:56.002 | WorkflowStateChanged | HumanReview | System/ProjectCompletionGate |

The gate rows are new in this phase — before it, that half of the lifecycle existed only in transient logs.

Same run: plan persisted (2 tasks, `PlanHash=41A2AFD4…`); `greeting.message` timeline `Running → Testing → Repairing → Running → Testing → Completed` with `RepairAttemptCount=1`; `docs.release-notes` first AgentRun at 12:42:11.245 after its prerequisite completed 12:42:08.652; verification `Passed` in 6.7s; explicit approval → `Completed`. Duration 14m 44s, cost **$0.4145** (credits delta).

Model telemetry, persisted on each AgentRun:

| AgentType | Model | Provider | Fallback used |
|---|---|---|---|
| Planner | `z-ai/glm-5.3-flash` | openrouter | NO |
| Implementation | `qwen/qwen3-coder` | openrouter | NO |
| Repair | `qwen/qwen3-coder` | openrouter | NO |
| Documentation | *(unresolved)* | *(none)* | NO |

## 7. Known gaps

- **`Documentation` has no routing category.** `ModelRoutingOptions.AgentTypeCategories` maps Implementation/Repair/Test/Review/Planner only, so a Documentation AgentRun resolves no model and falls through to `HermesOptions.DefaultModel`. Observed live. Not fixed here because inventing a category mapping was outside §9's "preserve that architecture" instruction, but it is a real routing hole.
- **Fallback is proven deterministically, not live.** No provider failure occurred during the final Golden run, so `Fallback used: NO` is an observation, not a demonstration of the fallback path against a real provider.
- **Per-AgentRun cost remains unavailable** until Hermes surfaces provider usage.
- **Single-Worker assumption.** The startup sweep terminalizes *every* `Running` AgentRun and WorkItem unconditionally. With two Workers sharing a database, one starting would kill the other's live work. Correct for the current single-Worker deployment; a lease/owner column would be required before scaling out.
- **A crash costs one repair attempt.** Recovery moves the WorkItem to `Repairing`, and the subsequent dispatch increments `RepairAttemptCount`, so a crash consumes budget that a genuine failure would have. Bounded and visible, but not free.
- **Three timing-sensitive pre-existing tests flake under parallel suite load** (`Approve_ConcurrentRequests_BothSucceed`, `ExecuteAsync_CancelledDuringBackoffDelay`, `Heartbeat_MultipleAgentRuns_EachHeartbeatsIndependently`). Each passes repeatedly in isolation; a different one fails per loaded run. None are in code this phase touched.
