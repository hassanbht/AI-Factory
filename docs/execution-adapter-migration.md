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
| **B** | **Execution-adapter abstraction with no behaviour change** | **Done, see below** |
| C | Real `HermesExecutionAdapter` (move Hermes logic out of `HermesAgentRuntime`; artifact recording to the Factory) | Not started |
| D | Full and live Hermes regression | Not started |
| D.1 | Runtime lifecycle hardening (named containers, cleanup, no hidden timeout retries, single timeout, pinned image) | Not started |
| E | `OpenHandsExecutionAdapter` (pinned Agent Server, per-run container) | Not started |
| F / G | Dual-run harness and empirical Hermes vs OpenHands comparison | Not started |
| H–K | Review path through the dispatcher, optional CLI adapters, gateway mapping, additive API telemetry, Hermes legacy reduction | Not started |

## Phase B: Execution Adapter Abstraction

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
| `LegacyHermesRuntimeAdapter` (key `Hermes`) | `Infrastructure/Agents/` | **Transitional.** Pure delegation to the unchanged `HermesAgentRuntime` |

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
