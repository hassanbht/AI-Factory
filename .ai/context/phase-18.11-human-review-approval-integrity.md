# Phase 18.11 — Human Review, Approval Integrity & Project Finalization

## 1. Executive Summary

Phase 18.10 built the deterministic path `Implementation -> Testing -> Reviewing
-> HumanReview` and made sure the automatic `ProjectCompletionGate` could never
itself produce `ProjectStatus.Completed`. It left one integrity gap open,
identified explicitly in that phase's own report: the domain transition table
(`ProjectTransitions`) still allowed `Reviewing -> Completed` as a *structurally
valid* move, reachable through the generic, manual
`PATCH /api/projects/{id}/status` endpoint — bypassing `HumanReview` entirely.

Phase 18.11 closes that gap:

- `ProjectTransitions.Allowed[Reviewing]` no longer contains `Completed` —
  `HumanReview -> Completed` is now the domain's only completion edge.
- `ProjectService.TransitionStatusAsync` (the generic endpoint's backing
  method) now rejects `next == ProjectStatus.Completed` unconditionally,
  regardless of current status — completion can no longer be expressed as an
  arbitrary status assignment through the generic mechanism at all.
- A new, small, explicit `ProjectService.ApproveAsync(projectId, actor,
  cancellationToken)` is the sole way to perform `HumanReview -> Completed` for
  the new Project pipeline, exposed via a dedicated
  `POST /api/projects/{id}/approve` endpoint.
- The operation is idempotent, concurrency-safe (existing `Version` token),
  audited (existing `IAuditLogger`), and never touches WorkItems, AgentRuns,
  the scheduler, or verification.
- The pre-existing legacy `ApprovalService`/`IWorkflowEngine` pipeline was
  analyzed empirically (not rewritten) and found to already respect the
  "HumanReview required before Completed" invariant this phase protects,
  even though it uses its own, separate, more permissive transition validator
  to *enter* HumanReview. This is documented as a known, pre-existing,
  orthogonal risk rather than silently left unexamined.

## 2. Repository Findings

Read before making any change: `AGENTS.md`; the Phase 18.10 report; `Project`,
`ProjectStatus`, `ProjectTransitions`; `ProjectService`; the Project status API
endpoints in `AI.Factory.Api/Program.cs`; `ProjectsAndTasksApiTests`;
`ApprovalRequest`, `ApprovalService`; `IWorkflowEngine`, `WorkflowEngine`,
`WorkflowStateStore`, `WorkflowTransitionValidator`; legacy
`AutonomousCodingOrchestrator`; `IAuditLogger`/`NullAuditLogger`/`AuditLogger`;
all existing Project lifecycle tests (`ProjectTransitionsTests`,
`ProjectServiceTests`, `ProjectsAndTasksApiTests`, `ApprovalServiceTests`,
`WorkflowEngineTests`).

Key pre-existing abstractions discovered and reused:

- **Actor identity**: no authenticated-user/role abstraction exists anywhere
  in the repository (no `ClaimsPrincipal`/auth middleware for end-user
  identity — only an internal *operational-endpoint* security middleware
  unrelated to human identity). The existing convention for "who did this" is
  a plain, caller-supplied `string` label (`WorkflowTransitionRequest.Actor`,
  `ApprovalService.RequestApprovalAsync`'s `requestedBy` parameter,
  `ApproveAsync`'s `comment` treated as `"human"`). This phase reuses exactly
  that convention rather than inventing an authentication system.
- **Audit**: `IAuditLogger`/`NullAuditLogger`/`AuditEventType.HumanApproval`
  already exist and are already used by the legacy `ApprovalService` for
  approve/reject events. Reused as-is — no new event type, no new audit
  framework.
- **Approval infrastructure**: `ApprovalRequest`/`ApprovalService`/
  `IWorkflowEngine` already exist, but are tied to a *different* Project
  state-transition mechanism (`WorkflowTransitionValidator`, via
  `WorkflowStateStore` writing `project.Status` directly) than the one the
  new Phase 18.3+ pipeline uses (`ProjectTransitions`/`Project.TransitionTo`
  via `ProjectService`). See §6 for the full analysis of why this phase does
  **not** adapt that existing framework for the new pipeline's approval step.
- **Optimistic concurrency**: `Project.Version` (`IVersionedEntity`) +
  `FactoryDbContext.SaveChangesAsync` -> `ConcurrencyConflictException` is
  already the Factory-wide race-safety mechanism (used identically by
  `ProjectCompletionGate`, `TaskDispatchScheduler`, `AgentRunWorkItemSync`).
  Reused unchanged.

## 3. Exact Pre-Existing Bypass

Before this phase, `ProjectTransitions.Allowed[ProjectStatus.Reviewing]`
included both `HumanReview` **and** `Completed`:

```csharp
[ProjectStatus.Reviewing] = [ProjectStatus.HumanReview, ProjectStatus.Completed, ...]
```

Since `ProjectService.TransitionStatusAsync` (backing
`PATCH /api/projects/{id}/status`) applies no restriction beyond
`ProjectTransitions.IsValid`/`Project.TransitionTo`, any caller could move a
Project directly `Reviewing -> Completed` via one PATCH request, with zero
human involvement and without ever passing through `HumanReview` —
contradicting the entire purpose of Phase 18.10's `Reviewing -> HumanReview`
gate. No existing test exercised or asserted this specific bypass (confirmed
by grep across `ProjectsAndTasksApiTests`/`ProjectServiceTests` for
`Reviewing`/`HumanReview`/`Completed` before this phase), so removing it
introduced no unexpected behavioral dependency.

## 4. Final Lifecycle Semantics

```text
Implementation -> Testing -> Reviewing -> HumanReview -> Completed
```

- `ProjectTransitions.Allowed[Reviewing]` = `{HumanReview, Paused, Failed,
  Cancelled}` — `Completed` removed.
- `ProjectTransitions.Allowed[HumanReview]` = `{Completed, Implementation,
  Paused, Failed, Cancelled}` — **unchanged**; this remains the only
  structurally valid route to `Completed`.
- `ProjectService.TransitionStatusAsync` additionally rejects
  `next == ProjectStatus.Completed` **unconditionally** (not just from
  `Reviewing` — from `HumanReview` too), so the generic/manual endpoint can
  never produce `Completed` under any input. This is intentionally stricter
  than the minimum the spec's §2/§3 asked for (which named `Reviewing ->
  Completed` specifically) — per §5's stronger phrasing ("`HumanReview ->
  Completed` is represented as an explicit human action rather than an
  arbitrary status assignment"), the *only* way to reach `Completed` for the
  new pipeline is now the dedicated approval action, full stop.
- `ProjectService.ApproveAsync` is the sole method that can still perform
  `HumanReview -> Completed`, and it does so through the exact same
  `Project.TransitionTo`/`ProjectTransitions.IsValid` domain guard as every
  other transition — no parallel/duplicate lifecycle rule was introduced.

## 5. Approval Service / API Design

`ProjectService.ApproveAsync(Guid id, string? actor = null, CancellationToken
cancellationToken = default)`:

1. Loads the Project (404 if missing).
2. If already `Completed` — returns it as-is (idempotent success, no
   re-transition, no error).
3. If not `HumanReview` — throws `ConflictException` naming the actual
   current status (maps to HTTP 409).
4. Otherwise: `project.TransitionTo(ProjectStatus.Completed)` +
   `SaveChangesAsync` (existing `Version`-guarded persistence).
5. On `ConcurrencyConflictException` (lost race): reloads the Project; if it
   is now `Completed` (the other concurrent caller won), returns that as a
   safe idempotent success instead of surfacing a spurious error for a
   request that, in effect, already succeeded; any other post-reload status
   rethrows the conflict.
6. Logs one `AuditEventType.HumanApproval` entry via the existing
   `IAuditLogger` (`actorType: "Human"`, `actorId: actor`, `outcome:
   "Approved"`, `projectId`) — answers "which Project, when, from what state,
   by what actor label" per §7.
7. Takes no `AgentRun`/`StructuredResult`/LLM-shaped input whatsoever — there
   is no code path by which an agent's output could satisfy this method's
   authorization check.

API: `POST /api/projects/{id}/approve`, optional body `{ "actor": "..." }`
(`ApproveProjectRequest`), returns the same `ToProjectDto` projection as every
other Project endpoint. Placed immediately after the existing
`PATCH /api/projects/{id}/status` handler in `Program.cs` — no new
controller/module, matching existing minimal-API conventions.

## 6. Authorization Limitations

There is **no real authentication or authorization system** in this
repository — confirmed by inspection; this was true before Phase 18.11 and
remains true after it. Per the spec's own instruction, this phase does
**not** attempt to build one. `actor` is an unauthenticated, caller-supplied
string, recorded purely for audit/observability (`actorId` in the audit log),
never verified, never used for an access-control decision. Anyone who can
reach `POST /api/projects/{id}/approve` can approve any Project currently in
`HumanReview` — exactly as anyone today can call `POST
/api/approvals/{id}/approve` for the legacy pipeline. This is an explicit,
documented limitation, not a claim of "authorized human approval" — the
implementation and its tests distinguish *an explicit approval action was
invoked* from *a verified human identity performed it*; only the former is
true today.

## 7. Legacy Pipeline Interaction

Empirically determined (§8 of the spec), by reading `ApprovalService`,
`WorkflowEngine`, `WorkflowStateStore`, `WorkflowTransitionValidator`, and
running the existing `ApprovalServiceTests` unchanged:

1. **Does legacy approval code still mutate `Project.Status`?** Yes —
   `WorkflowStateStore.ApplyTransitionAsync` writes `project.Status` directly,
   entirely bypassing `Project.TransitionTo`/`ProjectTransitions`.
2. **Can it still produce `Completed`?** Yes, via
   `ApprovalService.ApproveAsync` -> `IWorkflowEngine.TransitionAsync(...,
   ProjectStatus.Completed, ...)`.
3. **Can it bypass `HumanReview`?** For the specific `-> Completed` step,
   **no** — `WorkflowTransitionValidator.CanTransition` only allows `to ==
   Completed` when the *current* persisted status is exactly `HumanReview`
   (its own internal linear-sequence check), so `ApproveAsync` cannot
   complete a Project that isn't already in `HumanReview`, regardless of how
   it got there. `ApprovalServiceTests.ApproveAsync_TransitionsWorkflowToCompleted_AndPersistsDecision`
   (unmodified, still green) proves this end-to-end against the real
   database.
4. **Is it used by current tests/application flows?** Yes —
   `ApprovalServiceTests` (5 tests, all still passing unmodified),
   `/api/approvals/*` endpoints, and (indirectly, requiring an explicit
   manual "start orchestration" API call per the Phase 18.10 report) the
   legacy `AutonomousCodingOrchestrator`.
5. **Would changing it break legacy behavior?** Yes — `WorkflowTransitionValidator`
   deliberately allows entering `HumanReview` from *any* non-terminal status
   (`to == HumanReview` is unconditionally permitted), which is how
   `RequestApprovalAsync` can pause a Project for review from, e.g.,
   `Implementation` directly (proven by the existing, unmodified
   `RequestApprovalAsync_PersistsRecordAndPausesWorkflow_AcrossNewDbContextInstance`
   test, whose seeded Project starts at `Implementation`). Restricting this
   would change already-tested legacy semantics for no benefit to this
   phase's stated invariant.

**Conclusion**: legacy `ApprovalService` does **not** violate the specific
invariant this phase protects (HumanReview strictly required before
Completed) — it independently enforces the same rule through its own,
separate validator. It remains a **second mechanism that can complete a
Project**, and it can put a Project into `HumanReview` via a shortcut that
skips `Testing`/`Reviewing`/`ProjectCompletionGate` — but that shortcut is
pre-existing, unrelated to the `Reviewing -> Completed` bypass this phase was
asked to close, and rewriting/merging it was explicitly out of scope ("Do not
rewrite the legacy orchestration subsystem in this phase"). Per §8's
"Preferred" outcome ("legacy approval remains isolated... and cannot bypass
the new Project lifecycle boundary" — where "the boundary" is specifically
HumanReview-before-Completed), the current state qualifies: isolated (no
shared code path with the new `ApproveAsync`), and it cannot bypass
HumanReview for `Completed`. The residual risk (legacy can shortcut *into*
HumanReview) is recorded in §12/§13 below as a known, unresolved,
pre-existing condition rather than silently accepted.

## 8. Concurrency Behavior

- `ApproveAsync_ConcurrentRequests_ExactlyOneSucceeds_ResultIsCompleted`
  (service-level, two independent `DbContext`s, `Task.WhenAll`) and
  `Approve_ConcurrentRequests_BothSucceed_ProjectIsCompleted` (API-level, two
  concurrent HTTP POSTs) both prove: exactly one request performs the actual
  `SaveChangesAsync` write; the other hits `ConcurrencyConflictException`,
  reloads, observes `Completed`, and returns success — both callers get a 200
  with `Completed`, no exception ever reaches the HTTP layer, no duplicate
  domain-level transition (the loser never calls `TransitionTo` a second
  time — it returns immediately after the reload check).
- `ApproveAsync_Repeated_IsIdempotent` / `Approve_Repeated_IsIdempotent_BothReturn200Completed`
  prove a fully sequential repeat (no race) is equally safe.
- No locks, no static ownership maps, no distributed locks — only the
  pre-existing `Version` optimistic-concurrency column.

## 9. Tests Added

- **`ProjectTransitionsTests.cs`** (+3 `[InlineData]` rows): `Reviewing ->
  Completed` now `false`; `Reviewing -> HumanReview` and `HumanReview ->
  Completed` explicitly asserted `true`.
- **`ProjectServiceTests.cs`** (+16 tests): the corrected
  `TransitionStatusAsync_InvalidTransition_Throws` (now exercises `Created ->
  Testing` instead of the now-differently-handled `Completed` case);
  `TransitionStatusAsync_ToCompleted_IsAlwaysRejected_EvenFromHumanReview`;
  `ApproveAsync_FromHumanReview_TransitionsToCompleted`;
  `ApproveAsync_FromAnyOtherStatus_IsRejected` (10-case theory: Created,
  Analyzing, Specification, Architecture, Planning, Implementation, Testing,
  Reviewing, Failed, Cancelled); `ApproveAsync_Repeated_IsIdempotent`;
  `ApproveAsync_ConcurrentRequests_ExactlyOneSucceeds_ResultIsCompleted`;
  `ApproveAsync_UnknownProject_ThrowsNotFound`;
  `ApproveAsync_DoesNotMutateWorkItemsOrCreateAgentRuns` (seeds a real
  WorkItem + confirms it and the AgentRun count are untouched after
  approval).
- **`ProjectApprovalApiTests.cs`** (new file, 8 tests, real HTTP via
  `ApiTestFactory`): success (200, `Completed`); invalid-state (409, 5-case
  theory); unknown project (404); repeated (idempotent, 200 twice);
  concurrent (both 200, final state `Completed`); generic
  `PATCH .../status -> Completed` from `HumanReview` now returns 409 and
  leaves the Project at `HumanReview`.
- **`ProjectCompletionIntegrationTests.cs`** (existing Phase 18.10
  end-to-end test, updated in place, not recreated): the final step now
  calls `ProjectService.ApproveAsync` instead of the now-blocked
  `TransitionStatusAsync(..., Completed)`, and an added assertion proves the
  generic endpoint throws `ConflictException` for that exact same transition
  immediately before the real approval succeeds — directly satisfying the
  spec's "prove `Reviewing -> generic status API -> Completed` is no longer
  possible" requirement using the already-existing Phase 18.10 pipeline
  setup, without recreating it.
- Legacy `ApprovalServiceTests.cs`, `WorkflowEngineTests.cs`: **unchanged**,
  still green — proof the legacy pipeline's own tested behavior was not
  disturbed.

## 10. Exact Build/Test Results

- Targeted run before the full suite: `--filter
  FullyQualifiedName~ProjectApprovalApiTests` → 10/10 passed.
- `dotnet build AI.Factory.slnx -c Debug` — **0 Warnung(en), 0 Fehler**.
- `dotnet build AI.Factory.slnx -c Release` — **0 Warnung(en), 0 Fehler**.
- `dotnet test AI.Factory.slnx -c Debug --no-build` —
  `AI.Factory.Worker.Tests`: 85 passed, 0 failed;
  `AI.Factory.Api.Tests`: 293 passed, 0 failed (283 before this phase + 10 new);
  `AI.Factory.Application.Tests`: 766 passed, 0 failed (747 before this phase + 19 new).
  **Total: 1144 passed, 0 failed** (1115 before this phase + 29 new tests).

## 11. Migration Status

**None.** `dotnet ef migrations has-pending-model-changes --startup-project
../AI.Factory.Api --project .` → *"No changes have been made to the model
since the last migration."*, confirmed both before and after the full build/
test pass. This phase touched zero persisted schema — `ApproveAsync` reuses
`Project.Status`/`Project.Version`/`Project.CurrentError` (the last one,
added in Phase 18.10, was not touched here) and the existing
`IAuditLogger`/audit-log storage.

## 12. Known Limitations

- No real authentication/authorization exists; `actor` is an unauthenticated
  label (§6). Any caller reaching the approve endpoint can approve any
  `HumanReview` Project — this is a genuine, documented gap, not a design
  choice this phase can close within its stated scope.
- The legacy `ApprovalService`/`IWorkflowEngine` pipeline remains fully
  capable of completing a Project (from `HumanReview`, via its own separate
  validator) and can still place a Project into `HumanReview` from any
  non-terminal status, including ones the new pipeline would never allow
  directly (e.g. `Implementation -> HumanReview`), skipping
  `Testing`/`Reviewing`/`ProjectCompletionGate` entirely. This is pre-existing
  behavior, unrelated to the specific bypass this phase closed, and explicitly
  out of scope per "do not rewrite the legacy orchestration subsystem."
- `ProjectService.ApproveAsync`'s audit log entry is written only on the
  actual winning transition — the "losing" side of a concurrent race (which
  returns the reloaded, already-`Completed` Project) does not write a second
  audit entry for its own request. This was a deliberate choice to avoid a
  duplicate/misleading "Approved" log line for a request that performed no
  write of its own; the single entry from the winning request still fully
  answers "who/when/from what state" for the actual completion event.

## 13. Remaining Legacy Conflict

One conflict remains, by design not fully resolved in this phase (documented
per §8's requirement to "identify that dependency explicitly and isolate it
instead of silently preserving a bypass"): the legacy `ApprovalService` path
and the new `ProjectService.ApproveAsync` path are two **independent**
mechanisms that can both write `Project.Status = Completed` for the same
Project row. They are protected from **data corruption** by the shared
`Version`/`ConcurrencyConflictException` mechanism (both ultimately call
`FactoryDbContext.SaveChangesAsync`), and both **independently** enforce
"HumanReview required before Completed" — so they cannot presently produce
*contradictory* outcomes for that specific invariant. They remain, however,
two sources of truth for "how did this Project reach HumanReview in the first
place," which is a legitimate residual architectural risk for a future phase
to resolve (most likely by retiring `AutonomousCodingOrchestrator`/
`IWorkflowEngine` for Project-level lifecycle entirely, once nothing depends
on them), not something this phase — scoped explicitly to "lifecycle integrity
and human authorization only" — should resolve via a workflow-system rewrite.

## 14. Final Decision

Human Review & Approval Integrity: VERIFIED
Final Decision: GO
