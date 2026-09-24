# Flutter Client — API Gaps

Phase 19.0 · Backend `v1.0.0`

Capabilities the specified Flutter client needs that the **current** AI Factory API does
not expose. Each entry states what is missing, how it was verified, the UI impact, the
Phase 19 workaround, and a proposed backend change.

**Nothing in this file is implemented in Phase 19.0.** These are candidates for a later
API phase. Until then the client renders an explicit "not exposed by the API" state
rather than inventing, guessing or computing a substitute.

---

## G-REALTIME — No realtime update transport

**Missing.** No SSE, WebSocket, SignalR or long-polling endpoint.
**Verified.** No `MapHub`, `text/event-stream`, `WebSocket` or SignalR package
reference anywhere in `apps/src`.
**Status (Phase 19.9): still open.** No server-push transport; controlled client
polling implemented. Re-verified in Phase 19.9: the same search over `apps/src`
still finds no `MapHub`, `text/event-stream`, `WebSocket` or SignalR reference.
Polling does **not** close this gap and the UI never claims otherwise — it says
"live updates" and "last updated", never "connected" or "real-time".
**Impact.** Live execution views cannot be push-driven. A change is seen within
one polling interval (3–30 s depending on the screen), not the moment it happens.
**Workaround (implemented).** `PollingCoordinator`: bounded exponential backoff,
terminal-state stop, `401`/permanent-error stop, no-overlap queueing, page
visibility and app-lifecycle pause
([`flutter-architecture.md`](flutter-architecture.md) §12). Sufficient for a
single-operator console.
**Proposal.** `GET /api/projects/{id}/events` as SSE, emitting project status changes,
work item transitions and orchestration progress events. Only the coordinator's task in
`core/polling/` and the repository layer would change; screens are unaffected.

---

## G-RUN-TELEMETRY — AgentRun hides model, provider and token usage

**Missing.** `ToAgentRunDto` omits `Provider`, `Model`, `PromptTokens`,
`CompletionTokens`, `TotalTokens`, `Iteration`, although all are persisted on the
`AgentRun` entity.
**Verified.** `Program.cs` `ToAgentRunDto`; `Domain/Entities/AgentRun.cs`.
**Impact.** Workstream I ("model", "provider", cost/telemetry) cannot be satisfied on
the AgentRun detail screen.
**Workaround.** Show "Not exposed by the API"; link to the orchestration run, which does
return `selectedModel`.
**Proposal.** Add `provider`, `model`, `promptTokens`, `completionTokens`, `totalTokens`
to the AgentRun DTO. These are not secrets — the model name is already exposed on
`OrchestrationRun.selectedModel`. `input`, `workspacePath` and `systemPrompt` should
stay hidden.

---

## G-FALLBACK — Fallback usage is not persisted or exposed

**Missing.** No `fallbackUsed` / `fallbackReason` field on any entity or DTO, although
`IModelRouter.SelectCandidates` produces an ordered primary+fallback candidate list.
**Verified.** `Abstractions/IModelRouter.cs`; `Domain/Entities/AgentRun.cs` has no
fallback column.
**Impact.** Workstream I's "fallback used / fallback reason" cannot be displayed.
**Workaround.** Field omitted from the UI entirely — not shown as `false`, which would
assert something unverified.
**Proposal.** Persist the selected candidate index and the reason the primary was
skipped on `AgentRun`, then expose both.

---

## G-ACCEPTANCE — Acceptance criteria and verification results are not readable

**Missing.** `WorkItem.AcceptanceCriteria` is accepted on plan creation and evaluated by
`WorkItemAcceptanceVerifier`, but no endpoint returns either the criteria or an
`AcceptanceVerificationResult`, and the result is not persisted.
**Verified.** `ToWorkItemDto` omits `AcceptanceCriteria`; no route references
acceptance; `IWorkItemAcceptanceVerifier.Verify` returns a transient value.
**Impact.** Blocks the Acceptance tab (Workstream G), the acceptance column in the DAG
(Workstream H) and acceptance evidence in Human Review (Workstream J) — the last being
the most serious, because a reviewer is asked to approve without machine-checked
acceptance evidence.
**Workaround.** Explicit "Acceptance results are not exposed by the API" panel. The
client never renders a pass/fail it cannot source.
**Proposal.** Persist `AcceptanceVerificationResult` per work item and expose
`GET /api/tasks/{id}/acceptance` returning `{ status, summary, evaluatedCriteriaCount, criteria[] }`.
Highest-value gap for the review workflow.

---

## G-DEPS — WorkItem dependencies are only readable through the plan

**Missing.** No `GET /api/tasks/{id}/dependencies`; `ToWorkItemDto` omits dependencies
and `planKey`.
**Verified.** Dependencies have `POST`/`DELETE` routes but no read route; edges appear
only in the plan DTO's `dependsOn`.
**Impact.** The DAG is unbuildable for any project without a plan, and the client must
join two endpoints by plan key.
**Workaround.** Build the graph from `GET /api/projects/{id}/plan` ∪ `GET …/tasks`;
render a flat list with an explicit note when the plan returns `404`.
**Proposal.** Add `dependsOn: [workItemId]` and `planKey` to the WorkItem DTO. Small,
purely additive, removes the two-endpoint join.

---

## G-ARTIFACTS — No artifact browsing

**Missing.** The `Artifact` entity (project, work item, name, path, type, hash, size)
has no endpoints. `structuredResult.artifacts` carries bare strings only.
**Verified.** `Domain/Entities/Artifact.cs`; no route mentions artifacts.
**Impact.** "Changed artifacts" in Human Review and the Artifacts tab cannot show real
artifact metadata, and there is no diff.
**Workaround.** List the strings from `structuredResult.artifacts`, clearly labelled as
agent-reported names without metadata.
**Proposal.** `GET /api/projects/{id}/artifacts` returning metadata (never file
contents, never absolute paths). A review diff is a separate, larger feature.

---

## G-RUN-LIST — Orchestration runs cannot be listed

**Missing.** `GET /api/orchestration-runs/{id}` exists; there is no list or
per-project/per-work-item query.
**Verified.** Only four orchestration routes, all keyed by a known run id.
**Impact.** Execution history per project or per work item is unreachable; only
`ProjectDiagnosticSummary.latestRunId` (ops key) and ids from `POST` responses are known.
**Workaround.** Show the latest run from the diagnostics summary; cache ids the client
itself created during the session.
**Proposal.** `GET /api/orchestration-runs?projectId=&workItemId=&status=&page=&pageSize=`,
mirroring the existing `/api/agents/runs` query contract.

---

## G-ROUTING — No model / routing / provider endpoints

**Missing.** `IModelRouter` and the routing configuration exist server-side; nothing is
exposed over HTTP. `/api/operations/configuration` returns only provider *names* with
`enabled` and `hasSecretReference` flags.
**Verified.** No route matches `models`, `routing` or `providers`.
**Impact.** The "Models / Routing" screen (Workstream C, navigation §Models) has no data
source.
**Workaround.** Ship a documented placeholder screen that shows observed
`selectedModel` values and states that routing is server-side configuration. No
fabricated routing table.
**Proposal.** `GET /api/models/routing` returning per-role primary and fallback model
names and the active profile — **names only, never keys, never endpoints, never
credentials**.

---

## G-WORKER-HEALTH — Worker health is not exposed

**Missing.** `IWorkerHealthCoordinator` / `WorkerHealthSnapshot` exist in the
Application layer but are not registered on any API route.
**Verified.** No `WorkerHealth` reference in `AI.Factory.Api/Program.cs`.
**Impact.** The Dashboard's required "Worker Health" tile has no direct source.
**Workaround.** Compose a *derived, clearly labelled* indicator from
`GET /api/scheduler/status` (`enabled`, `lastCycleAtUtc` freshness) and
`GET /api/agents/metrics` (`queued` vs `running`). Presented as "Scheduler activity",
not as an authoritative worker health verdict.
**Proposal.** `GET /api/operations/worker` returning the existing
`WorkerHealthSnapshot`.

---

## G-HEALTH-DETAIL — Health endpoints return plain text

**Missing.** `/health`, `/health/ready`, `/health/live` return the bare strings
`Healthy` / `Unhealthy`; no per-check results, no version, no JSON.
**Verified.** No `ResponseWriter` configured; `HealthEndpointDeploymentTests` asserts
the exact body `Healthy`.
**Impact.** The Dashboard cannot say *which* dependency is unhealthy without the
operational key (`/api/operations/resilience`).
**Workaround.** Binary healthy/unhealthy tile; the detailed cause is offered behind
Operations when a key is configured.
**Proposal.** A JSON response writer on `/health/ready` with per-check `name`, `status`
and `duration` — no exception text, no connection strings.

---

## G-STAGE-TIMESTAMPS — No per-lifecycle-stage timestamps

**Missing.** `Project` carries only `createdAtUtc`, `updatedAtUtc` and
`lifecycleStartedAtUtc`. Stage entry times exist only as `WorkflowStateChanged` audit
entries, which require the operational key.
**Verified.** `ToProjectDto`; `ProjectTransitions` records no history.
**Impact.** Workstream E's "timestamps where available" is only partly satisfiable, and
not at all without an ops key.

Phase 19.4 found this gap to be wider than "no timestamps", and the lifecycle
visualization is shaped by the wider form:

* **No stage can be reported as completed.** One status arrives and no history, so
  the only defensible statement about another stage is that it lies before or after
  the reported one. A stage *before* it was necessarily entered — the transition
  table admits no way past it — but *when* is unknown; a stage *after* it may
  already have been visited, because `HumanReview -> Implementation` is a legal
  rework branch.
* **An interrupted project names no stage at all.** `Paused`, `Failed` and
  `Cancelled` **overwrite** `Status` rather than being recorded alongside it, so the
  stage at which execution stopped is unrecoverable from the API. This contradicts
  [`flutter-ux-spec.md`](flutter-ux-spec.md) §6.4, which expects a failed project's
  lifecycle to show "the stage at which it stopped".
* **`Paused` has no determinable resume target.** `PATCH /api/projects/{id}/status`
  accepts `Paused -> any active stage`, but the client cannot know which stage the
  project was paused in, so it cannot offer a Resume action without guessing one.
  This is why Project Detail offers none.

**Workaround.** Show stage times only when audit data is available; otherwise render
the stage without a timestamp. Never interpolate. Place stages relative to the
reported state, and mark **no** stage as current when the reported state overwrote
it — stating in the UI that the stage is not exposed (`ProjectLifecycle`,
`ProjectLifecycleWidget`).
**Proposal.** `GET /api/projects/{id}/history` returning the status transition timeline
under the normal (non-operational) rate-limit policy. A `PreviousStatus` (or
`PausedFromStatus`) field on the project DTO would resolve the resume target and the
failed-at-stage question on its own, at far lower cost.

---

## G-STALE-GLOBAL — Stale agent runs are per-project only

**Missing.** `staleAgentRuns` exists only on `ProjectDiagnosticSummary` (ops key).
`/api/agents/metrics` reports `queued` and `running`, not stale.
**Verified.** `IOperationalDiagnosticsService`; `/api/agents/metrics` projection.
**Impact.** The Dashboard's required "Stale AgentRuns" figure cannot be shown globally.
**Workaround.** Show it only for projects whose diagnostics have been fetched, labelled
as a partial figure.
**Proposal.** Add `stale` to `/api/agents/metrics`.

---

## G-MAXATTEMPTS — WorkItem attempt ceiling is not exposed

**Missing.** `WorkItem.MaxAttempts` (default 3) is persisted but not in the DTO.
**Verified.** `ToWorkItemDto`.
**Impact.** Attempts render as "2", not "2 of 3"; the UI cannot show retry exhaustion.
**Workaround.** Bare attempt count, no denominator, no assumed default.
**Proposal.** Add `maxAttempts` to the WorkItem DTO.

---

## G-PROJECT-PAGING — Unbounded list endpoints

**Missing.** `GET /api/projects` and `GET /api/projects/{id}/tasks` have no
`page`/`pageSize`, unlike `/api/agents/runs` and `/api/audit-log`.
**Verified.** `ProjectService.ListAsync` / `WorkItemService.ListByProjectAsync` return
everything.
**Impact.** Dashboard aggregation and the Projects table degrade as history grows.
**Workaround.** Client-side virtualization plus a filter recommendation above 200 rows.
**Proposal.** Add the same paging contract already used elsewhere, and a
`GET /api/projects/summary` returning status counts so the Dashboard stops downloading
every project to count them.

---

## G-AUDIT-SEVERITY — `severity` filter is validated but ignored

**Missing.** `GET /api/audit-log?severity=` parses and validates the value, then never
passes it to `AuditLogQuery`, which has no severity field.
**Verified.** `Program.cs` audit-log handler; `AuditLogQuery(ProjectId, ExecutionId, Page, PageSize)`.
**Impact.** A client filtering by `Error` silently receives all severities — a
misleading public contract, not merely a missing feature.
**Workaround.** The client does not send `severity`; it filters client-side within the
fetched page and says so in the UI.
**Proposal.** Either honour the parameter in `AuditLogQuery` or reject it with `400`.
This is the one item on this list that is a **contract inconsistency rather than a
missing capability**, and is therefore the only candidate that Phase 19.0's rules would
permit fixing in the backend — it is nonetheless left to an API phase, since the client
has a correct workaround and Phase 19.0 must not modify the released core.

---

## G-WORKITEM-EVENTS — No per-WorkItem event history

**Missing.** No endpoint returns a work item's transition or execution history. The
audit log carries `workItemId`, but only under the operational key, and
`/api/agents/runs` cannot be filtered by `workItemId`.
**Verified.** `AgentRunQuery(ProjectId, AgentDefinitionId, Status, From, To, Page, PageSize)`.
**Impact.** WorkItem Detail must fetch all project runs and filter client-side.
**Workaround.** Exactly that, on a bounded page size.
**Proposal.** Add `workItemId` to the agent-run query, and
`GET /api/tasks/{id}/history`.

---

## G-OPS-KEY-UX — Diagnostics require a shared operational key

**Not a missing endpoint — a security-design consequence to record.**
`/api/operations/*`, `/api/audit-log` and `/api/projects/{id}/diagnostics` require
`X-Operations-Key` when `RequireAccessKey` is enabled, while the entire
project/workflow surface requires nothing at all.

**Corrected in Phase 19.8 (evidence).** `/api/agents/metrics` was previously listed
here as protected; it is not. `OperationalEndpointSecurityMiddleware.IsOperationalPath`
matches only paths starting `/api/operations`, the exact path `/api/audit-log`, and
`/api/projects/*/diagnostics` — agent metrics is none of them, and is merely
rate-limited under the operational policy. Also recorded: `RequireAccessKey`
**defaults to `false`**, so a default deployment serves all of these without any key,
and the middleware accepts the key in `X-Operations-Key`, `X-Api-Key` or
`Authorization: Bearer` (`OperationalEndpointSecurityMiddleware.IsValidKeyProvidedAsync`).
Rejection is always `401` with `application/problem+json`; there is no `403` path.
**Impact.** A desktop client that shows diagnostics must hold a shared operational
secret; a Flutter Web build would place it in a browser.
**Client rule — implemented in Phase 19.8.** The key is entered by the operator and
stored in `flutter_secure_storage` behind `OperationalCredentialStore`, obscured in the
field, never rendered back (not even masked), never logged (`ApiHeaders.redacted`),
never in a URL. Sections degrade to an "Operator access required" state when absent,
independently of one another. A `401` triggers exactly one retry after re-reading the
stored key, then stops. **Flutter Web builds should not be configured with an
operational key.** *Phase 19.10:* a Web build keeps an entered key in memory for
the session only and says so on screen — it is never persisted in the browser.
Still open: the shared-secret design itself.
**Proposal.** Real API authentication with per-user tokens, so diagnostics are not
gated behind one shared secret. Larger than a UI phase.

---

## Explicitly **not** gaps

| Assumed missing | Reality |
|---|---|
| Project cancellation | `PATCH /api/projects/{id}/status` with `Cancelled` is legal from every non-terminal state |
| Pause / resume | `Paused` is a real `ProjectStatus` with legal transitions both ways |
| Approval endpoint | `POST /api/projects/{id}/approve` exists and is the sole authority; the legacy `/api/approvals/*` routes were retired in Phase 18.12 |
| AgentRun cancellation | `POST /api/agents/runs/{id}/cancel` |
| Orchestration cancellation | `POST /api/orchestration-runs/{id}/cancel` |
| Structured agent results | Fully exposed, including warnings, errors, artifacts and metadata |
| Deployment / version info | `GET /api/operations/deployment` (ops key) |
| Correlation tracing | `X-Correlation-ID` on every response + `GET /api/operations/{correlationId}` |

---

## Priority for a future API phase

| Rank | Gap | Reason |
|---|---|---|
| 1 | `G-ACCEPTANCE` | A human approves without machine-checked acceptance evidence |
| 2 | `G-AUDIT-SEVERITY` | An existing public contract is misleading |
| 3 | `G-DEPS` + `G-MAXATTEMPTS` | Two additive DTO fields remove a fragile two-endpoint join |
| 4 | `G-RUN-LIST` | Execution history is unreachable |
| 5 | `G-RUN-TELEMETRY` + `G-FALLBACK` | Model/provider observability for a model-routing platform |
| 6 | `G-WORKER-HEALTH` + `G-HEALTH-DETAIL` | Honest platform health on the Dashboard |
| 7 | `G-REALTIME` | Polling is adequate for one operator; push scales further |
| 8 | `G-ARTIFACTS`, `G-STAGE-TIMESTAMPS`, `G-PROJECT-PAGING`, `G-WORKITEM-EVENTS`, `G-STALE-GLOBAL` | Depth and scale, not correctness |
| 9 | `G-OPS-KEY-UX` | Requires a real auth design |
