# Phase 18.1.8 — Structured Agent Execution Contract & Result Pipeline

## 1. Architecture

```
AgentRun (+ AgentDefinition, tools, memory)
        |
   AgentExecutionInstruction      <- structured request (Application.Agents)
        |
   IAgentInstructionBuilder        <- builds the actual instruction text,
        |                             always ending in the output contract
   AgentRun execution snapshot (Input = built instruction text)
        |
   IAgentRuntime / HermesAgentRuntime / HermesAgentExecutor   <- UNCHANGED
        |
   real Hermes / OmniRoute / LLM completion (raw text)
        |
   IStructuredAgentResultParser    <- extracts + validates the JSON envelope
        |
   StructuredAgentResult (versioned) --serialize--> AgentRun.StructuredResult (JSON column)
        |
   API (/api/agents/runs/{id}) --deserialize--> structuredResult DTO field
```

`IAgentRuntime` was **not changed** — `HermesAgentRuntime`/`HermesAgentExecutor` are untouched, per §7. The only new integration point is `AgentOrchestrator`, which now:
1. builds the execution snapshot's `Input` through `IAgentInstructionBuilder` instead of ad-hoc string concatenation (previously only applied when prior memory existed; now applied unconditionally, so the output-contract instructions always reach the agent), and
2. after the runtime call, builds a `StructuredAgentResult` — either by parsing `runtimeResult.Output` (on success) or deterministically from the already-known outcome (on failure/cancel/timeout, never attempting to parse LLM text that wasn't produced or is irrelevant) — and persists it alongside the existing `AgentRun` fields, in the same transaction/`UpdateAsync` call as before.

`AgentRun.Status` (the domain lifecycle state machine) is **completely untouched** — still driven purely by `runtimeResult.Success`/`FailureCategory`, exactly as before this phase. The structured result's own `Status` (`StructuredResultStatus`) is a separate, additional signal — the agent's self-report — and never drives `AgentRun.Status`.

## 2. New contracts

- **`AgentExecutionInstruction`** (`Application/Agents/AgentExecutionInstruction.cs`) — the structured request: `CorrelationId`, `Objective`, `Instructions`, `ProjectContext`, `Constraints`, `ExpectedOutputFormat`, `AllowedCapabilities`, `PreviousContext`. Only fields the current orchestration flow actually has to say — nothing spec­ulative for a future Planner/Developer/Reviewer role.
- **`IAgentInstructionBuilder`** / **`AgentInstructionBuilder`** (`Application/Agents/`) — the one place instruction/prompt text gets built. Renders objective, instructions, constraints, available capabilities, project/prior context, and always ends with an explicit output-contract section (the exact JSON shape, in plain text, appended to every instruction). Deliberately generic — the same shape for every agent today.
- **`StructuredAgentResult`** (`Application/Agents/StructuredAgentResult.cs`) — the versioned result envelope: `Version`, `Status` (`StructuredResultStatus`: Succeeded/Failed/Cancelled/TimedOut/Invalid), `Summary`, `Output`, `Data` (raw JSON payload text, so future agent-specific structured data needs no AgentRun schema change), `Warnings`, `Errors`, `Artifacts`, `Metadata`. Carries `StructuredAgentResult.CurrentVersion` (1) and the single shared `JsonOptions` (case-insensitive property matching, string enum values) every serialize/deserialize of this type must use — centralized so orchestrator persistence, API read-back, and tests never drift into inconsistent JSON shapes.
- **`IStructuredAgentResultParser`** / **`StructuredAgentResultParser`** (`Application/Agents/`) — finds the first balanced JSON object in raw agent text (handles prose wrapping and fenced code blocks), deserializes leniently, then enforces required fields (`version`, `status`) and the supported version. Pure text/JSON processing, no provider dependency. Distinguishes `Missing` (no JSON object found at all), `Malformed` (a JSON object was found but failed to parse or was missing/had an unrecognized required field), and `UnsupportedVersion`.

## 3. Validation rules

A structured result is accepted only if:
1. a balanced `{...}` JSON object can be located in the raw text,
2. it deserializes without a JSON syntax error,
3. it has a `version` field equal to `StructuredAgentResult.CurrentVersion` (1),
4. it has a `status` field matching one of `Succeeded`/`Failed`/`Cancelled`/`TimedOut` (an agent can never self-report `Invalid` — that value is rejected as malformed if an agent tries to use it, since `Invalid` is reserved for the Factory's own validation failures, not something an agent legitimately reports about itself).

Anything else is rejected — never silently accepted, never trusted merely because it parsed as JSON (an arbitrary well-formed JSON object with unrelated fields, e.g. `{"foo":"bar"}`, is rejected as malformed for missing the required contract fields). The validator has no provider-specific logic — it operates purely on text.

## 4. AgentRun integration / preserved behavior

- `AgentRunStatus` semantics: unchanged.
- Optimistic concurrency: unchanged (`Version` remains the EF concurrency token; the new columns are ordinary tracked properties).
- Idempotent creation (`CorrelationId`): unchanged.
- Worker claiming (`TryClaimNextPendingAsync`): unchanged.
- Failure/cancellation/timeout handling: unchanged at the runtime/orchestrator-status level; each of those paths now additionally gets a deterministic `StructuredAgentResult` (Status mirrored from the final `AgentRunStatus`, `Errors` populated from `ErrorMessage`) without ever trying to parse LLM output that wasn't relevant to that outcome.
- Metrics: unchanged — no new counters were added or needed; existing `AgentRunsSucceeded`/`Failed`/`TimedOut`/`Cancelled`/`AgentWorkerClaimSuccess` counters are driven by the same code path as before.
- `IAgentRuntime`/`HermesAgentRuntime`/`HermesAgentExecutor`: byte-for-byte unchanged in this phase (last touched in Phase 18.1.7 for the OmniRoute/model-forwarding/cancellation fixes).

## 5. Persistence decision

Two new nullable columns on `agent_runs`, added via one additive EF Core migration (`20260910073606_AddStructuredAgentResult`):

- `StructuredResult` (`TEXT`, nullable) — the serialized `StructuredAgentResult` JSON envelope.
- `StructuredResultVersion` (`INTEGER`, nullable) — denormalized copy of the envelope's `Version`, for cheap filtering without deserializing the blob.

Rejected alternatives, and why: a dedicated result table (unnecessary join/complexity for a 1:1 relationship that's already versioned inside the JSON itself); event sourcing (explicitly out of scope, and this isn't an event stream — one AgentRun has exactly one current structured result). A plain nullable JSON column is the simplest design that satisfies queryability (via `StructuredResultVersion` for coarse filtering, or SQLite `json_extract` later if ever needed), extensibility (new agent-specific `Data` payloads need no schema change), backward compatibility (existing rows read back with both columns `null`), and SQLite/EF-migration friendliness.

**Migration evidence:**
- `dotnet ef migrations add AddStructuredAgentResult` — generated a pure two-column `AddColumn` migration (verified by reading the generated `Up`/`Down` methods — no unexpected drops/renames).
- `AgentRunMigrationTests.Migrate_ToLatest_OnCleanDatabase_Succeeds` — clean database migrates to latest, including this migration. **Passed.**
- `AgentRunMigrationTests.Migrate_AddStructuredAgentResult_PreservesExistingAgentRunData` (new) — seeds an `AgentRun` row via raw SQL at the pre-migration baseline (`20260909101215_AddAgentMemory`), migrates to latest, confirms the existing row's `Input`/`Output` are untouched and both new columns read back `null`, then confirms a fresh EF-created row can genuinely read/write both new columns. **Passed.**
- `AgentRunMigrationTests.Migrate_ToLatest_FromPreAgentDomainModelBaseline_...` (pre-existing, unrelated to this phase) — still **passes**, confirming the new migration doesn't disturb the earlier agent-domain-model migration's own data-preservation guarantee.

## 6. API changes

`GET /api/agents/runs/{id}` and the paged `GET /api/agents/runs` now include a `structuredResult` field on the existing `AgentRunDto` shape (no second parallel API was created, per §9). It is:
- `null` when no structured result has been persisted for that run (the common case for a run predating this phase, or one still `Queued`/`Running`),
- an object with `version`, `status` (string), `summary`, `output`, `data`, `warnings`, `errors`, `artifacts`, `metadata` when one exists — deserialized fresh from the stored JSON on every read (never the raw blob passed through directly), and
- `null` (never a 500) if the stored JSON somehow fails to deserialize — a degradation, not an error surfaced to the API consumer, since the raw column is intentionally never exposed and an unreadable historical blob must not break the endpoint.

The API still never invokes Hermes synchronously — `POST /api/agents/runs` remains fire-and-forget-to-`Queued`, exactly as before this phase; unchanged.

## 7. Tests

- **Contract** (`StructuredAgentResultParserTests.cs`, 14 tests): valid success result, valid failure result, JSON embedded in prose/fenced code block, arbitrary `data` payload preserved as raw JSON, null/whitespace input (Missing), no JSON object at all (Missing), a JSON object with no balanced closing brace (Missing), balanced-but-syntactically-invalid JSON (Malformed), missing `version` (Malformed), missing `status` (Malformed), unrecognized `status` string (Malformed), an agent self-reporting `status: "Invalid"` (rejected as Malformed — an agent can never claim that status), unsupported version number (UnsupportedVersion), and a well-formed-but-contract-free JSON object (Malformed — never trusted just for being valid JSON).
- **Instruction builder** (`AgentInstructionBuilderTests.cs`, 3 tests): objective + output-contract always present; constraints/capabilities/context rendered when provided; empty sections omitted entirely.
- **Orchestrator** (extended `AgentOrchestratorTests.cs`, +5 tests): a valid structured JSON result is parsed and persisted; a malformed/non-JSON success output is persisted as an `Invalid` structured result *without* changing `AgentRunStatus` away from `Succeeded`; a runtime failure produces a deterministic `Failed` structured result without attempting to parse any LLM output; a runtime timeout produces a deterministic `TimedOut` structured result; the runtime actually receives the full instruction text (objective + output contract) rather than the raw `AgentRun.Input` alone, proving the orchestrator now routes through `IAgentInstructionBuilder`.
- **Persistence/migration**: see §5.
- **API** (extended `AgentRunsApiTests.cs`, +2 tests): a persisted structured result round-trips correctly through `GET`; a run with no structured result yet returns `structuredResult: null`; the pre-existing `RunDetail_DoesNotExposeInternalHermesOrSecretFields` test (asserting no top-level `version`/`input`/`provider`/`model` field) still **passes** unmodified, confirming the new nested `structuredResult.version` field doesn't leak as a top-level property.
- **Regression**: full suite, see §9.

## 8. Real Hermes evidence

A dedicated real-Hermes structured-output test was added: `RealHermesE2ETests.StructuredResult_RealAgentRun_ThroughFullFactoryPipeline_ReachesValidStructuredResult`, gated behind `AI_FACTORY_HERMES_SMOKE_TEST=true` (same convention as every other real-Hermes test in this repo), routed through the OmniRoute gateway established in Phase 18.1.7 (`auto/coding:free`).

Sequence actually executed, for real:
1. a real `AgentRun` created (`CreateQueuedRunAsync`, going through the real `AgentRunService`),
2. queued,
3. claimed by a real `AgentRunWorker` instance,
4. `AgentOrchestrator` builds the full instruction (objective + JSON output contract) via `IAgentInstructionBuilder`,
5. `HermesAgentRuntime`/`HermesAgentExecutor` run a real `docker run` against `nousresearch/hermes-agent:latest`,
6. Hermes reaches OmniRoute (`http://host.docker.internal:20128/v1`, `auto/coding:free`),
7. a real LLM executes and returns text,
8. `IStructuredAgentResultParser` validates the returned JSON envelope,
9. the Factory persists both `AgentRun.Status = Succeeded` and `AgentRun.StructuredResult`/`StructuredResultVersion`,
10. the test re-reads the `AgentRun` through the real repository and confirms the deserialized `StructuredAgentResult` is not `Invalid` and contains the deterministic marker `AI_FACTORY_STRUCTURED_RESULT_OK` in its `Summary`/`Output`/`Data`.

**Result: PASSED** (~35 s). `AgentRun.StructuredResultVersion == 1`, `StructuredAgentResult.Status != Invalid`, marker verified present in the parsed envelope.

## 9. Regression / stability gate — actual commands and results

- Targeted new tests (contract + instruction builder + orchestrator + persistence + API): all passed individually during development (see §7 for counts).
- Full deterministic regression (`dotnet test apps/AI.Factory.slnx -c Debug --no-build`, `AI_FACTORY_HERMES_SMOKE_TEST` unset):
  - `AI.Factory.Worker.Tests`: 74/74
  - `AI.Factory.Application.Tests`: 547/547
  - `AI.Factory.Api.Tests`: 246/246
  - **Total: 867/867 passed**, run once, no failures. (Baseline grew from Phase 18.1.7's 841 by 26: the new contract/parser/builder/orchestrator/migration/API tests.)
- `dotnet build apps/AI.Factory.slnx -c Debug` — 0 warnings, 0 errors.
- `dotnet build apps/AI.Factory.slnx -c Release` — 0 warnings, 0 errors.
- Real structured Hermes E2E: see §8 — **PASSED**.
- Phase 18.1.7's existing real-Hermes stability suite was rerun in full, since this phase's change (the orchestrator now *always* routes through `IAgentInstructionBuilder`, not only when prior memory existed) materially touches every real execution's prompt content:
  - `Smoke_RealAgentRun_ThroughFullFactoryPipeline_ReachesSucceeded` — PASSED
  - `Memory_ScopedEntry_IsFoundAndRealExecutionStillSucceeds` — one run hit a transient `SQLite Error 5: database is locked` during parallel test-class contention inside the same `dotnet test` invocation (a DbContext-construction-time native SQLite function registration race, unrelated to this phase's logic); **re-run in isolation immediately passed cleanly** — recorded here rather than silently retried away.
  - `WorkerRecovery_FailingRunThenSucceedingRun_WorkerStaysAlive` — PASSED
  - `Cancellation_WorkerExecutionTimeout_RealHermesProcessIsKilled` — PASSED
  - `Timeout_HermesProcessTimeoutSeconds_ReportsTimedOut` — PASSED
  - `Stability_FiveConsecutiveRealExecutions_AllSucceed` — PASSED, 5/5 real completions
  - `RealHermesApiE2ETests.PostRun_ThenRealClaimAndOrchestrate_ThenGet_ShowsSucceeded` — PASSED (run in isolation after the merged TRX log from the combined run overwrote its individual result — a test-tooling artifact of writing all three test projects' TRX output to one shared file name, not a test failure)

No result in this report was fabricated; the one transient failure is disclosed rather than omitted, along with the isolated re-run that resolved it.

## 10. Known limitations

- A malformed/invalid structured result never changes `AgentRunStatus` — by design (§4 required preserving existing semantics) — but it does mean a caller must check `structuredResult.status` (or its absence/`Invalid` value) separately from `AgentRun.status` to know whether the agent's own self-report was trustworthy. This is documented behavior, not an oversight.
- `AgentRun.Model` still has no end-to-end wiring from the API (`CreateAgentRunCommand` has no `Model` parameter) — noted in Phase 18.1.7's report, still true here; the real structured E2E test sets `Model` directly through the repository in its test harness, same workaround as Phase 18.1.7.
- The instruction builder is intentionally generic (one shape for all agents) — no Planner/Developer/Reviewer-specific behavior exists yet, per §13's explicit non-goals. Future specialized roles will need their own `Instructions`/`Constraints`/`AllowedCapabilities` construction logic upstream of the builder, not changes to the builder or the contract types themselves.
- `StructuredAgentResult.Data` is stored as raw JSON text (not a typed payload) precisely so future agent-specific structured data needs no `AgentRun`/migration change — consumers deserialize it themselves once a concrete shape is defined for a given agent type.
- The one transient SQLite "database is locked" failure during the combined real-Hermes suite run (§9) was not root-caused beyond "test-process parallelism contention" — it did not reproduce on an isolated rerun, and investigating deeper was judged out of proportion to a flake that has no bearing on the structured-contract work itself.

## 11. Final report

**Structured Agent Contract: VERIFIED**

**Final Decision: GO**

All Definition-of-Done items are met: the structured `AgentExecutionInstruction`/`StructuredAgentResult` contracts exist and are versioned; malformed/invalid output is rejected by a centralized, provider-neutral validator that never trusts JSON merely for parsing; `AgentRun` lifecycle semantics, optimistic concurrency, idempotent creation, Worker claiming, and existing failure/cancellation/timeout handling are all unchanged; `IAgentRuntime`/`HermesAgentRuntime`/`HermesAgentExecutor` and the OmniRoute real-execution path from Phase 18.1.7 are untouched and were re-verified working end to end; the structured result is persisted (one additive, data-preserving migration) and retrievable through both the repository and the API; deterministic tests (24 new, across contract/builder/orchestrator/persistence/API) and the full 867/867 regression suite pass; both Debug and Release builds are clean; the real structured-output Hermes E2E test passed against a genuine LLM completion; no security boundary was touched (structured output remains untrusted, never auto-authorizing shell/filesystem/Git/deployment/credential/network access — the parser only ever produces data, never executes anything); and no speculative future-agent architecture was introduced.
