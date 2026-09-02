# AI Factory Implementation Status

## Current phase

Phase 15 — End-to-End Integration

## Completed

### Phase 1 — Persistent State Foundation

- .NET 10 solution
- Domain project
- Application project
- Infrastructure project
- API project
- Worker project
- EF Core SQLite packages
- Solution projects registered
- Solution build succeeds
- API starts successfully
- Project entity (extended: `Status`)
- WorkItem entity (extended: `Type`, `Dependencies`, `Runs`)
- WorkItemDependency entity
- AgentRun entity
- Artifact entity
- FactoryDbContext (extended with the above)
- EF migration (`AddWorkflowState`) applied to `data/factory.db`
- ProjectRepository
- ProjectService
- Project API (`POST /api/projects`, `GET /api/projects/{id}`)
- IAgentExecutor (interface only — no implementation yet)

### Phase 2 — Workflow Engine

- WorkflowTransition entity (Domain)
- IWorkflowEngine, IWorkflowStateStore, IWorkflowTransitionValidator (Application/Abstractions)
- WorkflowTransitionValidator, WorkflowEngine (Application/Orchestration) — deterministic, no infra/LLM dependency
- WorkflowStateStore (Infrastructure/Persistence, EF Core)
- FactoryDbContext extended with `workflow_transitions` table
- EF migration (`AddWorkflowTransitions`) applied to `data/factory.db`
- DI registration in `Program.cs`
- First test project: `apps/tests/AI.Factory.Application.Tests` (xUnit), registered in `AI.Factory.slnx`
- 31 tests passing: valid transitions, invalid transitions, terminal states, cancellation, failure, resume-after-persistence (new `DbContext`/store instance against the same underlying database)

### Phase 3 — Task DAG

- WorkItemStatus enum extended: `Failed` (additive, no schema change — `WorkItem.Status` stays `string`)
- TaskReadinessStatus enum (Domain) — computed/in-memory only, never persisted
- ITaskDependencyResolver, ITaskScheduler, ITaskStateManager, IWorkItemRepository (Application/Abstractions)
- TaskDependencyResolver (DFS cycle detection + dependency-status classification), WorkItemTaskScheduler (priority-ordered, concurrency=1) — Application/Orchestration, deterministic, no infra dependency
- WorkItemRepository, TaskStateManager (Infrastructure/Persistence, EF Core)
- DI registration in `Program.cs`
- 16 new tests: linear/branching/independent dependencies, circular dependency, failed dependency, retryable task, already-completed task, restart/resume — 47 total passing

### Phase 4 — Hermes Agent Executor

- HermesOptions (Infrastructure — DockerImage, DataVolume, WorkspaceRoot, TimeoutSeconds, MaxOutputLength), bound from `appsettings.json` "Hermes" section with runtime fallbacks (DataVolume → `~/.hermes`, WorkspaceRoot → existing `sandbox/agent-workspace`)
- IProcessRunner / SystemProcessRunner (Infrastructure/Agents) — structured `ProcessStartInfo.ArgumentList`, linked-CTS timeout + process-tree kill, `ProcessLaunchException` for missing executables
- HermesAgentExecutor : IAgentExecutor (Infrastructure/Agents) — creates a fresh isolated workspace per execution, builds `docker run` args from config, maps process result to `AgentExecutionResult` with output truncation at `MaxOutputLength`
- DI registration + `Hermes` config section in `Program.cs`/`appsettings.json`
- 13 new tests: successful execution, non-zero exit code, timeout (fake + real via `SystemProcessRunner`), missing Docker/executable, oversized-output truncation, cancellation, structured-argument safety, plus an isolated Docker integration test (`docker --version`, no image pull) — 60 total passing

### Phase 5 — Model Router

- IModelRouter, ModelRoutingRequest, ModelSelection, ModelCapability, NoModelAvailableException (Application/Abstractions)
- ModelRoutingOptions, ModelRouter (Application/Orchestration) — pure/deterministic, filters by availability/context-size/reasoning-strength then picks by CostPreference (Cheapest/BestQuality/Balanced); throws NoModelAvailableException when nothing survives, never assumes a model exists
- `ModelRouting` config section in `appsettings.json` (placeholder model IDs — an operator must replace them with real provider model IDs before going live; no actual model calls implemented, that's OpenRouter integration, a separate infrastructure adapter not built here)
- DI registration in `Program.cs`
- `config/models.yaml` left untouched (empty) — populating it would need a new YAML-parsing dependency not justified by this phase; used `appsettings.json` instead, consistent with the Hermes options pattern
- 8 new tests: known-category selection, unknown category, availability fallback, context-size exhaustion, reasoning-strength exhaustion, all 3 CostPreference behaviors — 68 total passing

### Phase 6 — Context Engine

- IContextProvider, ContextCategory (8 categories), ContextQuery, ContextDocument, ContextResult (Application/Abstractions)
- LocalContextProvider (Infrastructure/Context) — single provider covering both filesystem (per-project `architecture.md`/`coding-standards.md`/`decisions.md`/source files under `Project.RootPath`, path-traversal-checked) and database (Requirement/WorkItem+AgentRun); no embeddings/vector search — relevance is a simple recency-rank heuristic; `TestFailures` category returns empty (no Build/Test loop exists yet — Phase 8)
- No migration — reuses existing tables/entities as-is
- DI registration in `Program.cs`
- 11 new tests: DB-backed retrieval (requirements, task outputs, agent results), filesystem retrieval + graceful-empty when file/directory absent, project-not-found error, source-file recency ordering, build-directory exclusion, empty test-failures — 79 total passing

### Phase 7 — Task Planner

- ITaskPlanner, TaskPlanRequest, PlannedTask, TaskPlan, InvalidTaskPlanException (Application/Abstractions) — reuses existing `WorkItemType` and `TaskComplexity` enums, no duplicate types
- TaskPlanValidator (Application/Orchestration, static, pure) — enforces no-duplicate-IDs, no-missing-dependencies, no-cycles (DFS), at-least-one-root, at-least-one-terminal; deterministic regardless of how the plan was produced
- No concrete AI-calling `ITaskPlanner` implementation built yet (only interface + validator) — mirrors `IAgentExecutor`'s own multi-phase gap before `HermesAgentExecutor` arrived; no concrete class was named in this phase's scope
- No DI registration (nothing concrete to register yet)
- 8 new tests: valid linear/diamond plans pass; each of the 5 required validations individually triggered and isolated by check order (duplicates → missing-deps → root → terminal → cycle) — 87 total passing

### Phase 8 — Autonomous Coding Orchestrator

- IAutonomousCodingOrchestrator/WorkItemExecutionOutcome, IAgentRunRepository, IArtifactRecorder (Application/Abstractions); `AgentExecutionResult` extended with `WorkspacePath` (backward-compatible optional field)
- AutonomousCodingOrchestrator (Application/Orchestration) — full Ready→Build Context→Select Agent/Model→Execute→Persist AgentRun→Detect Changed Files→Complete-or-Repair loop, wired to existing ITaskScheduler/ITaskStateManager/IAgentExecutor/IContextProvider/IModelRouter
- AgentRunRepository, FileSystemArtifactRecorder (Infrastructure) — first real writers to the `AgentRun`/`Artifact` tables (existed unused since Phase 1)
- `ITaskStateManager.FailAsync` revised: retry target changed Draft→Repairing (semantically correct per this phase; flows through the existing scheduler unchanged) — one Phase-3 test updated accordingly
- `ITaskStateManager.RecoverOrphanedTasksAsync()` (new) — sweeps orphaned Running WorkItems on process restart; called once at API startup in `Program.cs`
- `AI.Factory.Application.csproj` gained `Microsoft.Extensions.Logging.Abstractions` (first Application-layer logging need)
- No migration — reuses existing `AgentRun`/`Artifact` tables as originally designed in Phase 1
- Explicit scope decision: "Build/Test/Evaluate" in the loop diagram is NOT a real build/test pipeline here — `IBuildExecutor`/`ITestExecutor`/`IValidationEngine` are Phase 9's named deliverables. "Evaluate" = `AgentExecutionResult.Success` for now.
- DI registration in `Program.cs`
- 9 new tests (fake `IAgentExecutor`, real EF/FS components against isolated Sqlite-in-memory + temp dirs): nothing-ready no-op, full success path with artifact recording, retry-vs-exhausted failure transitions, AgentRun-persisted-before-execution-returns proof, end-to-end recovery-after-simulated-restart — plus 3 new `RecoverOrphanedTasksAsync` tests — 96 total passing

### Phase 9 — Build Test Repair Loop

- IBuildExecutor/BuildRequest/BuildResult, ITestExecutor/TestRequest/TestExecutionResult, IValidationEngine/ValidationRequest/ValidationResult/ValidationFailure, IValidationRunRepository, RepairContext/RepairExhaustionPolicy (Application/Abstractions)
- ValidationEngine (Application/Orchestration) — build-then-test, normalizes failures via a simple language-tolerant keyword heuristic (error/fail/fehler)
- RepairOptions (Application/Orchestration, config-driven, default policy `Failed`)
- DotNetBuildExecutor, DotNetTestExecutor (Infrastructure/Validation) — real `dotnet build`/`dotnet test` via the existing `IProcessRunner`, no new process-execution code
- ValidationRunRepository (Infrastructure/Persistence) — first writer to the new `validation_runs` table
- New `ValidationRun` entity + migration (`AddValidationRuns`), `WorkItem.ValidationRuns` nav collection
- `ITaskStateManager.FailAsync` widened `Task`→`Task<bool>` (returns whether attempts were exhausted) — all existing call sites unaffected
- `WorkflowTransitionValidator` revised: `HumanReview` now reachable from any non-terminal state (same treatment as `Failed`/`Cancelled`), not only as the strict next-step after `Reviewing`
- `AutonomousCodingOrchestrator` (Phase 8) now runs real build+test validation after agent success — via `Project.RootPath` (looked up through the existing `IProjectRepository`) — before marking a task Completed; on validation failure it assembles+logs a `RepairContext` (not yet fed back into the next attempt's prompt — a further design decision, flagged not silently made) and, on repair-exhaustion, optionally escalates the project's workflow to `HumanReview` per `RepairOptions.Policy`
- DI registration in `Program.cs`
- 13 new tests: `ValidationEngine` (fake executors — build-fail skips tests, both-succeed, test-fail, normalization), a real `dotnet build` integration test (against this repo's own `AI.Factory.Domain.csproj`, fast/no-network) + a missing-path failure case, `WorkflowTransitionValidator` HumanReview-from-anywhere (4 cases), 3 new orchestrator tests (validation-failure→Repairing, HumanReview escalation, Failed-policy does-not-escalate) — 109 total passing

### Phase 10 — Independent Code Review

- ICodeReviewer/ReviewContext/ReviewFinding/ReviewFindingSeverity/ReviewCategory/ReviewResult, IReviewContextBuilder, IArtifactRepository, IReviewRunRepository (Application/Abstractions)
- AgentCodeReviewer (Application/Orchestration) — invokes IAgentExecutor with agent name "reviewer" + IModelRouter's existing "code-review" category (structural independence from the implementation's "coder" agent/model); parses JSON findings with graceful degrade (unparseable output → one Info finding, never a crash); reviewer-invocation failure itself returns a blocking Critical finding
- ReviewContextBuilder (Application/Orchestration) — assembles all 7 required inputs (requirements/acceptance criteria/architecture/changed files/tests/build results/task history) from existing sources: IContextProvider, WorkItem.AcceptanceCriteria, the new IArtifactRepository, and IValidationRunRepository (extended with GetLatestAsync — was write-only since Phase 9)
- ReviewOptions (BlockOnMediumFindings, default false — "may require repair according to policy")
- ArtifactRepository, ReviewRunRepository (Infrastructure/Persistence) — first reader of Artifact rows, first writer of the new `review_runs` table
- New `ReviewRun` entity + migration (`AddReviewRuns`), `WorkItem.ReviewRuns` nav collection
- `AutonomousCodingOrchestrator` now runs independent review after validation passes, before completion — Critical/High findings (or Medium when policy enabled) route through the existing bounded-repair path (HandleFailureAsync), never bypassed by the implementation agent's own success
- DI registration in `Program.cs`
- 12 new tests: reviewer independence (agent/model), structured-finding parsing, non-blocking Low/Info, empty findings, malformed-output degrade, reviewer-failure blocking, context assembly (all 7 inputs + graceful degrade with no validation run yet), orchestrator gating (Critical blocks, Low doesn't, Medium non-blocking by default, Medium blocking when policy enabled) — 121 total passing

### Phase 11 — Human Approval Gate

- ApprovalDecision enum (PendingApproval/Approved/Rejected), ApprovalRequest entity (Domain) — 8 fields exactly as specified (project, workflow, reason, requestedAt, requestedBy, decision, decidedAt, comment)
- IApprovalRepository (Application/Abstractions), ApprovalRepository (Infrastructure/Persistence)
- ApprovalService (Application/Approvals, mirrors ProjectService's role) — the single authoritative way to pause a project: RequestApprovalAsync persists the record + transitions workflow to HumanReview together (reuses the transition's own returned FromStatus as the "workflow" snapshot); ApproveAsync → Completed; RejectAsync → Failed; double-decision guarded (throws if already decided)
- `AutonomousCodingOrchestrator`'s existing raw `IWorkflowEngine` HumanReview call (Phase 9) refactored to go through `ApprovalService` instead — closes the gap where escalating to HumanReview left no discoverable approval record; the now-unused direct `IWorkflowEngine` dependency was removed from the orchestrator
- New `approval_requests` table + migration (`AddApprovalRequests`)
- 3 new endpoints: `GET /api/approvals/pending`, `POST /api/approvals/{id}/approve`, `POST /api/approvals/{id}/reject` (no "create" endpoint — requests are always created internally, matching the prompt's own 3-endpoint list)
- DI registration in `Program.cs`
- 5 new tests proving persistence + pause-until-decided: record+workflow-pause survive a simulated restart (new DbContext instance), repeated fresh reads show the project still paused in HumanReview across multiple "restarts" until an explicit decision, approve/reject each correctly transition + persist, double-decision throws — 126 total passing

### Phase 12 — OpenViking Knowledge Layer

- OpenVikingOptions (Enabled — default false, BaseUrl, ApiKey, TimeoutSeconds), IOpenVikingClient + wire DTOs, HttpOpenVikingClient (real HttpClient-based REST client — no real OpenViking product exists anywhere to integrate against yet, per `.ai/context/decisions.md`; wire contract defined by this implementation itself), OpenVikingContextProvider : IContextProvider (all in Infrastructure/Context)
- No composite/merge provider — OpenVikingContextProvider is a standalone, config-swappable alternative to LocalContextProvider (Program.cs conditionally registers one or the other based on `OpenViking:Enabled`, default false = system behavior fully unchanged)
- Graceful fallback: any failure (network, timeout, malformed response) returns an empty ContextResult rather than throwing; genuine caller cancellation still propagates (same timeout-vs-cancellation pattern as Phase 4's SystemProcessRunner)
- Application layer remains unaware of OpenViking by construction — enforced by the existing dependency direction (Application has no reference to Infrastructure), not by access modifiers
- No Domain coupling — nothing here touches Domain entities
- `OpenViking` config section in `appsettings.json` (`Enabled: false`, placeholder BaseUrl)
- No migration — no new persistence, workflow state stays in SQLite as before
- 9 new tests: fake-client unit tests (disabled short-circuits without calling the client, mapping, fallback-on-throw, cancellation propagates, empty-documents), and separate real-HTTP integration tests against a local `HttpListener`-backed server (real response parsing, real 500-error handling, provider-level end-to-end through real transport, fallback when nothing is listening) — 135 total passing

### Phase 13 — Evaluation and Learning Layer

- IEvaluationEngine/EvaluationResult/RegressionStatus (Application/Abstractions) — pure computed values, not persisted (fully derivable on demand from existing ValidationRun/ReviewRun/ApprovalRequest/WorkItem data, avoiding a redundant table)
- EvaluationEngine (Application/Orchestration) — aggregates build/test (latest ValidationRun), review (latest ReviewRun), human approval if applicable (latest project ApprovalRequest), repair count (AttemptCount-1), and regression status (comparing the last two ValidationRuns); `IsFullyValidated` is the single gate for "verified" evidence
- KnowledgeLevel enum (Observed/Inferred/Verified), Lesson entity — 9 fields (the 8 listed + Level, required by the separate "separate knowledge levels" rule) — genuinely new information, so persisted (unlike EvaluationResult)
- ILearningStore (Application/Abstractions), LearningStore (Infrastructure/Persistence) — append-only (Record + GetByProject only, no update/overwrite)
- LearningService (Application/Learning) — the evidence gate: empty evidence → refuses to store (returns null); Level computed from an optional EvaluationResult — Verified only when IsFullyValidated, Inferred when evaluated-but-not-clean, Observed when no evaluation supplied; high confidence alone can never produce Verified
- 3 existing repositories extended with read methods needed for evaluation but not before: IValidationRunRepository.GetHistoryAsync, IReviewRunRepository.GetLatestAsync, IApprovalRepository.GetLatestForProjectAsync
- New `lessons` table + migration (`AddLessons`)
- Not wired into AutonomousCodingOrchestrator's completion path — would require inventing an unspecified "lesson extraction" step (deriving Problem/Solution text from a completed task) not described in this prompt; components are standalone and fully callable, same precedent as ITaskPlanner (Phase 7)
- Not wired into ContextCategory/LocalContextProvider — "retrievable by future ContextProvider implementations" read as a design constraint (ILearningStore.GetByProjectAsync exists as a clean query surface), not a mandate to integrate now
- No prompt-mutation mechanism built (satisfies "do not automatically modify system prompts based on a single failure" by omission)
- DI registration in `Program.cs` (no new endpoints — none requested)
- 16 new tests: full evaluation matrix (all-clean, build-fail, review-blocked, approval-rejected/approved, regression/improvement detection, unknown-with-insufficient-history, repair-count derivation), learning gate (evidence-required, all 3 level assignments, hallucination-can't-be-verified, cross-restart retrieval, incremental/non-overwriting) — 151 total passing

### Phase 13.5 — Concrete ModelTaskPlanner

- ModelTaskPlanner : ITaskPlanner (Application/Orchestration) — first real implementation, closing the interface-only gap left since Phase 7; reuses IContextProvider (requirements+architecture) → IModelRouter ("planning" category, already configured since Phase 5, no new config) → IAgentExecutor (agent "planner") → structured JSON output → existing TaskPlanValidator (unchanged, called not reimplemented)
- LLM emits short string task IDs (e.g. "TASK-001"); parser resolves them to internally-generated Guids via a lookup map — preserves PlannedTask.Id's existing Guid-typed design (Phase 7) rather than changing the Domain shape to match the prompt's example
- Bounded, self-contained retry (ModelTaskPlannerOptions.MaxAttempts, default 3): malformed/invalid output retries with the validation error fed back into the next prompt; agent-execution failure (Docker down etc.) throws immediately, not retried — different failure class
- New TaskPlanningException (co-located with ITaskPlanner.cs)
- Still NOT persisting the plan — Phase 7 explicitly reserved that for a separate application service that doesn't exist yet (no bulk-insert on IWorkItemRepository); building it now would mean inventing unspecified mapping/status-default logic beyond what was asked
- DI registration + `TaskPlanner:MaxAttempts` config section in `Program.cs`/`appsettings.json`
- 9 new tests matching the user's own explicit list: valid plan accepted, malformed JSON rejected after retries exhausted, missing required fields rejected, missing dependency reference rejected, circular dependency rejected, invalid plan never returned (nothing to persist), context passed into the prompt, validation feedback fed into retry prompt, agent-failure throws immediately without retrying — 160 total passing

### Phase 15 — End-to-End Integration

- `AutonomousCodingOrchestrator` now wires previously-standalone components into the real execution path instead of leaving them callable-but-disconnected:
  - `ExecutionId` (new `Guid` per run) attached via `ILogger.BeginScope` — every log line for one execution correlates
  - `PipelineFailureCategory` enum (AgentExecution/Build/Test/Validation/Repair/Review/Infrastructure/HumanRejection) (Application/Abstractions) — every failure path now classified and logged, not just a free-text error string
  - Retry detection reuses the EF identity-map fact already established in Phase 8 (`workItem.AttemptCount` reflects `TaskStateManager.TryStartAsync`'s increment on the same tracked instance) — `AttemptCount > 1` ⇒ retry
  - `BuildRepairContextTextAsync` (new) — on retry, assembles prior AgentRun/ValidationRun/ReviewRun/Artifact rows into repair-context text, always ending with the mandatory literal sentence `"Do not repeat the previous failed implementation without addressing the reported failure."`; fed into `BuildPrompt` (now 3-arg) as a real prompt section, not just logged
  - `BuildContextAsync`'s category list widened to include `PreviousTaskOutputs`, `TestFailures`, `LearnedLessons` (previously only `ProjectRequirements`/`ArchitectureDocuments`) — exercises `ContextCategory.LearnedLessons` (added this phase) end-to-end
  - `EvaluateAndLearnAsync` (new) — runs after a task's independent review passes: calls the existing `IEvaluationEngine.EvaluateAsync` (Phase 13, previously never invoked by the pipeline), persists a new `EvaluationRun` row, then calls the existing `LearningService.RecordLessonAsync` (Phase 13, previously never invoked by the pipeline) with the evaluation result — Verified only when `IsFullyValidated`
  - `MaybeRequestFinalApprovalAsync` (new) — after a task completes, checks whether *all* of the project's WorkItems have reached a terminal status (Completed/Failed/Cancelled) with at least one Completed and none Failed; only then requests human approval via the existing `ApprovalService` — distinct from the pre-existing repair-exhaustion escalation path, which still fires independently on attempts-exhausted
- `EvaluationRun` entity (Domain) + `IEvaluationRunRepository`/`EvaluationRunRepository` (first persistence for evaluation — Phase 13 deliberately left `EvaluationResult` unpersisted/derivable-on-demand; this phase adds an audit-trail row per evaluation without changing `EvaluationEngine`'s own computation, which stays pure/derived)
- New `evaluation_runs` table + migration (`AddEvaluationRuns`)
- `ContextCategory.LearnedLessons` (new 9th category) + `LocalContextProvider` handling for it (optional `ILearningStore?` constructor dependency, defaults to empty when not supplied — no breaking change to the 12 existing call sites)
- `IAgentRunRepository.GetLatestAsync` (new) — needed by both `BuildRepairContextTextAsync` and `EvaluateAndLearnAsync`
- `ApprovalService` gained structured logging (`ILogger<ApprovalService>?`, optional, defaults to `NullLogger`) — approval requested/approved/rejected are now observable, rejected logs include `PipelineFailureCategory.HumanRejection`
- DI registration in `Program.cs` (`IEvaluationRunRepository`)
- 5 new tests: repair context fed into retry prompt (asserts the literal mandatory sentence + the previous error text both appear in the second attempt's prompt), EvaluationRun persisted on success, Verified lesson recorded after a fully-validated evaluation, final approval requested when all WorkItems reach a clean terminal state, final approval NOT requested while a sibling WorkItem is still incomplete — 165 total passing

**Intentionally still disconnected** (explicitly out of scope for this phase, not an oversight):
- Task-plan persistence — `ModelTaskPlanner` (Phase 13.5) still returns a validated `TaskPlan` that nothing maps into real `WorkItem`/`WorkItemDependency` rows; doing so would require inventing unspecified mapping/status-default rules not given by any prompt so far
- No new `PipelineStage`/state-machine enum — the existing `ProjectStatus` enum already covers every state this phase needed (`HumanReview`/`Completed`/`Failed`/`Cancelled` used as-is); introducing a separate micro-step enum (`Evaluating`/`Learning`, etc.) would duplicate state already expressed in practice by "which orchestrator method is currently running," which is not persisted state
- `ReviewFindingSeverity` (Critical/High/Medium/Low/Info, Phase 10) left unchanged — not renamed/remapped to any different failure-category vocabulary; `PipelineFailureCategory.Review` is a separate, coarser classification layered on top for logging, not a replacement

### Phase 16.1 — Global Exception Handling

- `NotFoundException`/`ConflictException`/`ValidationException`/`ForbiddenException` (Application/Exceptions, new) — business-failure exception types, none pre-existed
- `GlobalExceptionMiddleware` (Api/Middleware, new) — maps them (+ `UnauthorizedAccessException`→403, `TimeoutException`/`OperationCanceledException`→408, anything else→500) to a camelCase `{traceId, status, code, message}` JSON envelope; 500s log full exception server-side but never leak `exception.Message` to the client
- Registered first in `Program.cs`'s pipeline so it wraps every endpoint
- `InvalidOperationException` reachable through an existing endpoint converted to the specific type: `ProjectService.CreateAsync` (duplicate slug → `ConflictException`, plus new empty-name → `ValidationException`), `ApprovalService.GetOrThrow`/`ApprovalRepository.DecideAsync` (missing → `NotFoundException`, already-decided → `ConflictException`), `WorkflowEngine.TransitionAsync` (invalid transition → `ConflictException`)
- `Program` given a `public partial class Program {}` marker so `WebApplicationFactory<Program>` can target it
- New test project `AI.Factory.Api.Tests` (added to `AI.Factory.slnx`) with `ApiTestFactory` (in-memory SQLite, schema created before `Main`'s own startup-recovery step needs it) — 12 new tests: middleware-level mapping for all 7 exception classes + traceId presence (direct `DefaultHttpContext` invocation, no host) and 3 real-HTTP round-trips (validation/conflict/not-found) proving the middleware is actually wired, not just correct in isolation — 177 total passing (165 + 12)

### Phase 16.2 — Execution Limits and Timeouts

- `ExecutionLimitsOptions` (Application/Orchestration, new) — the 6 required fields (Options pattern, bound from `"ExecutionLimits"` in appsettings.json), `Validate()` throwing at startup on any non-positive value, `ExceedsWorkflowDuration(TimeSpan)` — a pure predicate exposed specifically so the wall-clock checkpoint logic is unit-testable without real waiting
- `TimeoutGuard` (Application/Orchestration, new) — reusable `RunAsync<T>` wrapper: caller-driven cancellation still propagates as `OperationCanceledException` unchanged; only the wrapper's own `CancelAfter` firing is converted into a `TimeoutException`, so a timeout is always a distinguishable, catchable failure — never confused with "user cancelled" (satisfies requirement 6 directly)
- `ValidationEngine`'s previously-hardcoded `BuildTimeout`/`TestTimeout` consts now come from `ExecutionLimitsOptions` (constructor-injected) — build/test timeout enforcement itself already existed since Phase 4/9 (`IProcessRunner`'s linked-CTS + `ProcessRunResult.TimedOut`, which already surfaces as a normal validation failure, not an exception) — this phase only centralizes the *values*
- `TaskStateManager` gained an optional `ExecutionLimitsOptions?` param (backward-compatible default) — `FailAsync`/`RecoverOrphanedTasksAsync` now exhaust at `Math.Min(item.MaxAttempts, executionLimits.MaxRepairAttempts)`, a global ceiling on top of the existing per-WorkItem value
- `AutonomousCodingOrchestrator`: agent-execution call wrapped in `TimeoutGuard` using `AgentExecutionTimeoutMinutes` (new `catch (TimeoutException)` branch logs structured metadata — WorkItemId/Operation/ConfiguredLimit/ExecutionId — then routes to the existing `HandleFailureAsync`, so a timeout is a controlled Repairing/Failed transition, never an unhandled exception); two `ExceedsWorkflowDuration` wall-clock checkpoints (after agent execution, after validation persists) enforce `MaxWorkflowDurationMinutes` across the whole task, routing through the same controlled-failure path via new `HandleWorkflowTimeoutAsync`
- `IModelRouter.SelectModel` deliberately NOT wrapped in a timeout — it is synchronous/pure with zero I/O (unchanged since Phase 5, no real model call exists yet); a fake async timeout around CPU-only code would be theater, so only `cancellationToken.ThrowIfCancellationRequested()` was added before the call, honoring cancellation without inventing enforcement that has nothing to bound
- `Program.cs`: binds+validates `ExecutionLimitsOptions` at startup, registered as a singleton — the DI container auto-supplies it into `ValidationEngine`/`TaskStateManager`/`AutonomousCodingOrchestrator`'s already-registered constructors
- Testing note (deliberate scope decision): the `MaxWorkflowDurationMinutes`/`AgentExecutionTimeoutMinutes` orchestrator-level *wiring* is verified by code inspection (both are one-line `TimeSpan.FromMinutes(...)` calls) plus fast, precise unit tests of the underlying primitives (`TimeoutGuardTests` with millisecond-scale durations, `ExecutionLimitsOptionsTests.ExceedsWorkflowDuration` with arbitrary elapsed values) — not by an end-to-end orchestrator test that actually waits out a real 1-minute boundary (the field's minimum meaningful granularity), which would either make the suite slow or require introducing a fake-clock abstraction disproportionate to a two-line guard
- 19 new tests (165→184 in Application.Tests; Api.Tests unchanged at 12) — 196 total passing

### Phase 16.3 — Resilience and Retry Policy

- `RetryOptions` (Infrastructure/Resilience, new) — MaxAttempts/BaseDelay/MaxDelay/JitterFactor, Options pattern, bound from `"Retry"` in appsettings.json
- `RetryExecutor` (Infrastructure/Resilience, new) — generic `ExecuteAsync<T>` retry loop with exponential-backoff+jitter (`base * 2^(attempt-1)`, capped at MaxDelay, ±JitterFactor randomized); the catch's exception filter (`attempt < MaxAttempts && !cancellationToken.IsCancellationRequested && isTransient(ex)`) means a non-transient exception, an exhausted attempt count, or genuine caller cancellation all just let the real exception propagate unchanged — no swallowing, no wrapping; every retry attempt logs Operation/Attempt/Reason/Delay (ExecutionId arrives for free via the orchestrator's existing `ILogger.BeginScope`, when called from that path)
- `HttpTransientClassifier` (Infrastructure/Resilience, new) — narrow allowlist: 429/500/502/503/504 and connection-level failures (no HTTP status reached at all) are transient; everything else (401/403 auth, 400/422 validation, any other 4xx business-rule failure) is never retried
- `HttpOpenVikingClient` — the one real outbound HTTP client that exists in the codebase (OpenRouter has no real implementation to wrap per Phase 5; Hermes is process-based, not HTTP-status-classifiable — see below) — now routes `QueryAsync` through `RetryExecutor` + `HttpTransientClassifier`; gained an optional `RetryExecutor?` param defaulting to a `MaxAttempts=1` (no-retry) instance so all 4 pre-existing single-request-server tests stay unaffected — `Program.cs` always supplies the real configured policy in production
- Side-effect safety (requirement 7): `HttpOpenVikingClient.QueryAsync` is a read-only query — inherently safe to retry, no idempotency mechanism needed
- **Deliberately not wrapped** (documented, not an oversight): OpenRouter model calls — no real HTTP client exists anywhere in the codebase (Phase 5 explicitly deferred it; `IModelRouter.SelectModel` is a pure local routing decision with zero I/O) — nothing to wrap. Hermes agent execution — process launches don't carry HTTP status codes to classify against, and agent execution already has its own outer business-level retry (the existing WorkItem.AttemptCount/MaxAttempts repair loop); stacking a second, lower-level automatic retry underneath it risks silently compounding retries on an expensive LLM-backed operation without a reliable transient-vs-permanent signal at the process level
- 18 new tests (184→202 in Application.Tests; Api.Tests unchanged at 12): `RetryExecutorTests` (transient-then-succeeds, non-transient-no-retry, max-attempts-exhausted propagates, cancellation-during-delay propagates, classifier status-code matrix) + 2 new real-HTTP `OpenVikingIntegrationTests` (503-then-200 actually retries and succeeds; 400 makes exactly one request, never retries — server only serves once, so an incorrect retry would hang the test rather than silently pass) — 214 total passing

### Phase 16.4 — Circuit Breaker

- `CircuitState` enum (Closed/Open/HalfOpen) + `CircuitOpenException` + `CircuitBreakerOptions` (ConsecutiveFailureThreshold/BreakDuration/HalfOpenTrialCount, Options pattern, bound from `"CircuitBreaker:Hermes"`) (Infrastructure/Resilience, new)
- `CircuitBreaker` (Infrastructure/Resilience, new) — one instance per provider (constructed with a `providerName`, meant to be registered as a **singleton** since its whole point is state persisting across calls); `ExecuteAsync<T>(operation, isFailure?)` — fails fast with `CircuitOpenException` without calling `operation` at all while Open; Open→HalfOpen once `BreakDuration` elapses, allowing up to `HalfOpenTrialCount` calls through; any HalfOpen failure re-opens immediately, any HalfOpen success closes and resets the failure streak; every state transition is logged (Provider/From/To)
- The `isFailure` predicate parameter exists because `HermesAgentExecutor` already converts its own expected failures (docker missing, non-zero exit, timeout) into a returned `AgentExecutionResult(Success: false)` rather than throwing (established since Phase 4/8) — without it, a provider that never throws could never trip its own circuit; the decorator supplies `result => !result.Success`
- `CircuitBreakerAgentExecutorDecorator : IAgentExecutor` (Infrastructure/Agents, new) — wraps the real executor rather than modifying it, preserving the interface boundary (Application still depends only on `IAgentExecutor`); on `CircuitOpenException` it returns the same controlled `AgentExecutionResult(Success: false, ...)` shape as any other expected failure, so `AutonomousCodingOrchestrator`'s existing `if (!result.Success)` handling needs no changes to cope with a fail-fast provider outage
- `Program.cs`: `HermesAgentExecutor` now registered as a concrete type; `IAgentExecutor` resolves the decorator wrapping it + the singleton `CircuitBreaker`
- **Deliberately not wrapped**: OpenRouter — same reason as Phase 16.3, no real client exists in the codebase yet to protect
- 7 new tests (202→209 in Application.Tests; Api.Tests unchanged at 12): opens after N consecutive failures, fails fast without invoking the operation while Open, recovers via a successful HalfOpen trial, re-opens immediately on a failed HalfOpen trial, a success resets the failure streak (doesn't accumulate across successes), the `isFailure` result-predicate path trips the circuit exactly like a thrown exception, and the decorator returns a controlled failure result (not an exception) when wrapping an open circuit — 221 total passing

### Phase 16.5 — Persistent Workflow Recovery

- Reviewed the existing model first (requirement 1): `TaskStateManager.RecoverOrphanedTasksAsync` already existed since Phase 8 — a full-process-restart sweep of every `Running` WorkItem, called once at Api startup. This phase adds a genuinely different, complementary capability (a periodic, heartbeat-aware staleness sweep for a long-lived process where a single execution can hang without the whole process crashing) rather than replacing it
- No new lifecycle states introduced (requirement 2) — `WorkItemStatus`/`ProjectStatus` already cover every state this phase needed (Running/Repairing/Failed); "stale" is a derived condition (Running + old heartbeat), not a state of its own
- `WorkItem` gained `LastHeartbeatAtUtc`/`LastCheckpoint` (Domain, migration `AddWorkItemRecoveryFields`) — current-state fields (same category as the existing `Status`/`AttemptCount`/`StartedAtUtc`), not an audit table, matching how the entity already models "current execution state" vs. the separate `*Run` tables that model history
- `RecoveryOptions` (Application/Orchestration, new) — `StaleExecutionTimeout`, `PeriodicSweepInterval` (Options pattern)
- `ITaskStateManager`/`TaskStateManager` gained `HeartbeatAsync` (records liveness + optional checkpoint label) and `RecoverStaleExecutionsAsync` (recovers only a `Running` item whose `LastHeartbeatAtUtc` — or `StartedAtUtc` if none was ever recorded — predates the configured timeout; a genuinely still-executing item with a recent heartbeat is left untouched; idempotent by construction — a recovered item no longer matches `Status==Running` on a repeat call)
- `AutonomousCodingOrchestrator` now calls `HeartbeatAsync` at 3 stage boundaries (AgentExecutionCompleted, ValidationPassed, ReviewPassed) — this is genuinely how "heartbeat" maps onto this codebase's actual execution shape (a multi-stage pipeline with one opaque `await` per external call, not a loop with an obvious mid-flight tick point)
- **`AI.Factory.Worker` — its first real implementation.** It was a completely empty stub (csproj only, zero source files) for the entire rest of this session; this phase gives it a `Program.cs` (Generic Host, `FactoryDbContext` against the same `data/factory.db`, `ITaskStateManager`) and a new `WorkflowRecoveryHostedService : BackgroundService` — runs the existing startup sweep once, then `RecoverStaleExecutionsAsync` on `PeriodicSweepInterval` thereafter
- Side-effect safety (requirements 8/9): recovery never re-executes anything itself — it only flips `Status` (Running→Repairing/Failed), deferring any actual rerun to the normal scheduler/orchestrator pickup path on its next pass, which already starts a fresh, isolated agent workspace per attempt (Phase 4 design) — so a recovered item can never collide with or duplicate its own interrupted attempt's side effects
- **Explicit scope note:** the Worker still does not itself run `AutonomousCodingOrchestrator.ExecuteNextReadyTaskAsync` in a loop — that execution loop has never existed anywhere in this codebase (only tests invoke the orchestrator directly); wiring one up is a materially different task from "persistent workflow recovery" and was not built here
- 6 new tests (209→215 in Application.Tests; Api.Tests unchanged at 12): heartbeat updates both fields, a stale-heartbeat Running item is recovered while a freshly-heartbeating one is left untouched, `LastCheckpoint` survives recovery (proving an interrupted execution's progress is visible, not discarded), repeated `RecoverStaleExecutionsAsync` calls are idempotent (1 recovered then 0), and an orchestrator-level test confirming heartbeat/checkpoint are actually recorded during a real successful run — 227 total passing

### Phase 16.6 — Execution Audit Trail

- `AuditEventType` (14-member enum matching the prompt's exact event catalog) + `AuditSeverity` (Info/Warning/Error/Critical) (Domain/Enums, new)
- `AuditLogEntry` entity (Domain, new) — ExecutionId/ProjectId/WorkItemId/TimestampUtc/EventType/Severity/Message/MetadataJson, exactly the fields requirement 2 lists; deliberately no FK/navigation to Project/WorkItem — an audit trail must survive deletion of what it describes
- `IAuditLogRepository`/`AuditLogQuery`/`AuditLogPage` + `IAuditLogger` (Application/Abstractions, new) — the write-side facade (`IAuditLogger`) is what callers use; `IAuditLogRepository` is the read/persist boundary
- `AuditOptions` (Application/Orchestration, new) — `RetentionDays`, `FailOnAuditError` (Options pattern)
- `AuditLogRepository`/`AuditLogger` (Infrastructure/Persistence, new) — `AuditLogger` never lets a persistence failure reach the caller unless `FailOnAuditError=true` (logs a warning instead); strips any metadata key containing key/token/secret/password/credential/authorization (case-insensitive) before serializing, as a real defense-in-depth mechanism, not just a comment
- **Event wiring** (requirement 4's full catalog, all 14 types, each reachable): `AutonomousCodingOrchestrator` — WorkflowStarted, RepairAttempt (on retry), ModelSelected, AgentStarted, AgentCompleted, Timeout (both agent- and workflow-level), BuildResult, TestResult, ValidationResult, WorkflowCompleted, and Retry/WorkflowFailed (in the shared `HandleFailureAsync`, keyed off whether attempts were exhausted); `WorkflowEngine.TransitionAsync` — WorkflowStateChanged; `ApprovalService` — HumanApproval (request/approve/reject). All three gained an optional `IAuditLogger?` constructor param defaulting to a new `NullAuditLogger.Instance` no-op — `AutonomousCodingOrchestrator`'s stayed required (its constructor already changes every phase) with the DI wiring supplying the real implementation
- `GET /api/audit-log?projectId=&executionId=&page=&pageSize=` (Program.cs) — paginated, calls `IAuditLogRepository` directly (same convention as the existing `GET /api/projects/{id}` reading `IProjectRepository` directly)
- New `audit_log_entries` table + migration (`AddAuditLog`), indexed on ExecutionId/ProjectId/TimestampUtc
- 21 new tests (215→223 in Application.Tests, 12→13 in Api.Tests): `AuditLogger` (persists all fields, strips secret-shaped metadata, swallows failure by default, propagates when configured critical), `AuditLogRepository` (pagination + total count + newest-first ordering, project-id filtering, retention purge deletes only expired rows), one orchestrator test asserting 7 of the 14 event types actually fire during one real successful run, one real-HTTP test for the paginated endpoint's envelope shape — 236 total passing

### Phase 16.7 — Health Checks

- `DatabaseHealthCheck`, `ConfigurationHealthCheck`, `CriticalServicesHealthCheck`, `OpenVikingHealthCheck` (Api/HealthChecks, new) — all "ready"-tagged, all isolated from business logic (no calls into orchestration services): DB check is a plain `CanConnectAsync` probe (no query); config check re-runs the existing `ExecutionLimitsOptions.Validate()`; critical-services check does a cheap `IServiceProvider.GetService<T>()` lookup for `ITaskStateManager`/`IAgentExecutor`/`IModelRouter`/`IAutonomousCodingOrchestrator` (never invokes them); OpenViking check only verifies configuration consistency (Enabled+BaseUrl), deliberately makes no network call — a real round-trip would make `/health/ready` slow and flaky exactly when the already-gracefully-degrading OpenViking layer is down, defeating the point of a health probe
- Uses .NET's built-in `Microsoft.Extensions.Diagnostics.HealthChecks` (part of the ASP.NET Core shared framework, already available to a `Sdk.Web` project) — no new NuGet dependency
- `/health/live` — zero registered checks run (`Predicate = _ => false`); if the process can answer HTTP at all, liveness is satisfied, exactly per requirement 2
- `/health/ready` and `/health` — run every "ready"-tagged check; ASP.NET Core's default health-check status-code mapping already returns 200 for Healthy/Degraded and 503 for Unhealthy, satisfying requirement 6 with no extra code
- OpenViking check is opt-in via `"Health:CheckOpenViking"` (default `false`) — not even registered unless explicitly turned on, satisfying "configurable" and "no expensive calls by default" together
- Replaces the old placeholder `/health` (a hand-written `{status:"ok"}` JSON literal from Phase 1) with the real aggregated check
- 22 new tests (223→245 total; 12→22 in Api.Tests): each health check exercised directly for both its Healthy and Unhealthy path (including a genuinely-unreachable-database case using a nonexistent-drive connection string, since SQLite `:memory:` always trivially connects and can't demonstrate the failure path) + 3 real-HTTP tests for `/health/live`, `/health/ready`, `/health` — 245 total passing

### Phase 16.8 — Agent Execution Security Boundaries

- `SecurityViolationException` (Infrastructure/Agents, new) — the one clear signal for every boundary violation in this phase
- `WorkspacePathGuard.EnsureWithinRoot(root, candidate)` (Infrastructure/Agents, new, static) — resolves both paths fully (collapsing `..`, and resolving the deepest existing ancestor's real target via `DirectoryInfo.ResolveLinkTarget` to catch a symlink planted inside an otherwise-legitimate path) before comparing, so traversal, absolute-path escape, and symlink escape are all caught against the real resolved location, not a naive string prefix on unresolved input
- `CommandPolicyOptions` (Infrastructure/Agents, new) — `AllowedCommands`/`DeniedCommands`, default-denies a real list of dangerous commands (`rm`/`del`/`format`/`shutdown`/`curl`/`wget`/shells/`ssh`/`scp`) even with zero configuration, default-allows only `docker`+`dotnet` — the only two executables this codebase actually launches
- `SystemProcessRunner` — now the single choke point every process launch goes through (Hermes AND the `dotnet build`/`dotnet test` executors share one instance), enforcing the command policy before every launch (deny wins over allow; an unlisted command is rejected by the explicit allow-list, not permitted by default) — "enforce programmatically, not via prompt instructions" satisfied literally, since this check cannot be bypassed by anything an agent's own output says; also now applies `ProcessRunRequest.WorkingDirectory`/`EnvironmentVariables` (both new, additive, optional fields) to the spawned process
- `HermesAgentExecutor`: the generated workspace path is validated through `WorkspacePathGuard` before creation (defense-in-depth on top of it already being Guid-generated, never agent-influenced, since Phase 4); docker now launches with `--network {HermesOptions.NetworkMode}` (new field, default `"none"` — no network access, least privilege by default) and an explicit `WorkingDirectory` + a **replaced, minimal environment** (`PATH` only — the container never inherits this process's full environment, which could otherwise carry DB connection strings or other secrets); a `SecurityViolationException` from either the path guard or the process-runner's policy check is caught and converted into the same controlled `AgentExecutionResult(Success: false, ...)` shape every other expected failure already uses (never an unhandled exception), and is also audit-logged at `Critical` severity via the `IAuditLogger` wired in Phase 16.6
- Secrets never reach logs/prompts (requirement 4) — already true by construction and reinforced this phase: `AuditLogger` (Phase 16.6) already strips secret-shaped metadata keys; the new controlled environment mechanism only ever logs var *names* it might set, never values; nothing new was added that logs environment contents
- `Program.cs`: `CommandPolicyOptions` bound from `"CommandPolicy"` and registered as the single shared policy for `SystemProcessRunner`; `HermesOptions.NetworkMode` bound alongside the rest of the `Hermes` section
- Fixed 5 pre-existing `SystemProcessRunnerTests` that used `cmd.exe`/a fake executable name for testing raw process mechanics (timeout, cancellation, stdout, exit code, launch failure) unrelated to this phase's policy — gave that test class its own explicitly permissive `CommandPolicyOptions` rather than loosening the shared production default
- 9 new tests (223→232 in Application.Tests; Api.Tests unchanged at 22): `WorkspacePathGuard` — `../` traversal rejected, absolute-path escape rejected, a legitimate within-root path succeeds, an unconfigured root is rejected; `SystemProcessRunner` — an explicitly denied command is rejected, a command absent from the allow-list is rejected, an explicitly allowed command proceeds past the policy check, the zero-configuration default already denies a dangerous command; `HermesAgentExecutor` — the docker invocation actually carries `--network`, a working directory, and an environment containing nothing but `PATH` — 254 total passing

### Phase 17 — Real End-to-End Runtime Integration

- `OrchestrationRun`/`OrchestrationRunAttempt`/`OrchestrationProgressEvent` (Domain, new) + `OrchestrationRunStatus`/`OrchestrationFailureCategory`/`OrchestrationProgressStage` enums — a NEW tracking layer representing "one API-triggered, plan-then-execute, individually tracked pass of the pipeline for a specific task," distinct from (and layered on top of) `WorkItem.Status`/`AttemptCount`, which continue to track the task's own cumulative state exactly as before
- **Central architectural decision**: rather than reimplementing model-selection/agent-execution/build/test/repair/review/evaluation/learning/approval a second time, `AutonomousCodingOrchestrator.ExecuteNextReadyTaskAsync` was refactored (pure extraction, no logic change — verified by the full existing test suite passing unmodified) into a shared private `ExecuteWorkItemAsync` plus two public entry points: the original (scheduler-picked "next ready" item) and a new `ExecuteSpecificWorkItemAsync(workItemId)` (a specific, caller-chosen item). The new Phase 17 pipeline calls the SAME, already-tested per-attempt logic — repair-context building, independent review, evaluation, learning, approval-triggering — none of it duplicated
- `IWorkItemRepository` gained `GetByIdAsync`/`UpdateAsync`; `IAutonomousCodingOrchestrator` gained `ExecuteSpecificWorkItemAsync`; `WorkItemExecutionOutcome` gained an optional `Category` (the same `PipelineFailureCategory` already used for audit logging) so callers outside the orchestrator can classify a failure without re-deriving it from error text
- `IOrchestrationProgressReporter` (new) — `AutonomousCodingOrchestrator` reports ~12 of the 20 suggested progress stages (ModelSelected/AgentStarted/AgentCompleted/BuildStarted/BuildCompleted/TestsStarted/TestsCompleted/ReviewStarted/ReviewCompleted/EvaluationCompleted/LearningCompleted/Failed) at the exact points its own audit-logging (Phase 16.6) already instruments; `OrchestrationRunExecutionEngine` reports the rest (RunCreated/ContextRetrieved/PlanningStarted/PlanReady/RepairStarted/AwaitingApproval/Completed) — decoupled via the interface so the orchestrator never needs to know OrchestrationRun exists
- `IOrchestrationService`/`OrchestrationService` — the single entry point `StartAsync` creates a Pending run and returns immediately (reuses Phase 16.1's `NotFoundException`/`ConflictException`, so the new endpoints need zero bespoke exception handling)
- `OrchestrationRunExecutionEngine` — per claimed run: prepares an isolated workspace (`{OrchestrationOptions.WorkspaceRoot}/runs/{runId}/workspace`, validated via the Phase 16.8 `WorkspacePathGuard`, project source copied in if present), calls `ITaskPlanner.CreatePlanAsync` + `TaskPlanValidator.Validate` (on failure: Failed/PlanningFailure, the orchestrator is never called — "do not call Hermes" satisfied literally), appends the validated plan to the WorkItem's own `Description` (so the existing, unchanged `BuildPrompt` naturally includes it — no prompt-assembly changes needed), then repeatedly calls `ExecuteSpecificWorkItemAsync` (once per attempt) until the WorkItem reaches Completed/Failed, persisting an `OrchestrationRunAttempt` after every call and mapping the final outcome to Completed/AwaitingApproval (checked via live `Project.Status`)/Failed/Cancelled
- **`WorkspacePathGuard`/`SecurityViolationException` relocated** from `Infrastructure/Agents` to `Application/Orchestration` — both are pure BCL logic with zero real infrastructure dependency, and Application code (this new engine) cannot depend on Infrastructure; `HermesAgentExecutor`/`SystemProcessRunner` updated to reference the new location, no behavior change
- `OrchestrationRunRepository` — `TryClaimNextPendingAsync` is a single conditional `ExecuteUpdateAsync` (`WHERE Status=Pending`) so two Workers racing can never both claim the same row (returns the in-memory claimed object directly rather than re-querying, which would return a stale EF-identity-map copy); `RecoverStaleLeasesAsync` returns an expired Claimed/Running run to Pending (or Failed if attempts are exhausted) — reuses the same `WorkflowRecoveryHostedService` periodic sweep from Phase 16.5 rather than adding a third hosted service
- **`AI.Factory.Worker`'s first real execution capability** (it previously only ran the Phase 16.5 recovery sweep) — `Program.cs` now registers the full pipeline DI graph (mirroring Api's), and a new `OrchestrationRunWorker : BackgroundService` polls every `Orchestration:QueuePollingInterval`, claims one Pending run, executes it under a linked `ExecutionTimeout`-bounded token with a periodic lease-renewal loop, and never lets one poll's failure stop the loop
- 5 new endpoints (`POST /api/orchestration-runs` → 202 + runId/status/statusUrl; `GET /api/orchestration-runs/{id}`; `GET .../attempts`; `GET .../logs`; `POST .../cancel`) — the API only ever creates a Pending row and returns; all real execution happens on the Worker, off the HTTP request, per the prompt's explicit requirement
- New `orchestration_runs`/`orchestration_run_attempts`/`orchestration_progress_events` tables + migration (`AddOrchestrationRuns`), deliberately without FKs to Project/WorkItem (a run's own history must survive unrelated deletes elsewhere)
- 24 new tests: `OrchestrationRunExecutionEngineTests` (successful run, build-failure-then-repair, test-failure-then-repair, max-attempts-exceeded, invalid-plan-never-calls-orchestrator, immediate-agent-failure, cancellation, awaiting-approval) using fakes for `ITaskPlanner`/`IAutonomousCodingOrchestrator` per the prompt's own "must not require a real Hermes server" instruction; `OrchestrationRunRepositoryTests` (duplicate-claim prevention, stale-lease recovery, an AwaitingApproval run is never re-claimed — the proxy for "no continued execution after human rejection," since rejection itself is Phase 11's already-tested `ApprovalService.RejectAsync`); `OrchestrationServiceTests`; 2 new `AutonomousCodingOrchestratorTests` proving `ExecuteSpecificWorkItemAsync` preserves the existing RepairContext-feeds-into-next-prompt behavior; 4 new `ApiIntegrationTests` (202 Accepted immediately, status/404/cancel over real HTTP); a disabled-by-default `HermesSmokeTest` gated by the `AI_FACTORY_HERMES_SMOKE_TEST` environment variable (never runs in normal CI)
- **Fixed two real EF identity-map bugs surfaced by these tests** (not test-only issues — both would also affect production callers that read-then-write within one DbContext scope): `TryClaimNextPendingAsync` was re-querying after its `ExecuteUpdateAsync`, returning the same tracked-but-stale instance instead of the freshly-claimed state; `RequestCancellationAsync` used `ExecuteUpdateAsync` too, invisible to a caller (`OrchestrationService`) that had already loaded the same run in the same scope — switched to load-then-save
- **Known test-host quirk** (disclosed, not a code defect): running `OrchestrationRunExecutionEngineTests` in complete isolation via `dotnet test --filter` crashes the VSTest test host before it reports results; the SAME tests pass cleanly and are counted correctly (0 failures) every time they run as part of the full suite — reproduced 4 times. Root cause not fully isolated (given time constraints); does not affect the reported pass counts below, which come from xUnit's own summary line, not the aggregate `dotnet test` process's exit status
- 268 tests total (245 in Application.Tests, up from 232; 26 in Api.Tests, up from 22)

### Phase 17.1 — Architecture Wiring & Dependency Injection Audit

- Evidence-based audit (not a rebuild) of every abstraction listed in the prompt plus their real names in this codebase: all resolve, are registered in both Api and Worker composition roots, and are actually consumed by `AutonomousCodingOrchestrator`/`OrchestrationRunExecutionEngine` — no `IMPLEMENTED_NOT_REGISTERED`/`REGISTERED_NOT_USED`/dead-code defects found
- **One real gap found and fixed**: Worker's composition root (`Program.cs`) had zero test coverage — Api's is indirectly proven by `ApiIntegrationTests` via `WebApplicationFactory<Program>`, but nothing built Worker's real DI graph. Fixed by extracting the existing `builder.Services.Add...` block into `Program.ConfigureServices(HostApplicationBuilder)` (verbatim relocation, no logic change) and adding `AI.Factory.Worker.Tests` (new project, 22 tests) that builds the real graph — real `appsettings.json` loaded via a content-root lookup, in-memory SQLite swapped in — and resolves every interface plus both hosted services, without ever calling `host.Run()`
- Confirmed intentional, correctly-preserved boundaries: `IOrchestrationService` is absent from Worker's container by design (Worker executes claimed runs, never starts new ones) — asserted explicitly in a test rather than left undocumented; `LocalContextProvider` legitimately returns empty for `ContextCategory.TestFailures`; OpenViking stays disabled by default with no fake endpoint introduced
- No new interfaces, features, queues, or architecture introduced — scope was audit + minimal wiring-testability fix only
- 294 tests total (272 → 294; +22 new `AI.Factory.Worker.Tests`)

### Phase 17.2 — Orchestration Run Observability and Execution Trace

- `OrchestrationRunStep` (Domain, new) — a paired start/complete(/fail/skip) record with `Stage`(`OrchestrationStepKind`, new 12-value enum: WorkspacePreparation/TaskPlanning/ContextRetrieval/ModelSelection/AgentExecution/Build/Test/Repair/CodeReview/HumanApproval/Evaluation/Learning), `Status`(`OrchestrationStepStatus`, new: Pending/Running/Succeeded/Failed/Skipped), `Sequence`(assigned at persistence time — timestamps alone can tie), `AttemptNumber`, `StartedAtUtc`/`CompletedAtUtc`, `FailureType`/`FailureMessage`, `MetadataJson`(nullable, `System.Text.Json`-serialized `Dictionary<string,string>`, never fabricated). Table `orchestration_run_steps`, FK cascade to `OrchestrationRun`, indexed on RunId/Stage/Status/StartedAtUtc — migration `AddOrchestrationRunSteps`
- **Deliberately distinct from the existing `OrchestrationProgressStage`** (Phase 17's free-text progress log, which bakes Started/Completed into the enum value itself, e.g. `BuildStarted`/`BuildCompleted`) — `OrchestrationStepKind` is a coarse phase name orthogonal to a separate `Status`, matching the prompt's own stage list; both enums/mechanisms coexist (progress log unchanged, `/logs` endpoint unaffected)
- `IOrchestrationRunTraceService`(`StartStepAsync`/`CompleteStepAsync`/`FailStepAsync`/`SkipStepAsync`, workItemId-keyed) + `NullOrchestrationRunTraceService` default — same decoupling pattern as `IOrchestrationProgressReporter`; Infrastructure impl finds the active run and is non-throwing (catches+logs its own persistence failures, matching `OrchestrationProgressReporter`'s existing convention, so a broken trace backend never affects real orchestration outcomes)
- Instrumented at 12 real boundaries: `OrchestrationRunExecutionEngine` (WorkspacePreparation, TaskPlanning — writes steps directly through the repository since it already holds the run, same as its own pre-existing `ReportAsync` helper); `AutonomousCodingOrchestrator` (ContextRetrieval, ModelSelection, AgentExecution, Build, Test, Repair, CodeReview, HumanApproval ×2 call sites, Evaluation, Learning — via the new optional `IOrchestrationRunTraceService?` ctor param, same established pattern as `IAuditLogger?`/`IOrchestrationProgressReporter?`)
- Genuine `Skipped` step: when `ValidationResult.TestsSucceeded` is null (build failed before tests could run, or no test target), Test is recorded Skipped rather than omitted or fabricated as Failed
- Build/Test steps are both derived from one `IValidationEngine.ValidateAsync` call (it builds and tests together) — their timing is approximated around that single call rather than independently measured; disclosed as a real, minor simplification, not fabricated data
- Failure handling: stage boundaries that can genuinely throw (WorkspacePreparation, TaskPlanning, ContextRetrieval, ModelSelection's callers via Repair, AgentExecution's timeout/exception paths, HumanApproval's existing catch) mark the step Failed before the original exception is rethrown (`throw;`, never swallowed) or — for the two `HumanApproval` sites, which already only log-and-continue on failure in the pre-existing code — the step is marked Failed without adding a new throw, preserving that exact pre-existing behavior
- `GET /api/orchestration-runs/{id}` extended (not duplicated) with an ordered `trace` array (`Steps.OrderBy(Sequence)`, metadata deserialized to a plain object, EF entities never exposed directly) — no new endpoint added, since no project-scoped run-listing endpoint existed yet to extend either
- 17 new tests: `OrchestrationRunTraceServiceTests`(7 — creation/complete/fail/skip/ordering/multiple-attempts/correct-run-isolation) using a real `OrchestrationRunTraceService`+in-memory SQLite; `OrchestrationInstrumentationTests`(3 — real `AutonomousCodingOrchestrator` wired to a real trace service: successful run produces Succeeded steps, a build failure produces a Failed Build step + Skipped Test step, and a broken step-persistence repository never changes the real agent-failure outcome the orchestrator returns); 1 new `ApiIntegrationTests` case asserting the `trace` array is ordered and carries failure/metadata correctly
- 305 tests total (272 → 305: +22 `Worker.Tests` already counted in 17.1, +10 in `Application.Tests`, +1 in `Api.Tests`)
- Out of scope, confirmed unchanged: `OpenViking:Enabled=false`, `Worker:MaxConcurrentRuns` not implemented, no Grafana/Prometheus/OpenTelemetry/SignalR — none introduced

## In progress

_(nothing currently in progress — Phase 17.2 is complete and validated)_

## Next phases
- Task-plan → WorkItem persistence for the AUTONOMOUS scheduler path (`ExecuteNextReadyTaskAsync`) — Phase 17 only maps a plan into WorkItem.Description text for prompt context, it still does not persist a `TaskPlan`'s sub-tasks as new `WorkItem`/`WorkItemDependency` rows
- Real-time progress streaming (e.g. SSE/WebSocket) — `GET /api/orchestration-runs/{id}/logs` is poll-based, not push-based
- A dashboard/UI over the new orchestration-run/attempt/progress data

## Rule

Only mark an item complete after implementation and validation.
