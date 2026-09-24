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
| **C** | **Real `HermesExecutionAdapter`; artifact recording and disabled-definition policy moved to the Factory** | **Done, see below** |
| D | Full and live Hermes regression | Not started |
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
