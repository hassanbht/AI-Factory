# Flutter Client — AI Factory API Contract Inventory

Phase 19.0 · Backend `v1.0.0` · Derived from source inspection of
`apps/src/AI.Factory.Api/Program.cs` and the Application/Domain contracts it projects.

This inventory is **descriptive, not aspirational**. Every route below exists in the
released API. Capabilities the Flutter UI needs and this file does not list are
recorded in [`flutter-api-gaps.md`](flutter-api-gaps.md).

---

## 1. Transport basics

| Concern | Value |
|---|---|
| Base URL (container) | `http://<host>:8080` (`API_PORT`, `docker-compose.yml`) |
| Base URL (local dev) | `http://localhost:5223` / `https://localhost:7291` |
| Content type | `application/json` |
| Error content type | `application/problem+json` (RFC 7807 `ProblemDetails`) |
| Correlation header | `X-Correlation-ID` — accepted on request, always returned on response |
| Operational auth | `X-Operations-Key` header (name configurable via `Security:OperationalEndpoints:HeaderName`), required only when `Security:OperationalEndpoints:RequireAccessKey` is `true` |
| End-user auth | **None.** The API has no user authentication/authorization abstraction |
| CORS | Disabled by default; must be enabled with an explicit origin allowlist for Flutter Web (`Security:Cors`) |
| Max request body | 256 KB default (`Security:RequestLimits:MaxRequestBodySizeBytes`) |
| OpenAPI | `/openapi/v1.json` only when `Security:OpenApi:Enabled` is true (default: Development only) |

### Rate-limit policies (fixed window, per policy, **not** per client)

| Policy | Default | Applied to |
|---|---|---|
| `GeneralApi` | 100 / 60 s | project, task, plan, orchestration-run, agent definition/run reads and writes |
| `OperationalApi` | 50 / 60 s | `/api/operations/*`, `/api/audit-log`, `/api/agents/metrics`, `/api/projects/{id}/diagnostics` |
| `ExpensiveOperation` | 10 / 60 s | `POST /api/orchestration-runs`, `POST /api/agents/runs`, `POST /api/projects/{id}/plan/generate` |

Rate limiting is **global per policy**, not per caller, so an aggressive polling
client can starve itself and the operator. This is the hard constraint behind the
polling budget in [`flutter-ux-spec.md`](flutter-ux-spec.md) § Live updates.

### Canonical error envelope

    {
      "type": "https://httpstatuses.com/409",
      "title": "Operation conflict",
      "status": 409,
      "detail": "Project '...' cannot be approved from status 'Reviewing'...",
      "instance": "/api/projects/.../approve",
      "correlationId": "...",
      "traceId": "...",
      "code": "conflict",
      "message": "..."
    }

`code` is the stable machine-readable discriminator. Values emitted by
`ApiExceptionMapper`: `not_found`, `validation_error`, `concurrency_conflict`,
`conflict`, `forbidden`, `timeout`, `service_unavailable`, `too_many_requests`,
`database_unavailable`, `database_error`, `internal_error`.

---

## 2. Health

| Method | Path | Auth | Response |
|---|---|---|---|
| GET | `/health/live` | none | `200` + **plain text** `Healthy` |
| GET | `/health/ready` | none | `200`/`503` + **plain text** `Healthy`/`Unhealthy` |
| GET | `/health` | none | same checks as `/health/ready` |

Readiness aggregates: `database`, `configuration`, `critical-services`,
`circuit-breaker`, `shutdown` (plus `openviking` when `Health:CheckOpenViking`).
**The body is not JSON and names no individual check** — see gap `G-HEALTH-DETAIL`.

UI consumer: Dashboard → System Health tile. Refresh: 30 s while the Dashboard is visible.

---

## 3. Projects

| Method | Path | Auth | Request | Success | Errors |
|---|---|---|---|---|---|
| POST | `/api/projects` | none | `{ name, description? }` | `201` + Project | `400` name empty / >200 chars / invalid path chars, description >2000; `503` workspace provisioning failure |
| GET | `/api/projects` | none | — | `200` + Project[] | — |
| GET | `/api/projects/{id}` | none | — | `200` + Project | `400` bad GUID, `404` |
| POST | `/api/projects/{id}/source` | none | `{ sourceType: "Empty" \| "GitRepository", location?, branch? }` | `200` + Project (now `Analyzing`) | `400` invalid type / validation, `409` already initialized or non-empty workspace, `503` git or infrastructure failure |
| POST | `/api/projects/{id}/start` | none | `{ actor? }` (body optional) | `200` + Project | `400` no description or no RootPath, `404`, `409` status is `Created` (source not initialized) or not `Analyzing` |
| PATCH | `/api/projects/{id}/status` | none | `{ status: "<ProjectStatus>" }` | `200` + Project | `400` invalid enum, `404`, `409` illegal transition **or any attempt to set `Completed`** |
| POST | `/api/projects/{id}/approve` | none | `{ actor? }` (body optional) | `200` + Project (`Completed`) | `404`, `409` not in `HumanReview` |

### Project DTO (exact fields returned)

    id, name, slug, description, status, gitRepository, gitBranch,
    lifecycleStartedAtUtc, currentError, createdAtUtc, updatedAtUtc

`rootPath` is deliberately **not** exposed (internal filesystem path).

### Mandatory client call order

    POST /api/projects             -> Created
    POST /api/projects/{id}/source -> Analyzing
    POST /api/projects/{id}/start  -> autonomous execution, owned by the Worker

`start` is idempotent (`lifecycleStartedAtUtc` already set ⇒ returned unchanged).
`approve` is idempotent (already `Completed` ⇒ returned unchanged).

### Cancellation and pause (no dedicated endpoints — use PATCH status)

`PATCH /status` with `Cancelled` is legal from every non-terminal state.
`Paused` is legal from `Analyzing` … `HumanReview`, and `Paused` may resume to any of
those stages. `Completed` is **rejected** by this endpoint by design — approval is the
only path to it.

---

## 4. WorkItems (routes say "tasks")

| Method | Path | Auth | Request | Success | Errors |
|---|---|---|---|---|---|
| POST | `/api/projects/{projectId}/tasks` | none | `{ type, title, description?, priority? }` | `201` + WorkItem | `400` invalid GUID / type |
| GET | `/api/projects/{projectId}/tasks` | none | — | `200` + WorkItem[] | `400` |
| GET | `/api/tasks/{id}` | none | — | `200` + WorkItem | `400`, `404` |
| PATCH | `/api/tasks/{id}/status` | none | `{ status }` | `200` + WorkItem | `400`, `404`, `409` illegal transition |
| POST | `/api/tasks/{id}/dependencies` | none | `{ dependsOnTaskId }` | `201` | `400`, `409` circular dependency |
| DELETE | `/api/tasks/{id}/dependencies/{dependsOnTaskId}` | none | — | `204` | `400` |
| GET | `/api/tasks/{id}/readiness` | none | — | `200` `{ taskId, readiness }` | `400` |

### WorkItem DTO (exact fields returned)

    id, projectId, type, title, description, status, priority,
    attemptCount, repairAttemptCount, currentError,
    lastHeartbeatAtUtc, createdAtUtc, updatedAtUtc

Not returned: `maxAttempts`, `acceptanceCriteria`, `startedAtUtc`, `completedAtUtc`,
`planKey`, `dependencies`, `parentWorkItemId`, `featureId`, `lastCheckpoint`.

`status` is a **raw string** (the column is a string, not an enum), so the client must
parse defensively and keep an `unknown` fallback.

`readiness` ∈ `Ready`, `WaitingForDependency`, `BlockedByFailedDependency`,
`Running`, `Completed`, `Cancelled`, `Failed`.

Priority ∈ `Critical`, `High`, `Normal`, `Low`.

---

## 5. Plan (the only source of the dependency graph)

| Method | Path | Auth | Request | Success | Errors |
|---|---|---|---|---|---|
| POST | `/api/projects/{projectId}/plan` | none | `{ summary?, tasks: [{ planKey, title, description?, type, priority?, dependsOn[], acceptanceCriteria }] }` | `200` + Plan | `400`, `409` an incompatible plan already exists |
| GET | `/api/projects/{projectId}/plan` | none | — | `200` + Plan | `404` no plan / no work items |
| POST | `/api/projects/{projectId}/plan/generate` | none (**Expensive** limit) | — | `201` + AgentRun | `400`, `404` |

### Plan DTO

    projectId, planHash,
    tasks: [ { planKey, workItemId, title, type, priority, dependsOn[], status, readiness } ]

`dependsOn` holds **plan keys**, and every entry carries its `workItemId`, so the
client can build the complete WorkItem DAG from this single response. This is the only
endpoint that exposes dependency edges — see gap `G-DEPS`.

`plan/generate` starts the Planner **agent**; the plan itself appears later through
`GET .../plan`. The response is the AgentRun to poll.

### Plan creation preconditions (verified live — all three return `400`/`409`)

1. The project must be in `Planning`. From any other status:
   `409 "must be in 'Planning' status to create a plan"`.
2. Every task needs a **non-empty** acceptance envelope. `{"version":1,"criteria":[]}`
   is rejected: `400 "Acceptance criteria envelope contains no criteria"`.
   The smallest accepted form is `{"version":1,"criteria":[{"type":"BuildPasses"}]}`;
   `BuildPasses` and `TestsPass` take no `path`, other criterion types require one.
3. An **enabled AgentDefinition must already exist for each task's `type`**, or the
   plan is rejected as non-dispatchable:
   `400 "No enabled AgentDefinition exists for WorkItemType 'Analysis'"`.

A UI that offers plan creation has to surface all three, because none of them is
discoverable from the request shape alone.

---

## 6. Agent definitions and runs

| Method | Path | Auth | Request | Success | Errors |
|---|---|---|---|---|---|
| POST | `/api/agents/definitions` | none | `{ name, description?, agentType, systemPrompt?, enabled? }` | `201` | `400` |
| GET | `/api/agents/definitions` | none | — | `200` + Definition[] | — |
| GET | `/api/agents/definitions/{id}` | none | — | `200` | `400`, `404` |
| POST | `/api/agents/runs` | none (**Expensive**) | `{ agentDefinitionId, projectId?, input? }` | `201` + AgentRun | `400` |
| GET | `/api/agents/runs/{id}` | none | — | `200` + AgentRun | `400`, `404` |
| GET | `/api/agents/runs` | none | query: `projectId`, `agentDefinitionId`, `status`, `from`, `to`, `page`, `pageSize` (≤ 100) | `200` `{ items, totalCount, page, pageSize }` | `400` |
| POST | `/api/agents/runs/{id}/cancel` | none | — | `202` | `400`, `404` |
| GET | `/api/agents/metrics` | **ops key** | — | `200` counters | — |

### AgentDefinition DTO

    id, name, description, agentType, enabled, createdAtUtc, updatedAtUtc

`systemPrompt` is intentionally never returned.

### AgentRun DTO (exact fields returned)

    id, projectId, workItemId, agentDefinitionId, status, attempt,
    createdAtUtc, startedAtUtc, completedAtUtc, durationMs,
    output, failureCategory, errorMessage, correlationId,
    structuredResult: { version, status, summary, output, data,
                        warnings[], errors[], artifacts[], metadata }

**Not returned** although persisted: `provider`, `model`, `promptTokens`,
`completionTokens`, `totalTokens`, `iteration`, `input`, `workspacePath`.
See gap `G-RUN-TELEMETRY`.

`status` ∈ `Created, Queued, Running, Waiting, Succeeded, Failed, TimedOut, Cancelled`
(terminal: the last four).
`failureCategory` ∈ `Unknown, Validation, Build, Test, Agent, Model, Provider,
Timeout, Cancelled, Infrastructure, Approval, Review`.
`structuredResult.status` ∈ `Succeeded, Failed, Cancelled, TimedOut, Invalid`; the whole
object is `null` when absent or unparsable.

### `/api/agents/metrics`

    queued, running, created, started, succeeded, failed, timedOut, cancelled,
    workerClaimSuccess, averageDurationMs, totalDurationMs

Global, process-lifetime counters — reset on API restart, **not** scoped per project.

---

## 7. Orchestration runs (per-WorkItem execution)

| Method | Path | Auth | Request | Success | Errors |
|---|---|---|---|---|---|
| POST | `/api/orchestration-runs` | none (**Expensive**) | `{ projectId, taskId, maxAttempts? 1–10 }` | `202` `{ runId, status, statusUrl }` | `400` |
| GET | `/api/orchestration-runs/{id}` | none | — | `200` + run + `trace[]` | `400`, `404` |
| GET | `/api/orchestration-runs/{id}/attempts` | none | — | `200` + attempt[] | `400`, `404` |
| GET | `/api/orchestration-runs/{id}/logs` | none | — | `200` + progress event[] | `400`, `404` |
| POST | `/api/orchestration-runs/{id}/cancel` | none | — | `202` | `400` |

### Run DTO

    id, projectId, workItemId, status, currentStage, attempt, maxAttempts,
    selectedModel, requestedAtUtc, startedAtUtc, completedAtUtc,
    failureCategory, errorMessage,
    trace: [ { stage, status, attemptNumber, startedAtUtc, completedAtUtc,
               failureType, failureMessage, metadata } ]

`selectedModel` **is** exposed here (unlike on AgentRun).

`status` ∈ `Pending, Claimed, Running, Planning, Executing, Building, Testing,
Repairing, Reviewing, Evaluating, AwaitingApproval, Completed, Failed, Cancelled`.
`failureCategory` ∈ `PlanningFailure, AgentFailure, BuildFailure, TestFailure,
ValidationFailure, ReviewFailure, InfrastructureFailure, Timeout, Cancelled`.

### Attempts — the build/test/review evidence used by HumanReview

    id, orchestrationRunId, attemptNumber, startedAtUtc, completedAtUtc, success,
    agentResultSummary, buildResultSummary, testResultSummary,
    validationResultSummary, repairContextText, reviewResultSummary,
    failureCategory, errorMessage

### Progress events (`/logs`)

    id, orchestrationRunId, stage, timestampUtc, message

`stage` ∈ `RunCreated, ContextRetrieved, PlanningStarted, PlanReady, ModelSelected,
AgentStarted, AgentCompleted, BuildStarted, BuildCompleted, TestsStarted,
TestsCompleted, RepairStarted, RepairCompleted, ReviewStarted, ReviewCompleted,
EvaluationCompleted, LearningCompleted, AwaitingApproval, Completed, Failed`.

There is **no list endpoint** for orchestration runs. A run id is discoverable only
from the `POST` response or from `ProjectDiagnosticSummary.latestRunId`. See gap
`G-RUN-LIST`.

---

## 8. Scheduler

| Method | Path | Auth | Response |
|---|---|---|---|
| GET | `/api/scheduler/status` | none | `{ enabled, maxConcurrentTasks, lastCycleAtUtc, lastCycleProjectId, lastCycleDispatchedCount, lastCycleFailureCount, lastCycleSkippedDueToConcurrency, totalDispatched, totalFailures }` |

In-memory, resets on restart. This is the closest available proxy for "is the Worker
alive" — see gap `G-WORKER-HEALTH`.

---

## 9. Operations and diagnostics (ops key required when enabled)

| Method | Path | Response |
|---|---|---|
| GET | `/api/projects/{projectId}/diagnostics` | `ProjectDiagnosticSummary` (`404` when unknown) |
| GET | `/api/operations/projects/{projectId}/diagnostics` | identical alias |
| GET | `/api/operations/metrics` | `OperationalMetricsSnapshot` (~70 counters) |
| GET | `/api/operations/resilience` | `{ dependencies: [ { name, circuitState, consecutiveFailures, openedAtUtc, nextProbeAtUtc, bulkhead } ] }` |
| GET | `/api/operations/recent?count=` | recent audit entries (`count` 1–1000, default 50) |
| GET | `/api/operations/{correlationId}` | `{ correlationId, entryCount, firstEntryAtUtc, lastEntryAtUtc, hasFailures, entries[] }` |
| GET | `/api/operations/active` | `{ isStopping, activeCount, operations: [ { operationId, correlationId, operationName, projectId, taskId, runId, startedAtUtc, durationMs } ] }` |
| GET | `/api/operations/configuration` | `ConfigurationDiagnosticSnapshot` |
| GET | `/api/operations/database` | `DatabaseDiagnosticsSnapshot` |
| GET | `/api/operations/deployment` | `DeploymentDiagnosticsSnapshot` |
| GET | `/api/audit-log?projectId=&executionId=&severity=&page=&pageSize=` | `{ items, totalCount, page, pageSize }`, pageSize ≤ 100 |

### ProjectDiagnosticSummary

`lastFailureCategory` and `latestApprovalDecision` are **integers** (§11).

    projectId, projectStatus, latestTaskId, latestTaskStatus, repairAttemptCount,
    latestRunId, latestRunStatus, lastSuccessAtUtc, lastFailureAtUtc,
    lastFailureCategory, lastFailureMessage, latestApprovalDecision,
    workItemStatusCounts { status: count }, activeAgentRuns, staleAgentRuns,
    executionDurationMs, attemptCount

### DeploymentDiagnosticsSnapshot

    applicationName, environment, version, informationalVersion, frameworkDescription,
    osDescription, processArchitecture, isContainerized, startedAtUtc, uptime,
    databaseProvider, gitCommit?, buildTimestamp?, gitBranch?, buildNumber?

### DatabaseDiagnosticsSnapshot

    generatedAtUtc, providerName, isReady, commandTimeoutSeconds, retriesEnabled,
    maxRetryAttempts, optimisticConcurrencyEnabled, operationsCount, failuresCount,
    retriesCount, transientFailuresCount, concurrencyConflictsCount, busyFailuresCount,
    lockedFailuresCount, transactionFailuresCount, averageDurationMs

### ConfigurationDiagnosticSnapshot

    generatedAtUtc, environmentName, isValid,
    database { configured, provider, hasConnectionString },
    secretManagement { enabled, providerName, failFastOnMissingSecret },
    security { operationalEndpointsSecured, operationalKeyHeader, rateLimitingEnabled,
               maxRequestBodySizeBytes, corsEnabled, httpsRedirectEnabled, hstsEnabled },
    resilience { configuredProfilesCount, profileNames[] },
    externalProviders: [ { name, enabled, hasSecretReference, secretReferenceName } ],
    configurationSources[]

Boolean and name metadata only — **no secret values are ever present**, by contract.

### AuditLogEntry

    id, executionId, projectId, workItemId, timestampUtc, eventType, severity,
    message, metadataJson, correlationId, operationId, parentOperationId, runId,
    outcome, actorType, actorId, failureCategory

`severity` and `eventType` arrive as **integers**, not names — see
§11 *Serialization quirks*. `severity` is `AuditSeverity` (`Info`=0, `Warning`=1,
`Error`=2, `Critical`=3); `eventType` is a 48-value enum including
`HumanApproval`, `BuildResult`, `TestResult`, `RepairAttempt`, `ReviewResult`,
`WorkflowStateChanged`.

The `severity` query parameter is **validated but never applied** to the query
(`AuditLogQuery` carries no severity field). Filter client-side — gap `G-AUDIT-SEVERITY`.

---

## 10. Endpoints that do **not** exist

`/api/models`, `/api/routing`, `/api/providers`, `/api/artifacts`,
`/api/projects/{id}/artifacts`, `/api/projects/{id}/runs`,
`/api/projects/{id}/acceptance`, `/api/approvals/*` (retired in Phase 18.12),
and any WebSocket / SSE / SignalR hub.

Details and UI impact: [`flutter-api-gaps.md`](flutter-api-gaps.md).

---

## 11. Serialization quirks (verified against the running v1.0.0 API)

Both of these were confirmed by calling the real API during Phase 19.2, not
inferred from source. Each one silently corrupts data in a naive client.

### 11.1 Timestamps: `Z` on write, no `Z` on read

    POST /api/projects  ->  "createdAtUtc":"2026-09-19T19:02:11.4453985Z"
    GET  /api/projects  ->  "createdAtUtc":"2026-09-19T19:02:11.4453985"

The in-memory `DateTime` has `Kind=Utc`; the same value read back from SQLite
has `Kind=Unspecified`, so `System.Text.Json` omits the suffix. Every such
field is named `...AtUtc` and documented as UTC, so this is a serialization
artefact and not a change of meaning.

A client that calls a plain ISO-8601 parser will read the suffix-less form as
**local** time and shift every read-back timestamp by its UTC offset. Parse
these fields as UTC explicitly. The Flutter client does this in
`UtcDateTimeConverter`.

Affected: every `...AtUtc` field on a persisted entity (project, work item,
agent run, audit entry). Values computed in memory — `generatedAtUtc`,
`startedAtUtc` on `/api/operations/deployment` — carry the `Z`.

### 11.2 Enums in raw record responses are ordinals, not names

The API registers no `JsonStringEnumConverter`, so an endpoint returning a C#
record directly serializes its enum members as integers:

    GET /api/audit-log  ->  "eventType":31,"severity":1

Endpoints whose handler stringifies explicitly (`Status = x.ToString()`) send
names. The split, per endpoint:

| Sends enum **names** | Sends enum **ordinals** |
|---|---|
| Project, WorkItem, AgentRun, plan, readiness, orchestration run + trace | `ProjectDiagnosticSummary.lastFailureCategory`, `.latestApprovalDecision` |
| `/api/operations/resilience` `circuitState` | `AuditLogEntry.eventType`, `.severity`, `.failureCategory` |
| | `OrchestrationRunAttempt.failureCategory`, `OrchestrationProgressEvent.stage` |

A client mapping ordinals depends on the declaration order of the backend
enums, which is therefore part of the contract until a converter is added.
The Flutter client accepts both forms and degrades an out-of-range ordinal to
`unknown`.
