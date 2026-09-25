# Execution Adapter Migration

Status log for moving AI Factory from a Hermes-specific execution path to a pluggable execution-adapter seam. The approved direction comes from the architecture assessment of 2026-09-24:

```text
AgentOrchestrator → IAgentRuntime (AdapterDispatchingAgentRuntime)
                  → AgentType → Execution profile → IExecutionAdapter → runtime
```

AI Factory stays the control plane. Adapters execute one AgentRun and report an outcome. They never own AgentRun, WorkItem or Project state, and they never perform acceptance, review or approval.

| Phase | Scope | Status |
|---|---|---|
| A | Architecture assessment and migration plan | Done (Gate 1 approved) |
| B | Execution-adapter abstraction with no behaviour change | Done, see below |
| C | Real `HermesExecutionAdapter`; artifact recording and disabled-definition policy moved to the Factory | Done, see below |
| **D** | **Full and live Hermes regression** | **Done, see below** |
| D.1 | Runtime lifecycle hardening (named containers, cleanup, no hidden timeout retries, single timeout, pinned image) | Not started |
| E | `OpenHandsExecutionAdapter` (pinned Agent Server, per-run container) | Not started |
| F / G | Dual-run harness and empirical Hermes vs OpenHands comparison | Not started |
| H–K | Review path through the dispatcher, optional CLI adapters, gateway mapping, additive API telemetry, Hermes legacy reduction | Not started |

## Phase B: Execution Adapter Abstraction

> Historical record. Phase C (below) replaced the transitional adapter and removed `HermesAgentRuntime`.

### What exists now

| Type | Location | Role |
|---|---|---|
| `IExecutionAdapter`, `ExecutionCapabilities` | `Application/Abstractions/Execution/IExecutionAdapter.cs` | Runtime contract: `Key`, `Capabilities`, `ExecuteAsync(spec, ct)`. There is one capability flag, `FactoryRoutedModel` |
| `AgentExecutionSpec`, `ExecutionAgent`, `ExecutionWorkspace`, `ModelRoute` | `Application/Abstractions/Execution/AgentExecutionSpec.cs` | Provider-neutral request made of plain values only (no entities, repositories, secrets or runtime handles) |
| `ExecutionOutcome`, `ExecutionOutcomeStatus`, `ExecutionUsage` | `Application/Abstractions/Execution/ExecutionOutcome.cs` | Provider-neutral result: Succeeded, Failed, TimedOut or Cancelled, using the existing `FailureCategory` taxonomy |
| `IExecutionAdapterRegistry`, `DuplicateExecutionAdapterException` | `Application/Abstractions/Execution/IExecutionAdapterRegistry.cs` | Keyed, case-insensitive lookup that never substitutes one adapter for another |
| `ExecutionAdapterRegistry` | `Application/Execution/` | Built from DI; a duplicate or blank key throws |
| `ExecutionOptions`, `ExecutionProfileOptions`, `ExecutionAdapterOptions` | `Application/Execution/ExecutionOptions.cs` | The `Execution` config section; validated at startup |
| `ExecutionProfileResolver`, `ExecutionProfileResolution` | `Application/Execution/` | Maps AgentType to a profile and then to an adapter, and fails closed |
| `AdapterDispatchingAgentRuntime` | `Application/Execution/` | The registered `IAgentRuntime`: resolves the adapter, builds the spec, maps the outcome back |
| `LegacyHermesRuntimeAdapter` (key `Hermes`) | `Infrastructure/Agents/` | **Transitional.** Pure delegation to the unchanged `HermesAgentRuntime`. Removed in Phase C |

### Configuration

No existing configuration file was changed. With no `Execution` section (every current deployment), the composition root applies the only permitted default: a single profile `Hermes` pointing at adapter `Hermes`.

The optional explicit shape, validated at startup:

```json
"Execution": {
  "DefaultProfile": "Standard",
  "Profiles": { "Standard": { "Adapter": "Hermes" } },
  "AgentTypeProfiles": { "Repair": "Standard" },
  "Adapters": { "Hermes": { "Enabled": true } }
}
```

Fail-closed rules:

- A partial section is rejected at startup. For example, profiles without a `DefaultProfile`, a `DefaultProfile` or `AgentTypeProfiles` entry naming an unconfigured profile, or a profile with no adapter.
- At run time, an unregistered adapter key, a disabled adapter, or a routed model sent to an adapter without `FactoryRoutedModel` is returned as a `Validation` failure. The orchestrator persists that as a terminal `Failed` AgentRun. There is never a fallback to Hermes.

### Behaviour preservation

- Hermes prompt construction, CLI and docker arguments, environment, mounts, the resilience decorator, timeout and failure classification, and artifact recording all still run inside the unchanged `HermesAgentRuntime`, `HermesAgentExecutor` and `ResilienceAgentExecutorDecorator`.
- The adapter only reshapes the request and result:
  - spec → the same AgentRun snapshot
  - `AgentRunExecutionResult` → `ExecutionOutcome`
- The dispatcher maps back losslessly:
  - `Success` = `Status == Succeeded`
  - `FailureCategory` is passed through
  - exceptions, including cancellation, propagate unchanged
- `ExecutionAdapterGoldenEquivalenceTests` drives the same scripted Hermes execution through both paths in 12 scenarios, each on its own database. The two paths are:
  - old: `AgentOrchestrator → HermesAgentRuntime`
  - new: `AgentOrchestrator → dispatcher → adapter → HermesAgentRuntime`

  It compares executor inputs, the orchestration result, the persisted AgentRun and structured result (including metadata), artifacts, and exception behaviour.
- The golden test was mutation-checked: breaking the workspace, model or enabled mapping in the adapter makes it fail. The status mapping is invisible end to end (TimedOut/Cancelled is derived from the category), so it is asserted directly in `LegacyHermesRuntimeAdapterTests`.

### Evidence (2026-09-24, working tree on `8603fdd`)

- `dotnet build apps/AI.Factory.slnx`: 0 warnings, 0 errors.
- `dotnet test apps/AI.Factory.slnx`, two runs, both 1,435 passed and 0 failed. Before the change it was 1,358.

| Project | Before | After |
|---|---|---|
| Api | 310 | 314 |
| Application | 952 | 1,017 |
| Worker | 96 | 104 |

- New tests:

  | Class | Tests |
  |---|---|
  | `AdapterDispatchingAgentRuntimeTests` | 18 |
  | `ExecutionProfileResolverTests` | 13 |
  | `ExecutionAdapterGoldenEquivalenceTests` | 12 |
  | `ExecutionAdapterRegistryTests` | 9 |
  | `LegacyHermesRuntimeAdapterTests` | 7 |
  | `ExecutionAdapterArchitectureTests` | 6 |
  | `ExecutionAdapterCompositionTests` (Api) | 4 |
  | `ExecutionConfigurationBindingTests` (Worker) | 5 |
  | `WorkerCompositionRootTests` (additions) | 3 |

- **Live execution: none.** Hermes, OpenHands, OpenRouter and OmniRoute were not executed. The env-gated live tests (`AI_FACTORY_HERMES_SMOKE_TEST`) were disabled, return early, and are counted as *passed* by xUnit 2.9.2. They are not evidence of live behaviour.
- No database or migration change, no API or DTO change, no Flutter change, no deployment configuration change.

### Deliberately deferred

- **Phase C:**
  - Move Hermes logic into a real `HermesExecutionAdapter`.
  - Move artifact recording and the disabled-definition check to the Factory side; this removes `ExecutionAgent.Enabled`.
  - Add adapter metadata to the structured result.
  - Remove `HermesAgentRuntime` as a separate layer.
- **Phase H:** `AgentCodeReviewer` still calls `IAgentExecutor` directly, so the review path does not go through the dispatcher.
- **Phase D.1:** hidden timeout retries in `ResilienceAgentExecutorDecorator`, unnamed Hermes containers, and multiple timeout layers.
- **Test evidence:** gated live tests report *Passed* when disabled. Fixing that needs a skip mechanism (for example `Xunit.SkippableFact`, or xUnit v3 `Assert.Skip`), which is a new test dependency, so it was not added in Phase B.
- **Startup check:** unknown adapter keys in an explicit profile are rejected per run, not at startup. Adapters are scoped services, so the registry is only built inside a scope.

## Phase C: Real Hermes Execution Adapter

### Scope

Replace the Phase B delegation (`LegacyHermesRuntimeAdapter → HermesAgentRuntime`) with a real `HermesExecutionAdapter`. Move the two Factory concerns that lived inside the Hermes runtime (the disabled-definition check and workspace artifact recording) to the Factory side. Add adapter metadata to the structured result. Execution semantics are unchanged.

### Architecture

Before (Phase B):

```text
AgentOrchestrator
  → AdapterDispatchingAgentRuntime
  → LegacyHermesRuntimeAdapter
  → HermesAgentRuntime                      (prompt, classification, disabled check, artifact recording)
  → IAgentExecutor = ResilienceAgentExecutorDecorator
  → HermesAgentExecutor → docker run
```

After (Phase C):

```text
AgentOrchestrator                           (records workspace artifacts after execution)
  → AdapterDispatchingAgentRuntime          (disabled-definition policy, then adapter selection)
  → ExecutionProfileResolver → IExecutionAdapterRegistry
  → HermesExecutionAdapter  (key "Hermes")  (prompt, request mapping, timeout/cancel/failure interpretation, exit reason)
  → IAgentExecutor = ResilienceAgentExecutorDecorator   (unchanged)
  → HermesAgentExecutor → docker run                    (unchanged)
```

There is exactly one Hermes implementation. `HermesExecutionAdapter` owns the AgentRun-level Hermes mechanics. `HermesAgentExecutor` stays underneath it as the Hermes process layer, because `AgentCodeReviewer` still calls `IAgentExecutor` directly (Phase H). No second runtime layer does the same work.

### Hermes responsibilities and where they now live

| Responsibility | Owner after Phase C |
|---|---|
| Prompt construction (`SystemPrompt` + blank line + instructions, or instructions alone) | `HermesExecutionAdapter` |
| Request mapping: agent name, routed model (`-m`), Factory-resolved workspace | `HermesExecutionAdapter` |
| Cancellation interpretation (a cancelled token becomes a classified `Cancelled` result, not an exception) | `HermesExecutionAdapter` |
| Timeout interpretation (`timed out` / `timeout` error text becomes `TimedOut` with the `Timeout` category) | `HermesExecutionAdapter` |
| Failure classification (`agent` / `timeout` hints) and the default error message | `HermesExecutionAdapter` |
| Exit reason | `HermesExecutionAdapter` (new) |
| `docker run` arguments, `-z`, `-m` default, `--provider`, network, data volume, workspace mount, `HERMES_WRITE_SAFE_ROOT`, extra `-e` variables, provider API-key injection, PATH-only host environment | `HermesAgentExecutor`, unchanged |
| Process timeout, output truncation, missing-workspace refusal, scratch fallback, `WorkspacePathGuard` checks | `HermesAgentExecutor`, unchanged |
| Transient retry, circuit breaker, bulkhead | `ResilienceAgentExecutorDecorator`, unchanged |

The one change to `HermesAgentExecutor` is additive: `AgentExecutionResult` gained an optional `ExitCode`, filled in when the process ran to completion. Arguments, environment and process handling are byte-identical.

### Factory responsibilities moved

- **Disabled AgentDefinition:** checked in `AdapterDispatchingAgentRuntime` before profile resolution, so no adapter is invoked. The semantics are unchanged:
  - same message: `AgentDefinition '<Name>' (<Id>) is disabled.`
  - same category: `IFailureClassifier.Classify(message, "validation")`, which gives `Validation`
  - `DurationMs` is 0, and the run's provider and model are echoed back
  - the orchestrator persists a terminal `Failed` AgentRun
  - `ExecutionAgent.Enabled` was removed from the spec.

  One ordering edge case: a disabled definition whose profile also cannot be resolved now reports "disabled" rather than the resolution error. The built-in configuration always resolves, so this cannot happen in any current deployment.
- **Artifact recording:** `AgentOrchestrator` now calls the existing `IArtifactRecorder` (`FileSystemArtifactRecorder`, every workspace file) after the final runtime call and before the structured result is built. Everything about the recording is preserved:
  - the condition: success, `ProjectId` and `WorkItemId` present, non-blank workspace
  - the arguments: `ProjectId`, `WorkItemId`, and the run's token
  - the returned references: artifact ids for the WorkItem, filtered to the project
  - heartbeat coverage (still inside `orchestrator.ExecuteAsync`)
  - exception and cancellation propagation.

  The workspace is the Factory-resolved path that was given to the runtime. The old code used the path the executor reported back, which `HermesAgentExecutor` always sets to that same path for WorkItem-backed runs. A fallback run is attempted only after a failure, so at most one execution per AgentRun records artifacts. The recorder and repository are optional constructor dependencies (both or neither); both composition roots supply them, and composition tests assert that.

### Adapter metadata (internal only)

- `ExecutionOutcome.ExitReason` and `AgentRunExecutionResult.ExecutionAdapter` / `ExitReason` are new optional fields.
- For every run an adapter executed, `AgentOrchestrator` adds two keys to the existing structured-result `Metadata` dictionary:
  - `ExecutionAdapter = Hermes`
  - `ExitReason`: `exit:N` when the Hermes process exited with code N (success is always `exit:0`); otherwise `timeout`, `cancelled`, or `error` (no process exit was observed: launch failure, missing workspace, security refusal, retries exhausted, circuit open, bulkhead full).
- A nonzero exit whose error text mentions a timeout keeps `exit:N` as its exit reason while its status and category are TimedOut/Timeout, exactly as before.
- Runs rejected before any adapter ran (disabled definition, unresolved profile, unsatisfied capability) get neither key.
- This is persisted metadata inside the existing `StructuredResult` JSON column. It adds no schema, migration, DTO, endpoint or Flutter change.

### Configuration

No configuration file changed. `HermesOptions` still binds from the existing `Hermes` section. Binding it beneath `Execution:Adapters:Hermes` was deliberately not done; it belongs to the Phase I configuration refactor, and doing it now would introduce two competing sources. The built-in default profile is now keyed by `HermesExecutionAdapter.AdapterKey` (`Hermes`).

### Files

- **Removed:**
  - `Infrastructure/Agents/HermesAgentRuntime.cs`
  - `Infrastructure/Agents/LegacyHermesRuntimeAdapter.cs`
  - `tests/.../HermesAgentRuntimeTests.cs` (ported)
  - `tests/.../Execution/LegacyHermesRuntimeAdapterTests.cs` (ported)
  - the `AddScoped<HermesAgentRuntime>()` and `LegacyHermesRuntimeAdapter` registrations in both `Program.cs` files.
- **Added:**
  - `Infrastructure/Agents/HermesExecutionAdapter.cs`
  - `tests/.../Execution/HermesExecutionAdapterTests.cs`
  - `tests/.../Execution/AgentOrchestratorArtifactRecordingTests.cs`
  - `tests/.../Execution/Golden/phase-b-baseline.json`
- **Modified, source:**
  - `Application/Abstractions/IAgentExecutor.cs` (`ExitCode`)
  - `Application/Abstractions/IAgentRuntime.cs` (`ExecutionAdapter`, `ExitReason`)
  - `Application/Abstractions/Execution/AgentExecutionSpec.cs` (`Enabled` removed)
  - `Application/Abstractions/Execution/ExecutionOutcome.cs` (`ExitReason`)
  - `Application/Execution/AdapterDispatchingAgentRuntime.cs`
  - `Application/Agents/AgentOrchestrator.cs`
  - `Infrastructure/Agents/HermesAgentExecutor.cs`
  - `Api/Program.cs` and `Worker/Program.cs` (DI and comments)
  - `Worker/AgentRunWorker.cs` (comment only)
- **Modified, tests:**
  - dispatcher, golden, architecture and composition tests (Api and Worker)
  - comments in `AgentOrchestratorTests`, `RepairVerificationLoopTests`, `RealHermesE2ETests` and `LiveProjectLifecycleE2ETests`.

`AgentCodeReviewer`, `ResilienceAgentExecutorDecorator`, `FileSystemArtifactRecorder`, `WorkspacePathGuard`, `ProjectWorkspaceResolver`, `HermesOptions`, persistence, migrations, API contracts, Flutter and deployment files are untouched. The historical `.ai/context` phase notes still mention `HermesAgentRuntime`; they are records of their phases and were not rewritten.

### Golden equivalence

Before any Phase C production change, the Phase B old path (`AgentOrchestrator → HermesAgentRuntime`) was run for all 12 scenarios on commit `5273f53` and recorded in `Golden/phase-b-baseline.json`. The Phase B dispatcher path was checked equal at the same time.

`ExecutionAdapterGoldenEquivalenceTests` now drives the Phase C path with the same scripted executor and the production artifact wiring, then compares against that baseline. It checks:

- executor requests
- exceptions
- the orchestration result
- durations (except the cancel case)
- the persisted AgentRun
- the structured result
- recorded artifact paths.

Normalized values: artifact ids, the temporary workspace path, and line endings, since the baseline was captured on Windows and CI also runs Linux.

The one intended difference is the `ExecutionAdapter` and `ExitReason` metadata. It is asserted per scenario, then removed, and the rest must match byte for byte. The scripted executor now also reports the exit codes that `HermesAgentExecutor` would report; that field did not exist in Phase B.

Mutation check: each of these mutations failed exactly the expected scenarios, and the restored code passes 13/13.

| Mutation | Scenarios that failed |
|---|---|
| Prompt separator | all 11 executed scenarios |
| Workspace dropped | the two workspace-backed scenarios |
| Model dropped | routed and explicit model |
| Cancel message | cancelled |
| Timeout detection | timeout |
| No artifact recording | workspace-backed success |
| Recording on failure | workspace-backed failure |
| Disabled check removed | disabled |
| Disabled category changed | disabled |
| Adapter metadata dropped | the 10 scenarios with a structured result from an adapter run |

### Evidence

Evidence recorded 2026-09-24 on the working tree over `5273f53`.

- **Baseline before any change:**
  - `dotnet build apps/AI.Factory.slnx`: 0 warnings, 0 errors.
  - `dotnet test`: Api 314 and Application 1,017 passed. Worker had 1 failure in 104; four reruns of Worker gave 104/104.
- **After Phase C:**
  - `dotnet build`: 0 warnings, 0 errors.
  - Four full-suite runs:

    | Run | Api | Application | Worker | Total |
    |---|---|---|---|---|
    | 1 | 315 | 1,057 | 105 | 1,477 / 1,477 |
    | 2 | 315 | 1,057 | 104 / 105 | 1,476 / 1,477 |
    | 3 | 315 | 1,057 | 104 / 105 | 1,476 / 1,477 |
    | 4 | 315 | 1,057 | 105 | 1,477 / 1,477 |

  - The single failure in runs 2 and 3 is always `MultiWorkerRecoveryAcceptanceTests.TwoWorkers_SharingSqlite_ProveNonInterference_ConcurrentRecovery_AndNoDuplicateExecution` at line 378 (a concurrent SQLite recovery race: 0 of 1 expected recoveries). It is **pre-existing and unrelated**:
    - it does not touch the runtime, adapter, executor or artifact code;
    - run alone 20 times on a clean `HEAD` worktree, it failed 3 times;
    - run alone 20 times on the Phase C tree, it failed once.
- **Test count changes:**

  | Project | Before | After |
  |---|---|---|
  | Api | 314 | 315 |
  | Application | 1,017 | 1,057 |
  | Worker | 104 | 105 |

- **Test classes:**

  | Class | Tests | Change |
  |---|---|---|
  | `HermesExecutionAdapterTests` | 28 | new; replaces `HermesAgentRuntimeTests` (9) and `LegacyHermesRuntimeAdapterTests` (7) |
  | `AgentOrchestratorArtifactRecordingTests` | 14 | new |
  | `AdapterDispatchingAgentRuntimeTests` | 26 | was 18 |
  | `ExecutionAdapterArchitectureTests` | 11 | was 6 |
  | `ExecutionAdapterGoldenEquivalenceTests` | 13 | was 12 |
  | `ExecutionAdapterCompositionTests` (Api) | 5 | was 4 |
  | `WorkerCompositionRootTests` | +1 | |

- **Focused runs, all passing:**
  - adapter, dispatcher, golden, architecture and artifact tests
  - `AgentOrchestratorTests`, `HermesAgentExecutorTests`, `HermesWorkspaceMountTests`
  - `ModelRoutingAndRecoveryTests`, `AgentRunFullLifecycleIntegrationTests`, `RepairVerificationLoopTests`
  - 179 tests in total; the golden test also passed on repeated runs.
- **`dotnet format --verify-no-changes`** on the changed files: every file Phase C wrote or edited is clean. Two sets of findings remain; the same check reports both on a clean `HEAD` checkout, and Phase C left those lines unchanged:
  - the unindented model-routing preflight block in `Worker/Program.cs`
  - one initializer at `RepairVerificationLoopTests.cs:166`.
- **Live execution: none.** Hermes, OpenRouter, OmniRoute and OpenHands were not executed. The env-gated live tests were disabled; they return early and count as *passed*, so they are not evidence.
- No database, migration, API/DTO, endpoint, Flutter or deployment-configuration change.

### Deferred

- **Phase D:** live Hermes regression.
- **Phase D.1:**
  - hidden timeout retries
  - unnamed containers and cleanup
  - multiple timeout layers
  - image pinning.
- **Phase E:** OpenHands.
- **Phase H:** `AgentCodeReviewer` still calls `IAgentExecutor` directly.
- **Phase I:**
  - `HermesOptions` under `Execution:Adapters:Hermes`
  - the gateway still bound globally through `HermesOptions.Provider`.
- **Artifact semantics:** changed-files-only recording (G-ARTIFACTS).
- **Test evidence:** gated live tests still report *Passed* when disabled (see Phase B).

## Phase D: Real Hermes Regression and Live End-to-End Validation

Recorded 2026-09-24 on `0283c8e` (Phase C) plus the Phase D test-only changes listed under *Files*. Validation only: no production code, configuration, schema, API or Flutter change.

### Environment

| Item | Value |
|---|---|
| Docker | Docker Desktop, server 29.8.0 (WSL 2 backend). It was stopped at the start and was started for this phase. |
| Hermes image | `nousresearch/hermes-agent:latest`, image id `ef2bbf0546f7`, pulled 5 days before the run (not pinned; see D.1) |
| Live gate | `AI_FACTORY_HERMES_SMOKE_TEST=true`, loaded from the git-ignored `.env.live` into the test process only |
| Gateway | **OpenRouter**, `AI_FACTORY_LIVE_BASE_URL=https://openrouter.ai/api/v1`, key secret name `AI_FACTORY_LIVE_API_KEY_SECRET=OPENROUTER_API_KEY` (PRESENT). OmniRoute was also exercised; see *Gateway*. |
| Models | Planner, Review and general roles: `deepseek/deepseek-v4-flash-0731`. Implementation, Repair, Test and Documentation: `qwen/qwen3-coder`. |
| Model override | `.env.live` names `deepseek/deepseek-v4-flash-0731:free`. OpenRouter now answers that slug with `HTTP 404: This model is unavailable for free` and names the paid slug instead. For the test processes only, the three `:free` values were replaced by the provider-named slug; `.env.live` was not edited. |
| Databases | One throwaway SQLite file per test (`%TEMP%\live-e2e-*\factory.db`, WAL), in-memory SQLite for `RealHermesE2ETests`, and `%TEMP%\phase-d-proc-*\factory.db` for the process-level run. `C:\AI-Factory\data\factory.db` and the production Docker volumes were not touched. |
| Workspaces | Disposable temp directories only. Every Hermes data volume was disposable after the first smoke attempt (see *Failures*, F-1). |
| Credentials | `OPENROUTER_API_KEY` = PRESENT, `OMNIROUTE_API_KEY` = PRESENT. Values were never printed, and every captured log was checked mechanically for them. Spend for the whole phase: about USD 0.42. |

Starting Docker Desktop also started the existing `ai-factory` compose stack (`ai-factory-api-1`, `ai-factory-worker-1`, `hermes`), because their restart policy is `unless-stopped`. They were left as they were. They use their own volumes and never shared a database with the runs below.

### Test-only changes made in this phase

- `Fixtures/LiveExecutionEvidence.cs` (new). It reads back from the live run's own database the AgentRuns (id, WorkItem, attempt, provider, model, status, failure category, `ExecutionAdapter`, `ExitReason`), artifact rows per WorkItem, ValidationRuns and ReviewRuns. It asserts that every succeeded AgentRun persisted `ExecutionAdapter=Hermes` and a non-empty `ExitReason`. `LivePlannerE2ETests` and `LiveProjectLifecycleE2ETests` call it just before the approval step.
- `RealHermesE2ETests` and `RealHermesApiE2ETests` hard-coded the OmniRoute base URL, key secret name and model. They now read the same `AI_FACTORY_LIVE_BASE_URL`, `AI_FACTORY_LIVE_API_KEY_SECRET` and `AI_FACTORY_LIVE_MODEL` variables that `LiveWorkerHost` already reads, with the previous OmniRoute values as defaults. With the variables unset nothing changes. No new variable names were introduced.

### Real execution evidence

"Live" means the gate was enabled, a real Hermes container ran, and a real model request was made. Tests whose gate was off are not counted here.

| Test | Result | Evidence | Duration | AgentRun | WorkItem | Model | Gateway |
|---|---|---|---|---|---|---|---|
| `HermesSmokeTest` (executor level, bypasses the adapter) | **Passed** | container created, started, died and destroyed; `hello.txt` written with the exact content | 37 s | n/a (executor only) | n/a | deepseek-v4-flash-0731 | OpenRouter |
| `RealHermesApiE2ETests` (OmniRoute defaults) | Failed (D) | `POST /api/agents/runs` → claim → orchestrate → `HermesExecutionAdapter` → Hermes exit 2; OmniRoute's own call log recorded `POST /v1/chat/completions` combo `auto/coding:free` → 403 from `oc/big-pickle` | 47 s | `63df3493-1fe7-4278-a0f7-46353cf126a0` | none | auto/coding:free | OmniRoute |
| `RealHermesApiE2ETests` (OpenRouter) | **Passed** | API POST → Factory claim → adapter → Hermes exit 0 in 37,962 ms → API GET `Succeeded` with the marker in the output | 41 s | `baa8dad1-b176-4d47-a1c1-992af9445428` | none | deepseek-v4-flash-0731 | OpenRouter |
| `LivePlannerE2ETests.AutonomousStart_…_HumanApproval` | **Passed** | full lifecycle below; 6 containers (4 AgentRuns + 2 reviews), all removed | 9 m 16 s | Planner `54102027-b45f-4f14-9659-e1acdfddf44a`, Implementation `8778b446-571a-4db7-b1bc-f235fea4bfee`, Repair `2021926e-21ef-4c7c-85bb-a53d030c8daa`, Documentation `bfaf03d5-47c4-402e-b4d4-e7712479a67e` | `4b3d7ea8-6aae-4084-8bc2-2aa89d4a4da2`, `741f465d-961e-41cf-a46c-e386baadb902` | deepseek (Planner, Review), qwen3-coder (the rest) | OpenRouter |
| `LiveProjectLifecycleE2ETests` | Failed (F) | T01 and T02 both executed live (exit 0), passed real verification, were reviewed and reached `Completed`; the test then failed on its own expectation that T02 enters `Repairing` | 8 m 19 s | `0f550924-b275-4677-9946-e3bf821ae2aa`, `427b9539-a98a-41b0-b117-a4f02eb1c9d1` | `6943da62-0be3-4d77-bef2-e8bd5cb87127`, `03ada6c7-aac4-4519-985f-fcfc95cf5030` | qwen3-coder | OpenRouter |
| `RealHermesE2ETests` (11 tests, OpenRouter) | 2 passed, 9 failed (B/F) | 14 containers created, 11 started; see *Failures* | 19.5 min | several | | deepseek-v4-flash-0731 | OpenRouter |
| `LiveWorkerRecoveryE2ETests` | Failed (B), 4 of 4 runs | Planner AgentRun `1cdbdde1-a6aa-4257-846e-dddf4b9767cf` genuinely `Running` when the Worker was interrupted | 14–17 s | `1cdbdde1-…` | none | deepseek-v4-flash-0731 | OpenRouter |
| **Process-level run** (real `AI.Factory.Api` and `AI.Factory.Worker` executables, Production environment, driven only over HTTP) | **Completed** | lifecycle and HTTP approval below; 6 containers, all removed | 44 min wall clock (31 min of it waiting for AgentDefinition registration, see G-1) | Planner `22778351-8096-4492-83B5-9F19D8BB60E3`, Implementation `1E641431-FA9C-4EAA-8888-37E8C346508D`, Repair `B3341852-E867-4B88-9F6B-F2168F013C63`, Documentation `0F67F51B-14DF-47B7-A441-7A3C4F2C8C03` | `DBCD482D-B4E4-46DD-A5BA-82634BF758B2`, `E50C6E8A-D1D3-47EB-9811-B5AF12632D75` | deepseek (Planner, Review), qwen3-coder (the rest) | OpenRouter |

### Lifecycle evidence

From the process-level run, Project `4ac8931f-03ba-4f06-823c-8b17e60f4d7e`. Every value comes from the HTTP responses and the persisted audit trail. The in-process `LivePlannerE2ETests` run showed the same sequence for Project `fae1ac84-5f58-474e-bcdd-4c1b1df4dd6a`.

| Stage | Evidence | Actor |
|---|---|---|
| Create | `POST /api/projects` → 201 | operator (HTTP) |
| Source | `POST /api/projects/{id}/source` (GitRepository, a real bare repo) → 200 | operator (HTTP) |
| Start | `POST /api/projects/{id}/start` → 200; audit `WorkflowStarted Analyzing` | Human/qa-lead@factory.local |
| Specification, Architecture, Planning | audit `WorkflowStateChanged` | System/ProjectLifecycleOrchestrator |
| Planner | AgentRun `22778351…` Succeeded; plan materialized into 2 WorkItems with a dependency (`release.notes` ← `greeting.update` in the in-process run) | Factory dispatch |
| WorkItems, Execution | Implementation run, then verification **failed** (the pinned greeting test broke, by construction), then Repair run (`RepairAttemptCount=1`), then verification passed; the Documentation run was dispatched only after its prerequisite `Completed` | Factory |
| Acceptance | ValidationRuns: both WorkItems Success=1, Build=1, Tests=1; `WorkItemAcceptancePassed`; project-level `ValidationResult Passed` | System/ProjectCompletionGate |
| Reviewing | audit `WorkflowStateChanged Reviewing`; ReviewRuns persisted for both WorkItems (see *Failures*, G-2) | System/ProjectCompletionGate |
| HumanReview | `GET` 20:58:59 → `HumanReview`; held 20 s; `GET` 20:59:19 → still `HumanReview`, same `updatedAtUtc` | System/ProjectCompletionGate |
| Approve | `POST /api/projects/{id}/approve` `{"actor":"qa-lead@factory.local"}` → **200**, body `status: Completed`; audit `HumanApproval Approved` | Human/qa-lead@factory.local |
| Completed | `GET /api/projects/{id}` → `Completed` | the approval only |

No agent or runtime moved the Project to `Completed`. The only transition into `Completed` is the `HumanApproval` audit entry from the Human actor. The in-process test also re-proved that the generic `TransitionStatusAsync(…, Completed)` throws `ConflictException`.

The workspace really changed: `GetGreeting()` returns `Hello, AI Factory!`, `RELEASE_NOTES.md` contains exactly that line, and the repair updated the pinned test. Only the project's own workspace existed under the throwaway projects root. Each container mounted only that workspace and its own disposable data volume.

### Artifacts

Recorded by `AgentOrchestrator` (Phase C) from the Factory-resolved workspace:

| Run | WorkItem | ProjectId | Rows | Distinct paths |
|---|---|---|---|---|
| process-level | `DBCD482D…` (Implementation plus Repair) | `4AC8931F…` | 12 | 6 |
| process-level | `E50C6E8A…` (Documentation) | `4AC8931F…` | 6 | 6 |
| `LivePlannerE2ETests` | `4b3d7ea8…` (Implementation plus Repair) | `fae1ac84…` | 12 | 6 |
| `LivePlannerE2ETests` | `741f465d…` (Documentation) | `fae1ac84…` | 6 | 6 |

The paths are the six workspace files (`Demo.slnx`, `src/Demo.csproj`, `src/GreetingService.cs`, `tests/Demo.Tests.csproj`, `tests/GreetingServiceTests.cs`, `RELEASE_NOTES.md`) under the project's own root. Every row carries the correct ProjectId and WorkItemId.

Recording happens once per successful WorkItem-backed AgentRun. The Implementation and Repair runs each recorded the full file set, which gives 12 rows for 6 paths. That is exactly the preserved "record every file on every successful run" semantics. Duplicate rows per path are a G-ARTIFACTS item, not a Phase C change. The Planner run has no WorkItem and recorded nothing, as specified.

### Metadata

`ExecutionAdapter` and `ExitReason` were persisted for every live AgentRun that an adapter executed:

| Source | AgentRuns | `ExecutionAdapter` | `ExitReason` |
|---|---|---|---|
| process-level DB | all 4 (Planner, Implementation, Repair, Documentation) | `Hermes` | `exit:0` |
| `LivePlannerE2ETests` DB, asserted | all 4 | `Hermes` | `exit:0` |

Failure paths observed live, from `RealHermesE2ETests`:
- timeout: status `TimedOut`, category `Timeout`
- worker-execution cancellation: status `Cancelled`, category `Cancelled`
- invalid network: `docker run` exit 125, retries exhausted, category `Agent`

The in-memory databases of those tests are not reachable afterwards, so their `ExitReason` values (`timeout`, `cancelled`, `error`) were not read back live. They are covered deterministically by the golden and adapter tests.

### Gateway

- **OpenRouter** executed. Its `/api/v1/key` usage rose from USD 2.501 to 2.924 across the phase. The Worker's `ModelRoutingPreflight` verified every configured model against it at startup: `anthropic/claude-sonnet-5`, `openai/gpt-5.1`, `deepseek/deepseek-v4-flash-0731` and `qwen/qwen3-coder`.
- **OmniRoute** executed and was genuinely in the path, not bypassed. Its own call log (`~/.omniroute/call_logs/2026-09-24`) recorded `POST /v1/chat/completions` combo `auto/coding:free` at 19:31:11 UTC. OmniRoute routed it upstream to `oc/big-pickle`, which answered 403. OmniRoute was not running at the start; it was started for this phase and left running.

### Failures

Categories follow §28.

| Id | Test | Exact error | Component | Repro | Class | Code change? | Blocks? |
|---|---|---|---|---|---|---|---|
| F-1 | `HermesSmokeTest`, first attempt | `Hermes execution failed` (s6 log only); Hermes warned that the SQLite WAL was on a cross-VM filesystem | test default data volume `D:\Project\.hermes`, which is shared with the user's running `hermes` container | 1 | C | no; reran with the existing `AI_FACTORY_HERMES_DATA` variable pointing at a disposable directory | no |
| D-1 | all live tests, first attempt | `HTTP 404: This model is unavailable for free` for `deepseek/deepseek-v4-flash-0731:free` | OpenRouter catalog | 2 | D | no; provider-named paid slug used in the test process only. The shipped `appsettings.Production.json` also routes planning to this slug. | no |
| C-1 | OmniRoute-wired tests, first attempt | `Invalid API key` from OmniRoute | `.env.live` `OMNIROUTE_API_KEY` | 1 | C | no; the user corrected the key | no |
| D-2 | `RealHermesApiE2ETests` (OmniRoute) | `[403] oc/big-pickle … OpenCode's free tier can only be used from within OpenCode` | OmniRoute upstream for `auto/coding:free` | 1 | D | no; the same test passes on OpenRouter | no |
| F-2 | `LiveProjectLifecycleE2ETests` | `WorkItem 03ada6c7… reached terminal status Completed instead of Repairing` | test expectation | 1 | F | no; the test waits for `Repairing` although nothing in T02 fails verification. It passed in Phase 18.22 only because that model missed the file on its first try. | no |
| B-1 | `RealHermesE2ETests`: Smoke, StructuredResult, Planner, Memory, Stability, WorkerRecovery, FullProjectLifecycle_WithRepair | `Assert.NotNull() Failure` after the wait. Hermes exited 0 and `AgentRuntimeCompleted` was logged, then `AgentOrchestrator.ExecuteAsync` (line 283) → `AgentRunRepository.UpdateAsync` threw `ConcurrencyConflictException` (0 rows affected), plus `SQLite Error 5: database is locked` | this harness: one shared in-memory `SqliteConnection` across the worker, heartbeat and test contexts | reproduced identically on **Phase B `5273f53`** (Smoke, Stability, FullProjectLifecycle) | B | no; not a Phase C change. The file-based `LiveWorkerHost` tests do not hit it. | no |
| B-2 | `RealHermesE2ETests`: WorkItemExecution_…UpdatesPersistedWorkItemState, RealHermes_WorkItemExecution_… | `AgentWorkspaceResolutionFailed`: workspace under `C:\ProgramData\AI-Factory\workspace\…` does not exist / path resolves outside the configured root | the test provisions workspaces outside what the Windows production overlay resolves | reproduced identically on Phase B | B | no | no |
| B-3 | `LiveWorkerRecoveryE2ETests` | `The orphaned AgentRun was not marked as recovered by orphan recovery.` The interrupted run ended `Cancelled/Cancelled`, written by the dying Worker, instead of being left for orphan recovery | worker shutdown vs recovery expectation | 4 of 4 (2 on Phase B, 2 on Phase D) | B | no | no |

No failure was classified A (Phase C regression). Every A/B comparison against `5273f53` behaved identically, and no production code was changed.

### Known pre-existing flake

`MultiWorkerRecoveryAcceptanceTests.TwoWorkers_SharingSqlite_…` (SQLite recovery race, documented under Phase C: 3 of 20 isolated failures on clean HEAD). It did **not** fail in any of the four full-suite runs in this phase, and it is unrelated to the live paths above.

### D.1 findings (observed, not changed)

- **Unnamed containers and cleanup.** Every container gets a random name (for example `vigorous_bell`, `optimistic_cerf`).
  - When the test host was killed mid-run, its container kept running for about 25–30 s until its one-shot command finished.
  - A cancellation or timeout before `docker run` started a container left two containers permanently in `Created` state with `AutoRemove=true` (`upbeat_cartwright`, `crazy_austin`, mounts `ai-factory-e2e-*`, from the Phase B A/B run of the same unchanged executor). Auto-remove never fires for a container that never started.
  - At the end of that run a third container was still `Up` after the test finished.
- **Hidden retries.** The Hermes process timeout (2 s) was reported as `Transient agent execution error: Execution timed out` and retried three times before `TimedOut`. A deterministic configuration error (`network ai-factory-invalid-network not found`, exit 125) was also retried three times as "Transient", creating three containers that never started.
- **Timeout layers.** Hermes `TimeoutSeconds` (a docker-run ceiling), the resilience decorator's attempt budget, and `AgentWorker.ExecutionTimeout` all acted in the timeout and cancellation tests. `AgentRuntimeTimedOut` then reported `DurationMs=0` after retries were exhausted.
- **Image pinning.** `:latest` resolved to `ef2bbf0546f7`, 5 days old. This image prints `[stage2]` bootstrap lines and a config-migration warning to stdout. That breaks review parsing (G-2) and is a behaviour change the Factory did not choose.

### Known limitations (G)

- **G-1:** a fresh database has no AgentDefinitions. Until an operator registers them, the Factory holds the project in `Planning` and reports `No enabled AgentDefinition with AgentType 'Planner'`. That matches the documented v1.0.0 behaviour ("operational data, not seed data"). In the process-level run they were registered through the existing `POST /api/agents/definitions`, and the same Project then continued by itself.
- **G-2:** review ran for real, but its findings could not be parsed. `AgentCodeReviewer` still calls `IAgentExecutor` directly (Phase H) with `deepseek/deepseek-v4-flash-0731`. Each review container exited 0, but the image's stdout noise made the findings unparseable, so each review degraded to one non-blocking Info finding. The review stage was executed and persisted; its content was not useful. `AgentCodeReviewer` is unchanged by Phase C.

### Regression after live execution

| Check | Result |
|---|---|
| Baseline before any live run: `dotnet build apps/AI.Factory.slnx` | 0 warnings, 0 errors |
| Baseline full suite, run 1 and run 2 | 1,477 / 1,477 each (Api 315, Application 1,057, Worker 105) |
| After live execution: build | 0 warnings, 0 errors |
| After live execution: full suite, run 1 and run 2 | 1,477 / 1,477 each (Api 315, Application 1,057, Worker 105) |
| `dotnet format --verify-no-changes` on the Phase D files | new code clean; the only findings are the pre-existing mis-indented comment block at `LivePlannerE2ETests.cs:313-320`, which this phase did not touch (its diff starts at line 356) |

The full suite runs with the live gate unset. Its live tests return early there and are **not** counted as live evidence. The focused Hermes, adapter, lifecycle, Worker-recovery, composition-root and architecture tests are all part of those four green runs. The live results are only the ones in *Real execution evidence*.

### Live execution summary

| Runtime | Status |
|---|---|
| Hermes | EXECUTED |
| OpenRouter | EXECUTED |
| OmniRoute | EXECUTED (reached; upstream returned 403) |
| OpenHands | NOT EXECUTED |

No database schema or migration change; the process-level run applied the existing migrations to a throwaway file. No API, DTO or endpoint change. No Flutter change.

---

## Phase D.1 — Runtime Lifecycle Hardening

### Problems observed in Phase D

1. **Orphan containers after host/process termination**: Killed host processes left Hermes containers running for 25–30 s independently of the Factory process.
2. **Created containers left permanently**: Containers that failed to start (e.g. invalid network) or received early cancellation remained in Docker's `Created` state indefinitely because `--rm` only removes containers that successfully started and exited.
3. **Unnamed containers**: Docker assigned random names (e.g. `vigorous_bell`, `optimistic_cerf`), complicating correlation, tracking, diagnostics, and targeted orphan cleanup.
4. **Hidden timeout retries**: A 2 s process timeout was categorized as transient and retried three times before transitioning to `TimedOut`.
5. **Deterministic Docker error retries**: Missing Docker network (exit code 125) was retried three times as transient launch failures.
6. **Multiple timeout owners**: Hermes process timeout, Polly retry attempts, and Worker execution budget competed independently, causing misleading reported duration (`DurationMs=0`).
7. **Floating Hermes image**: Using `nousresearch/hermes-agent:latest` introduced startup output changes across pulls.

### Solution

1. **Deterministic Container Naming & Identity**:
   - Containers are deterministically named: `ai-factory-agent-<agentRunId:N>`.
   - Every container is tagged with Docker labels:
     - `factory=ai-factory`
     - `managed=true`
     - `agent-run-id=<GUID>`
     - `adapter=Hermes`
     - `project-id=<GUID>` (when available)
   - Labels strictly exclude any secrets, keys, or passwords.
2. **Reliable Container Lifecycle & Cleanup**:
   - Guaranteed cleanup in `HermesAgentExecutor`: `finally` block calls `CleanupContainerAsync` (`docker rm -f <containerName>`) whenever execution times out, is cancelled, encounters startup failures (e.g. exit code 125), or fails with launch exceptions.
   - Normal completion with `--rm` automatically removes containers upon container exit.
3. **Startup Orphan Sweep**:
   - `IContainerLifecycleManager` introduced in Application (`apps/src/AI.Factory.Application/Abstractions/Execution/IContainerLifecycleManager.cs`).
   - Implemented in Infrastructure as `DockerContainerLifecycleManager` (`apps/src/AI.Factory.Infrastructure/Agents/DockerContainerLifecycleManager.cs`).
   - Integrated into `WorkflowRecoveryHostedService`: on Worker startup and periodic stale sweep, inspects Factory-owned managed containers (`factory=ai-factory` and `managed=true`).
   - Safely removes containers whose AgentRun is missing from the database, already in a terminal state, or stale past `StaleExecutionTimeout`.
   - **Strict Ownership Guarantee**: Unmanaged/foreign containers or active containers belonging to other healthy executions are NEVER touched.
4. **Single Authoritative Execution Timeout**:
   - Factory boundary defines one authoritative deadline: `AgentExecutionSpec.Deadline = startedAt + effectiveTimeout` derived from `AgentWorkerOptions.ExecutionTimeout` and `ExecutionLimitsOptions.AgentExecutionTimeout`.
   - `AgentOrchestrator` computes the authoritative deadline and passes it down via `AgentExecutionSpec`.
   - `HermesExecutionAdapter` checks the deadline before launching and rejects already-expired specs immediately (`DurationMs = 0`, `TimedOut = true`) without invoking Docker.
   - `HermesAgentExecutor` derives remaining process timeout: `remaining = deadline - UtcNow` bounded by `HermesOptions.TimeoutSeconds`. Lower layers never compete with the authoritative Worker budget.
5. **Retry Classification Hardening**:
   - `AgentExecutionFailureClassifier` (`apps/src/AI.Factory.Infrastructure/Agents/AgentExecutionFailureClassifier.cs`) classifies outcomes deterministically:
     - Timeout (`TimedOut == true` or execution timeout text) → NON-RETRYABLE.
     - Cancellation (`Cancelled == true` or cancellation text) → NON-RETRYABLE.
     - Agent process exit code 1..124 (normal application non-zero exit) → NON-RETRYABLE.
     - Deterministic Docker configuration / CLI error (missing network, invalid image, volume mount error, usage syntax) → NON-RETRYABLE.
     - Genuine transient daemon launch failures (daemon unreachable, socket error, connection reset) → RETRYABLE.
   - `ResilienceAgentExecutorDecorator` uses `AgentExecutionFailureClassifier.IsTransientLaunchFailure` to prevent infinite or invalid retries.
6. **Pinned Hermes Image**:
   - Replaced floating `:latest` with pinned version: `nousresearch/hermes-agent:0.20.6`.
   - Pinned digest: `sha256:bcced061c9e114b711eabed6d4b8c4b2cff98ad33ec20317ef5692b5dd4ffecd`.
   - Upstream commit revision: `c30ac90a92097058ddd6f9db3fa2e3182a7bfdcc`.

### Configuration Changes

- `apps/src/AI.Factory.Infrastructure/Agents/HermesOptions.cs`:
  - `DockerImage` default updated from `nousresearch/hermes-agent:latest` to `nousresearch/hermes-agent:0.20.6`.
- `apps/src/AI.Factory.Worker/appsettings.json` and `apps/src/AI.Factory.Api/appsettings.json`:
  - `Execution:Adapters:Hermes:DockerImage` set to `"nousresearch/hermes-agent:0.20.6"`.
- Dependency Injection:
  - `IContainerLifecycleManager` registered as `DockerContainerLifecycleManager` in Worker and API composition roots.
  - Injected into `WorkflowRecoveryHostedService` to run orphan sweep on Worker startup and stale intervals.

### Docker Topology

```
Worker Host (or container)
   ↓
AgentRunWorker
   ↓
AgentOrchestrator (defines authoritative Timeout & Deadline)
   ↓
AdapterDispatchingAgentRuntime
   ↓
HermesExecutionAdapter
   ↓
HermesAgentExecutor
   ↓
docker run --rm --name ai-factory-agent-<agentRunId:N> \
  --label factory=ai-factory \
  --label managed=true \
  --label agent-run-id=<agentRunId> \
  --label adapter=Hermes \
  --label project-id=<projectId> \
  nousresearch/hermes-agent:0.20.6 ...
   ↓
(Completion / Timeout / Cancellation / Startup Failure)
   ↓
Guaranteed cleanup: docker rm -f ai-factory-agent-<agentRunId:N>
   ↓
0 Factory-owned containers remain
```

### Tests

- **New Unit Tests**:
  - `apps/tests/AI.Factory.Application.Tests/Execution/ContainerLifecycleUnitTests.cs`: 14 tests covering:
    - Container naming convention (`ai-factory-agent-<runId:N>`).
    - Metadata labels presence and secret exclusion.
    - Retry classification (timeout -> no retry, cancellation -> no retry, missing network -> no retry, invalid image -> no retry, agent exit -> no retry, transient daemon error -> retry).
    - Authoritative timeout derivation and expired deadline rejection without Docker launch.
    - Guaranteed container cleanup on timeout, cancellation, startup failure.
    - Orphan sweep logic (removing stale, preserving unmanaged/foreign).
- **New Integration Tests (Real Docker)**:
  - `apps/tests/AI.Factory.Worker.Tests/DockerLifecycleIntegrationTests.cs`: 4 real Docker daemon tests covering:
    - Startup failure cleanup (invalid network exit 125 leaves 0 containers).
    - Timeout cleanup (1s timeout leaves 0 containers).
    - Cancellation cleanup (caller cancellation leaves 0 containers).
    - Real Docker orphan sweep (creates Factory container + unmanaged container, sweeps stale Factory container, verifies foreign container is preserved).
- **Full Solution Regression**:
  - `dotnet test apps/AI.Factory.slnx`
  - Total: **1,495 passed, 0 failed, 0 warnings** across all 3 test assemblies:
    - `AI.Factory.Application.Tests`: 1,071 passed
    - `AI.Factory.Api.Tests`: 315 passed
    - `AI.Factory.Worker.Tests`: 109 passed

### Live Evidence

1. **Hermes Smoke Test (`HermesSmokeTest.cs`)**:
   - Gateway: OpenRouter (`deepseek/deepseek-v4-flash-0731`)
   - Image: `nousresearch/hermes-agent:0.20.6`
   - Duration: 36 s
   - Result: Succeeded; `hello.txt` created with exact content `Hello AI Factory`.
   - Containers before: 0
   - Containers after: 0
2. **Live Golden Lifecycle Run (`LivePlannerE2ETests.AutonomousStart_RealPlanner_ForcedRepair_DependentDispatch_ProjectVerification_HumanApproval`)**:
   - Gateway: OpenRouter (`deepseek/deepseek-v4-flash-0731` for planning/review, `qwen/qwen3-coder` for implementation/repair)
   - Image: `nousresearch/hermes-agent:0.20.6`
   - Active polling: Captured container `ai-factory-agent-dcbc0d8b13264aceab2b7f5c75dc7bbe` during execution with labels `factory=ai-factory`, `managed=true`, `adapter=Hermes`, `agent-run-id=dcbc0d8b-1326-4ace-ab2b-7f5c75dc7bbe`, `project-id=f863bfbf-8581-40b8-adc3-615fe25bdbf0`.
   - Containers before: 0
   - Containers after: 0
3. **Docker Lifecycle Integration Tests**:
   - Real Docker startup failure (exit 125): 0 containers remain.
   - Real Docker timeout: 0 containers remain.
   - Real Docker cancellation: 0 containers remain.
   - Real Docker orphan sweep: 1 stale Factory container removed, foreign unmanaged container preserved.

### Pinned Image

- **Tag**: `nousresearch/hermes-agent:0.20.6`
- **Digest**: `sha256:bcced061c9e114b711eabed6d4b8c4b2cff98ad33ec20317ef5692b5dd4ffecd`
- **Revision**: `c30ac90a92097058ddd6f9db3fa2e3182a7bfdcc`

### Remaining Limitations & Deferred Scope

- **Phase E**: OpenHands adapter (`OpenHandsExecutionAdapter`, container lifecycle for OpenHands, event-stream parsing).
- **Phase H**: Migration of `AgentCodeReviewer` from direct `IAgentExecutor` to `IExecutionAdapter`.
- **Phase I**: Provider gateway abstraction and routing architecture (resolving OmniRoute upstream 403).

