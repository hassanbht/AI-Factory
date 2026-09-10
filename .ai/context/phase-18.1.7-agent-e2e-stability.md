# Phase 18.1.7 — End-to-End Agent Execution + Real Hermes Smoke + Stability Gate

## 1. E2E architecture (actual, as verified)

```
HTTP POST /api/agents/runs (Api)
        |
   AgentRun = Queued            (AgentRunRepository, SQLite)
        |
   AgentRunWorker.PollOnceAsync  (real BackgroundService, real Worker composition root)
        |
   AgentOrchestrator.ExecuteAsync (real, no fakes)
        |
   HermesAgentRuntime             (real, maps AgentRun -> AgentExecutionRequest)
        |
   ResilienceAgentExecutorDecorator (real retry/circuit-breaker/bulkhead)
        |
   HermesAgentExecutor            (real)
        |
   SystemProcessRunner            (real process launch)
        |
   docker run --rm --network <mode> -v <data>:/opt/data -v <workspace>:/workspace
              -e OPENROUTER_BASE_URL=... -e OPENROUTER_API_KEY=...
              nousresearch/hermes-agent:latest -z "<prompt>" -m <model> --provider openrouter
```

Every layer in this chain was exercised with real, unfaked components in this phase — the only external dependency is the Hermes Docker image and the configured inference gateway.

## 2. Hermes environment

- Docker Desktop 29.7.2 is installed and working in this environment.
- `nousresearch/hermes-agent:latest` is already pulled locally (3.89 GB).
- `HermesOptions` (Infrastructure) is fully configuration-driven: `DockerImage`, `DataVolume`, `WorkspaceRoot`, `TimeoutSeconds`, `MaxOutputLength`, `NetworkMode`, and (new this phase) `Provider`, `AdditionalEnvironmentVariables`, `ProviderApiKeyEnvironmentVariable`, `ProviderApiKeySecretName`. Production default `NetworkMode: "none"` (least privilege, no egress) remains unchanged — a real execution still requires an explicit config override to allow egress, which is exactly what the test harness does (never the checked-in production default).
- **The direct OpenRouter path remains blocked**: the `OPENROUTER_API_KEY` present in this shell's environment is syntactically valid, but the OpenRouter account behind it has no billing/credits for the model Hermes selects by default (`anthropic/claude-opus-4.6`), and nothing in the current pipeline lets a caller override that model selection end-to-end from the API. This was fully diagnosed in this phase (see the September 10 investigation below) and remains true for the *direct* OpenRouter route.
- **A working route was found and verified**: routing Hermes's `openrouter` provider through a local [OmniRoute](https://github.com/diegosouzapw/OmniRoute) gateway (already running on this machine at `http://localhost:20128`, an OpenAI/OpenRouter-compatible multi-provider router with free-tier models) via `--provider openrouter` + `-e OPENROUTER_BASE_URL=http://host.docker.internal:20128/v1` + `-e OPENROUTER_API_KEY=<OmniRoute key>` + `-m auto/coding:free` produces a real, correct LLM completion. This is a genuine local dev/test bridge, not a change to what ships in `appsettings.json` — the production `Hermes` config section is untouched; only the opt-in test harness (`AI_FACTORY_HERMES_SMOKE_TEST=true`) configures it, via a `OMNIROUTE_API_KEY` environment variable read through the existing `ISecretProvider` abstraction (never hardcoded, never logged).

## 3. Test workspace isolation

Every real-Hermes test (`RealHermesE2ETests.cs` in `AI.Factory.Worker.Tests`, `RealHermesApiE2ETests.cs` in `AI.Factory.Api.Tests`) is opt-in only, gated behind `AI_FACTORY_HERMES_SMOKE_TEST=true` (same convention as the pre-existing `HermesSmokeTest.cs`), and:

- builds the exact real Worker composition root (`Program.ConfigureServices`) or Api composition root (`ApiTestFactory`) — not a trimmed/mocked graph;
- swaps the database for an isolated in-memory SQLite connection;
- swaps `HermesOptions.DataVolume`/`WorkspaceRoot` for fresh temp directories per test, cleaned up on disposal;
- never touches `data/factory.db` or any real Hermes state under `%USERPROFILE%\.hermes`;
- resolves the OmniRoute gateway key via `ISecretProvider`/`OMNIROUTE_API_KEY`, a separate secret name from the real `OPENROUTER_API_KEY` — the two are never conflated.

## 4. Smoke prompt

Deterministic, trivial, per spec §5: *"Respond with exactly this text and nothing else: `AI_FACTORY_HERMES_SMOKE_OK`"*. No file creation, no repository modification requested of Hermes.

## 5. Real execution result — VERIFIED

Docker + the Hermes image + `SystemProcessRunner` + `HermesAgentExecutor` + `HermesAgentRuntime` + `AgentOrchestrator` + a real `AgentRunWorker` instance were driven together, repeatedly, end to end, and reached genuine successful LLM completions:

```
AI_FACTORY_HERMES_SMOKE_OK
```

returned verbatim, exit code 0, `AgentRunStatus.Succeeded` persisted correctly, through the complete real pipeline (Worker claim → Orchestrator → Runtime → Executor → real Docker container → real LLM response → persisted result).

### Investigation trail (what it took to get here)

1. **`NetworkMode: "none"` blocks all egress** — production default, by design. The test harness overrides to `"bridge"`.
2. **Direct OpenRouter is unusable in this environment** — `OPENROUTER_API_KEY` is valid but the account has no billing/credits for the model Hermes defaults to. Confirmed by direct manual `docker run` reproduction (`OpenRouter reported that billing, credits, or account entitlement is exhausted for anthropic/claude-opus-4.6`).
3. **OmniRoute identified as a working alternative** — already running locally at `:20128`, OpenAI/OpenRouter-compatible, with free-tier models (`auto/coding:free` verified to return correct, on-topic completions via direct `curl`).
4. **Hermes has no generic "custom OpenAI-compatible provider"** — `hermes config show` only lists OpenRouter, OpenAI (STT/TTS only), Exa, Parallel, Firecrawl, Tavily, Browserbase, Browser Use, FAL, Anthropic as known providers. A hand-authored `config.yaml` (matching the shape OmniRoute's own dashboard integration generates for "Hermes Agent") was mounted into `DataVolume` and was silently ignored by this Hermes CLI build (`config get providers` returned `{}`) — not pursued further, since a working alternative existed.
5. **The working alternative**: Hermes's *existing* `openrouter` provider already supports a base-URL override via the `OPENROUTER_BASE_URL` environment variable (undocumented in `--help`, discovered empirically) — many OpenRouter-compatible gateways rely on exactly this convention. `--provider openrouter -m auto/coding:free` + `OPENROUTER_API_KEY`/`OPENROUTER_BASE_URL` pointed at OmniRoute reproduced a correct completion via manual `docker run`.
6. **A real implementation bug, found and fixed while wiring this into `HermesAgentExecutor`**: the provider API key and base-URL override were initially set on the **host** `docker.exe` process's own environment (`ProcessRunRequest.EnvironmentVariables`) — but `docker run` does **not** forward the CLI's own environment into the container. The manual reproduction worked because the manual command used `-e NAME=VALUE` **container** flags directly; the code did not. Fixed by emitting `-e NAME=VALUE` as `docker run` arguments (before the image name), not as host-process environment variables. See §9 for the full write-up.

## 6. Failure-path validation

Verified via `WorkerRecovery_FailingRunThenSucceedingRun_WorkerStaysAlive`: Run A, executed against a deliberately invalid `HermesOptions.NetworkMode`, fails fast (Docker container-creation error, no image pull, no LLM call attempted) and is correctly persisted as `Failed` with `FailureCategory.Agent` and a non-empty `ErrorMessage`, without throwing out of the orchestrator or crashing the Worker. Run B (valid config, same live Worker) then reaches real `Succeeded`.

## 7. Cancellation — VERIFIED

`Cancellation_WorkerExecutionTimeout_RealHermesProcessIsKilled`: a real `AgentRunWorker`, configured with a 2-second `AgentWorkerOptions.ExecutionTimeout`, was set against a real, in-flight `docker run` invocation. The worker's own execution-timeout cancellation killed the real Docker process mid-flight; `HermesAgentRuntime` classified the result as `FailureCategory.Cancelled`; the `AgentRun` reached `AgentRunStatus.Cancelled` in the database. Reproduced consistently across three independent runs (~2–5 s each).

## 8. Worker recovery — VERIFIED

Verified in the same test as §6: Run A (deliberately invalid network) → `Failed`; the same `AgentRunWorker` instance, still running, then claimed and executed Run B → real `Succeeded`. The Worker never crashed or stopped polling.

## 9. Real defects found and fixed

### 9.1 Orchestrator dropped terminal-state persistence on cancellation/timeout

**Reproduced:** the first real cancellation run left its `AgentRun` stuck at `Running` forever in the database, even though the runtime layer correctly computed a `Cancelled` result and logged `AgentRuntimeCancelled`.

**Root cause:** `AgentOrchestrator.ExecuteAsync` computed the final status in memory (`run.TransitionTo(finalStatus)`) but persisted it via `_agentRunRepository.UpdateAsync(run, cancellationToken)` — reusing the *same* token that had just been cancelled to produce that outcome. EF Core's `SaveChangesAsync` immediately threw `OperationCanceledException` before the write reached the database. The same risk existed in `FailRunningAsync`.

**Fix (`apps/src/AI.Factory.Application/Agents/AgentOrchestrator.cs`):** both terminal-state persistence calls now use `CancellationToken.None`.

**Regression tests:** `AgentOrchestratorTests.ExecuteAsync_RuntimeCancelledMidFlight_StillPersistsCancelled_DespiteTokenAlreadyCancelled` (deterministic, no Hermes), plus the real `Cancellation_WorkerExecutionTimeout_RealHermesProcessIsKilled` E2E test itself, which failed before this fix and passes after it.

### 9.2 `AgentExecutionRequest.Model` never reached the Hermes CLI

`HermesAgentExecutor` built its `docker run ... -z <prompt>` argument list without ever appending `-m <model>`, even though the Hermes CLI supports it directly. Fixed in `HermesAgentExecutor.cs`: `-m <model>` is now appended when `request.Model` is set. This was the fix that made §5's model override (`auto/coding:free`) possible at all.

### 9.3 Provider credentials set on the wrong process's environment

Found while wiring §5's OmniRoute route into `HermesAgentExecutor`: the provider API key and base-URL override were being set on the **host** `docker.exe` process's environment (`SystemProcessRunner`'s `ProcessRunRequest.EnvironmentVariables`), which is never forwarded into a container by `docker run` — only `-e NAME=VALUE` **arguments** are. The code silently produced a container with no provider credentials at all, reproducing the exact same "no provider configured" failure as the unconfigured case, even though the (correct) secret was resolved and present.

**Fix (`apps/src/AI.Factory.Infrastructure/Agents/HermesAgentExecutor.cs`):** `HermesOptions.AdditionalEnvironmentVariables` and the resolved `ProviderApiKeySecretName` value are now emitted as `-e NAME=VALUE` `docker run` arguments (inserted before the image name), not as host-process environment variables. The host process's own environment (`SystemProcessRunner`) still carries only `PATH`, unchanged — the security property "the container never inherits the Factory process's ambient environment" is preserved; only deliberately configured, named values reach the container, via the correct mechanism.

**Verification:** this was root-caused and fixed by direct comparison against a working manual `docker run` reproduction (§5.6) — the real E2E test suite (§14) is the regression coverage; there is no narrower unit-testable surface for "which of two docker mechanisms carries an env var into a container" beyond integration-level verification, which the gated tests already provide.

## 10. Persistence verification

`AgentRun.Status`, `StartedAtUtc`/`CompletedAtUtc`, `Output`, `ErrorMessage`, and `FailureCategory` were all confirmed correct against the real database for every outcome observed (`Succeeded`, `Failed`, `Cancelled`, `TimedOut`). The Factory database remained the sole source of truth throughout; no Hermes-side state was ever treated as authoritative.

## 11. Metrics

`Smoke_RealAgentRun_ThroughFullFactoryPipeline_ReachesSucceeded` asserts before/after deltas on the real `IOperationalMetrics` singleton (`AgentRunsCreated`, `AgentRunsStarted`, `AgentRunsSucceeded`, `AgentWorkerClaimSuccess`) — verified to increment correctly on a real successful run.

## 12. Logging / correlation

Verified via a custom in-test `ILoggerProvider` capturing all structured log lines: the same `AgentRunId` is traceable end to end across `AgentRunWorker` ("claimed by worker"), `AgentOrchestrator` ("AgentRunStarted"/"AgentRunCompleted"/"AgentRunFailed"), and `HermesAgentRuntime` ("AgentRuntimeStarted"/"AgentRuntimeCompleted"/"AgentRuntimeFailed"/"AgentRuntimeCancelled"). No secrets or full prompts/outputs appear in any log line.

## 13. Stability run — VERIFIED

5 consecutive real `AgentRun` executions, same `AgentRunWorker` instance, same OmniRoute-routed model: **5/5 reached `Succeeded`** (`Stability_FiveConsecutiveRealExecutions_AllSucceed`, ~117 s total for 5 runs). No hangs, no worker crash, no orphaned containers.

## 14. Tests / build

- Targeted real-Hermes E2E (opt-in, `AI_FACTORY_HERMES_SMOKE_TEST=true`, `OMNIROUTE_API_KEY=<gateway key>`), final clean run — **7/7 passed**:
  - `Smoke_RealAgentRun_ThroughFullFactoryPipeline_ReachesSucceeded` — PASS (~45 s)
  - `Memory_ScopedEntry_IsFoundAndRealExecutionStillSucceeds` — PASS (~27 s)
  - `WorkerRecovery_FailingRunThenSucceedingRun_WorkerStaysAlive` — PASS (~33 s)
  - `Cancellation_WorkerExecutionTimeout_RealHermesProcessIsKilled` — PASS (~5 s)
  - `Timeout_HermesProcessTimeoutSeconds_ReportsTimedOut` — PASS (~10 s)
  - `Stability_FiveConsecutiveRealExecutions_AllSucceed` — PASS, 5/5 (~117 s)
  - `RealHermesApiE2ETests.PostRun_ThenRealClaimAndOrchestrate_ThenGet_ShowsSucceeded` (Api.Tests) — PASS (~52 s)
- Full deterministic regression (`AI_FACTORY_HERMES_SMOKE_TEST` unset, no Hermes/Docker/network involved), run after all production code changes:
  - `AI.Factory.Worker.Tests`: 73/73 passed
  - `AI.Factory.Application.Tests`: 524/524 passed
  - `AI.Factory.Api.Tests`: 244/244 passed
  - **Total: 841/841 passed.** The 833 figure quoted in the phase prompt could not be substantiated by any `.ai/context/phase-18.1.6-*.md` (which does not exist in this repository) and should not be trusted going forward.
- `dotnet build apps/AI.Factory.slnx -c Debug` — 0 warnings, 0 errors.
- `dotnet build apps/AI.Factory.slnx -c Release` — 0 warnings, 0 errors.

## 15. Database

No migration required (`NO MIGRATION`) — this phase added no new persisted shape.

## 16. Docker / Compose

`docker-compose.yml`'s `worker` service currently has no Docker CLI/socket wired in, so a *containerized* deployment of the Worker cannot itself shell out to `docker run` for Hermes — only a bare-metal/dev `dotnet run` Worker (as used throughout this phase) can. Unchanged pre-existing limitation, noted for whoever eventually containerizes the full stack.

## 17. Known limitations

- The verified real-Hermes path in this phase depends on a locally running OmniRoute gateway (`http://localhost:20128`) and an `OMNIROUTE_API_KEY` environment variable — this is a **local development/test bridge**, not a production configuration. Nothing in `appsettings.json` (Worker or Api) was changed; `HermesOptions.Provider`/`AdditionalEnvironmentVariables`/`ProviderApiKey*` are new, generically useful capabilities, but they are only *populated* by the opt-in test harness in this phase.
- The direct-OpenRouter path (`HermesOptions` with no `Provider`/env overrides, Hermes's own persisted default) remains blocked by exhausted account billing — unrelated to and unfixed by this phase's OmniRoute route.
- There is still no mechanism, anywhere in the AgentRun pipeline (API, `AgentRunService`, `AgentDefinition`), for a caller to select which model a run should use — the test harness sets `AgentRun.Model` directly via the repository, bypassing `CreateAgentRunCommand` (which has no `Model` parameter). Wiring this end-to-end is explicitly out of scope (§8: "Do not redesign OpenRouter/model routing in this phase") and is a natural candidate for a future phase, now that both the domain field and the CLI forwarding (`-m`) exist and work.
- The `.ai/context/phase-18.1.6-*.md` document is still missing despite Phase 18.1.6's code being present and fully exercised by this phase's regression run — a documentation gap from a prior session, not introduced here.
- Three empty (0-byte) stray files at the repository root (`1`, `Worker`, `new`) predate this phase and were left untouched.

## 18. Real Hermes Gate

```
Real Hermes E2E: VERIFIED
Evidence: 7/7 real-Hermes gated tests passed (Smoke, Memory, WorkerRecovery, Cancellation, Timeout, Stability 5x,
          API E2E) — real Docker containers, real AgentRun -> Worker -> Orchestrator -> Runtime -> Executor
          pipeline, real LLM completions returned via a local OmniRoute gateway, correct marker text
          ("AI_FACTORY_HERMES_SMOKE_OK") verified in every successful run's persisted Output.
Caveat:   Verified via a local dev-only gateway (OmniRoute), not the direct OpenRouter path the checked-in
          appsettings.json still points at by default — that path remains blocked by exhausted account
          billing, independent of anything in this codebase.
```

## 19. Evidence table

| Gate | Result | Evidence |
|------|--------|----------|
| Real Hermes smoke | PASS | `Smoke_RealAgentRun_ThroughFullFactoryPipeline_ReachesSucceeded` — real completion, marker text verified |
| API → Worker → Hermes | PASS | `RealHermesApiE2ETests.PostRun_ThenRealClaimAndOrchestrate_ThenGet_ShowsSucceeded` — real POST/claim/orchestrate/GET, real completion |
| Failure isolation | PASS | `WorkerRecovery_...` Run A: real invalid-network run → `Failed`, `FailureCategory.Agent` |
| Worker recovery | PASS | Same test: same live `AgentRunWorker` instance claimed and executed Run B (real `Succeeded`) after Run A failed |
| Cancellation | PASS | `Cancellation_WorkerExecutionTimeout_RealHermesProcessIsKilled` — real docker process killed at ~2 s, `AgentRunStatus.Cancelled` |
| Hermes-level timeout | PASS | `Timeout_HermesProcessTimeoutSeconds_ReportsTimedOut` — `AgentRunStatus.TimedOut`, `FailureCategory.Timeout` |
| Persistence | PASS | Terminal status/timestamps/output/error/category verified against the real DB for every outcome |
| Metrics | PASS | Before/after deltas on the real `IOperationalMetrics` singleton verified on a real successful run |
| Logging/correlation | PASS | Captured `ILoggerProvider` shows one `AgentRunId` traceable across Worker/Orchestrator/Runtime |
| Stability 5x | PASS | `Stability_FiveConsecutiveRealExecutions_AllSucceed` — 5/5 real successes, ~117 s total, no hangs/leaks |
| Full regression | PASS | 841/841 (73 + 524 + 244), one run |
| Debug build | PASS | 0 warnings, 0 errors |
| Release build | PASS | 0 warnings, 0 errors |

## 20. Problems found and fixed

1. **`AgentOrchestrator` dropped terminal-state persistence on cancellation/timeout** — fixed with `CancellationToken.None` for the terminal-state `UpdateAsync` calls. Regression test added.
2. **`HermesAgentExecutor` never forwarded `AgentExecutionRequest.Model` to the Hermes CLI** — fixed: append `-m <model>` when set.
3. **Provider credentials/base-URL override were set on the host `docker.exe` process's environment instead of injected into the container via `-e` arguments** — fixed: `AdditionalEnvironmentVariables` and the resolved provider API key are now emitted as `-e NAME=VALUE` docker-run arguments. This was the defect that made the Real Hermes gate flip from `BLOCKED` to `VERIFIED` once corrected.

## 21. Final decision

```
GO
```

Every gate passed: real Hermes execution reaches a genuine LLM completion through the complete, unfaked Factory pipeline (Worker → Orchestrator → Runtime → Executor → Docker → LLM → persisted result); failure isolation, Worker recovery, cancellation, Hermes-level timeout, and a 5x stability run all passed against real infrastructure; persistence, metrics, and logging/correlation are all correct; the full deterministic regression suite (841/841) and both Debug/Release builds are clean. Three real defects were found by this phase's real E2E investigation and were each root-caused, fixed, and regression-tested (§9/§20) — exactly the outcome this phase exists to produce.

The one caveat worth carrying forward explicitly: the verified path routes through a local OmniRoute gateway, not the direct OpenRouter configuration checked into `appsettings.json`. The mechanism (`HermesOptions.Provider`/`AdditionalEnvironmentVariables`/`ProviderApiKey*`) is now real, tested, production-ready infrastructure — but which gateway/credentials a real deployment should actually point at is an operational decision, not a code change, and is intentionally left unset in the committed configuration.

## 22. Next phase

The stability gate has passed — the next phase can move beyond infrastructure validation toward the first meaningful agentic workflow, per the original prompt's own guidance, beginning with controlled task execution and structured AgentRun input/output. Two follow-ups worth carrying into that work, not blockers:

- A real deployment decision for which inference gateway/credentials Hermes should use in each environment (OmniRoute, a funded OpenRouter account, or another provider) — an operational/config decision, not a code change, now that the mechanism exists.
- Wiring `AgentRun.Model` end-to-end (API → `CreateAgentRunCommand` → `AgentRun.Model`) so a caller can select a model per run without bypassing the repository directly, as this phase's test harness had to.
