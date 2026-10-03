# Phase M report — Execution reliability, credential isolation, Claude Code & Codex adapters

Date: 2026-10-02. Source of truth: `bench/archon-vs-factory/PROTOCOL.md`. Evidence labels: VERIFIED_FROM_SOURCE,
VERIFIED_BY_TEST, VERIFIED_LIVE, ENVIRONMENT_DEPENDENT, NOT_VERIFIED. Budget: zero spend (no paid model calls).

## A. Baseline

| Item | Value |
|---|---|
| Build (as found) | **Failed**: 29 errors. A stray benchmark workspace `apps/src/AI.Factory.Api/projects/factory-t2-r1/` (default Project.RootPath resolved under the Api source dir) compiled into the Api. Fixed by excluding `projects/**` from Api/Worker compile items; the directory itself was left in place (untracked; remove it at your discretion). |
| Build after that fix | 0 errors, 0 warnings |
| Tests | Api 314, Application 1194, Worker 146 = **1654 passed**, 0 failed, 0 skipped (live suites are opt-in, early-return) |
| Runtime inventory | Hermes image `nousresearch/hermes-agent:0.20.6`; OpenHands `agent-server:1.49.4-python`; Claude Code **2.1.121**; Codex CLI **0.160.0** (installed this phase via npm, with your approval); Docker 29.7.2; .NET SDK 10.0.401 |
| Adapters registered | Hermes, OpenHands |

## B. Defects fixed

| # | Problem (benchmark) | Root cause | Fix (main files) | Regression test | Evidence |
|---|---|---|---|---|---|
| 1 | factory-T1-r2: task `TimedOut`, `attemptCount 0`, project idle in Implementation, empty `currentError` for 25 min | Bench DB showed AgentRun TimedOut → WorkItem Failed (error set), but `ProjectCompletionGate` only advanced when *all* WorkItems were Completed; a terminal Failed WorkItem left the Project stuck. No runtime retry existed; `AttemptCount` was never written. | `AgentRunWorkItemSync`: every runtime failure consumes one attempt of `AttemptCount/MaxAttempts`; Timeout/Infrastructure → `Ready` (new `Running→Ready` transition) while budget remains, else Failed with reason + Retry audit row. `ProjectCompletionGate`: terminal Failed/Cancelled WorkItem fails the Project closed with its reason; Blocked is surfaced on `CurrentError`. `TaskDispatchScheduler` stamps `Attempt`. | `AgentRunWorkItemSyncTests` (retry, exhausted, policy forbids, orphan retry, non-retryable, cancelled, idempotent), `ProjectCompletionGateTests` (dead ends) | VERIFIED_BY_TEST; bench DB VERIFIED_FROM_SOURCE |
| 2 | factory-T3-r1: crashed Worker's AgentRun stayed Running, container worked the workspace ~15 min next to the new run | AgentRun orphan detection used the 15-min `StaleExecutionTimeout`; WorkItem recovery moved it to Repairing while the old container lived; Api startup recovered *every* Running WorkItem. | `AgentRunOrphanRecovery` (heartbeat-based, `OrphanHeartbeatTimeout` 2 min, sweep 1 min): conditional mark under concurrency token → terminate runtime via every `IContainerLifecycleManager` → WorkItem retry; per-candidate isolation. New repo methods `GetRunningWithoutHeartbeatSinceAsync`/`TryRecoverOrphanedRunAsync`. `LocalProcessLifecycleManager` reaps crashed CLI process trees (pid + start time). Api startup now heartbeat-bounded. | `ExecutionReliabilityTests` (crash order, live run untouched, repeated restarts, overlapping workers, heartbeat race, isolation), `CredentialIsolationAndReaperTests` (real processes killed, recycled-pid guard, sweep) | VERIFIED_BY_TEST |
| 3 | Worker crashes under Development: captive scoped `IAuditLogger` in singleton `OrchestrationRunWorker` | Constructor-injected scoped service | Per-call scope in `OrchestrationRunWorker.AuditAsync` | `WorkerScopeValidationTests` (ValidateScopes+ValidateOnBuild) — **fails on the old code with "Cannot consume scoped service 'IAuditLogger'"**, passes now | VERIFIED_BY_TEST |
| 4 | Hermes used `~/.hermes/.env` key instead of the Factory key | Factory mounted `~/.hermes` (default `DataVolume`) as `HERMES_HOME`; Hermes `env_loader` loads `$HERMES_HOME/.env` with `override=True` (read in the image) | `HERMES_HOME` is a per-container anonymous volume (`-v /opt/data`, removed by `--rm`/`rm -f -v`); `HermesOptions.DataVolume` deleted; a missing Factory credential refuses to launch | `CredentialIsolationAndReaperTests`; live `HermesCredentialPrecedenceLiveTests` | **VERIFIED_LIVE** (zero cost, local mock provider): old mount style → provider saw the AMBIENT key; Phase M executor → only the FACTORY key |
| 5 | No token/cost data in audit | Runtime report fields discarded | `ExecutionRuntimeReport` threaded to AgentRun metadata (session, cost+source, request id, version, turns, timestamps); Hermes report's `session_id`, `estimated_cost_usd`, `api_calls` now kept | `RuntimeTelemetryPersistenceTests`, adapter tests | VERIFIED_BY_TEST |
| 6 | Missing/incomplete runtime output as false success | (Hermes already fixed in Phase L) | New adapters require the CLI's own structured success + exit 0; malformed/missing output fails | `CliExecutionAdapterTests` | VERIFIED_BY_TEST |
| 7/8 | Stray files, out-of-scope rewrites (T1-r2 `dotnet-install.sh`; T2-r1 csproj rewrite, xunit downgrade, `.slnx`, `BUILD_SUMMARY.txt`) | No Factory-side check | `WorkspaceIntegrityPolicy` + `GitWorkspaceIntegrityInspector` inside `VerificationGate` → repairable verification failure naming each violation | `WorkspaceIntegrityTests` (policy + real git repo replaying T1/T2), `VerificationGateTests` | VERIFIED_BY_TEST |
| — | Fresh DB without AgentDefinitions loops silently | Planner `ValidationException` thrown every cycle | Fails closed with actionable message; bootstrap registers definitions | `ProjectLifecycleOrchestratorTests` | VERIFIED_BY_TEST |
| — | Late completion could overwrite Cancelled/recovered | `AgentRunRepository.UpdateAsync` merged every concurrency conflict | Conflict rethrown when the other writer changed Status; orchestrator exits cleanly | `ExecutionReliabilityTests` | VERIFIED_BY_TEST |
| — | Base config not runnable; `/start` vs `plan/generate` unclear | — | Development overlays, `scripts/bootstrap-factory.ps1`, 409 message names `/start` | script parse + dry run | VERIFIED_LIVE (dry run) |

An independent review of the Phase M diff found 8 issues (stdin write without timeout, recovery cleanup before claim,
unhandled lifecycle-race exception, integrity gate failing open, unhardened git, child leak after start, crashing
output callback, uncaught `AggregateException`). All were fixed with regression tests. Fixing the git hardening
surfaced one more real bug (dropping git's system config lost `core.autocrlf`, so CRLF checkouts read as modified);
the golden lifecycle E2E caught it and it is fixed (three consecutive green re-runs).

## C. New adapters

| | Claude Code | Codex |
|---|---|---|
| CLI version | 2.1.121 (verified `--help`, live probes) | 0.160.0 (verified `exec --help`, live probes) |
| Invocation | `claude -p --output-format stream-json --verbose --model M --session-id {AgentRunId} --no-session-persistence --permission-mode dontAsk --setting-sources "" --strict-mcp-config --disable-slash-commands [--bare] --allowedTools …`, prompt on stdin | `codex exec --json --ephemeral --skip-git-repo-check --ignore-user-config --ignore-rules --color never -c approval_policy="never" -s workspace-write -m M -C {ws} -`, prompt on stdin |
| Authentication | `ANTHROPIC_API_KEY` (+`--bare`) or `CLAUDE_CODE_OAUTH_TOKEN` | `CODEX_API_KEY` (from secret `OPENAI_API_KEY`) |
| Credential precedence | Factory secret only, child env only; isolated `CLAUDE_CONFIG_DIR`/HOME. **VERIFIED_LIVE**: isolated run answers "Not logged in" on a logged-in machine; with an invalid injected key → `401 authentication_failed` | Factory secret only; isolated `CODEX_HOME`/HOME. **VERIFIED_LIVE**: OpenAI rejected exactly the injected probe key (`sk-inval****robe`) |
| Workspace isolation | cwd = Factory workspace; runtime state in `{StateRoot}/{runId}` (outside the workspace, overlap refused); VERIFIED_LIVE that the workspace stayed untouched in probes | same |
| Output parsing | `result` event (`is_error`, `subtype`, `usage`, `total_cost_usd`, `num_turns`, `session_id`), `init` model; early abort on `api_retry` 401/403 | `thread.started`, `item.completed` (agent_message/error), `error`, `turn.failed`, `turn.completed.usage`; early abort on 401 |
| Session | session id = AgentRun id; resume not implemented (retry = new AgentRun) | `thread_id` recorded; `--ephemeral`, no resume |
| Usage / cost | usage + `total_cost_usd` when present | usage when present; cost not exposed (null) |
| Cancellation / timeout / cleanup | Factory deadline, tree kill, sandbox deletion; crash reaper | same |
| Live generation | **NOT_VERIFIED** — no API key; subscription token requires your `claude setup-token` | **NOT_VERIFIED** — no OpenAI account |
| Limitations | credential visible to agent-spawned subprocesses (`Bash(dotnet *)` runs agent code); private NuGet feeds not visible | OpenAI model only (benchmark confounder); Windows needs native `codex.exe` path |

## D. Regression (final)

| Project | Total | Passed | Failed | Skipped |
|---|---|---|---|---|
| Api.Tests | 314 | 314 | 0 | 0 |
| Application.Tests | 1314 | 1314 | 0 | 0 |
| Worker.Tests | 151 | 151 | 0 | 0 |
| **Total** | **1779** (baseline 1654) | **1779** | 0 | 0 |

Build warnings: 0. Commands: `dotnet build apps/AI.Factory.slnx`, `dotnet test apps/AI.Factory.slnx --no-build`.

## E. Live evidence

**VERIFIED LIVE**
- Hermes credential precedence (two credentials, real container, local mock provider, zero cost) — before the review
  fixes. A re-run after the process-runner changes was blocked: Docker Desktop's engine did not start after the
  machine rebooted (ENVIRONMENT_DEPENDENT); the same paths are covered by deterministic tests.
- Claude Code and Codex isolation + credential injection (invalid-key probes, zero cost).
- Bootstrap script dry run.

**BLOCKED EXTERNALLY** — LIMITED — EXTERNAL GATEWAY/AUTH CONSTRAINT
- Claude Code live smoke: no `ANTHROPIC_API_KEY`; a subscription token needs `claude setup-token` by you.
- Codex live smoke: no OpenAI account.
- Hermes/OpenHands live smoke and benchmark rerun: zero-spend budget.

**NOT VERIFIED**: any Claude Code / Codex code generation through the Factory.

To run the Claude Code smoke yourself (uses subscription quota, no API spend), in your own terminal:

```powershell
claude setup-token
$env:CLAUDE_CODE_OAUTH_TOKEN = '<token printed above>'; $env:AI_FACTORY_LIVE_SMOKE = 'ClaudeCode'
# [Phase N] A literal OAuth token had been pasted here (as the AI_FACTORY_LIVE_SMOKE value). It was redacted;
# treat that token as exposed and revoke/rotate it.
dotnet test apps/tests/AI.Factory.Worker.Tests --filter CliRuntimeLiveSmokeTests --logger "console;verbosity=detailed"
```

## F. Benchmark

| Config | Hidden pass | Fully correct | Median time | Cost | Interventions | Resume | Audit | Hygiene |
|---|---|---|---|---|---|---|---|---|
| Archon (+Claude Code) | 70/70 | 6/6 | ~6 min | 0 (subscription) | 0 | 1 | 1 | 2 runs left artifacts |
| AI Factory + Hermes (pre-Phase M) | 68/70 | 5/6 | ~16 min | 3.23 USD | 1 | 2 | 2 | 2 runs out of scope |
| AI Factory + OpenHands | NOT RUN | | | | | | | |
| AI Factory + Claude Code | NOT RUN | | | | | | | |
| AI Factory + Codex | NOT RUN | | | | | | | |

The rerun is prepared with one command per runtime (`start-factory.ps1 -Runtime …`, PROTOCOL.md "Phase M rerun");
fixture, prompts, run count, scoring and hidden tests are unchanged. Not executed: zero-spend budget and missing
Anthropic/OpenAI credentials.

## G. Architecture decision

**LIMITED — EXTERNAL CONSTRAINT**

The reliability and security defects the benchmark found are fixed and covered by deterministic tests, the Factory
remains the only control plane, and all four runtimes sit behind the same dispatcher. What is missing is external:
live Claude Code/Codex generation and the benchmark rerun need credentials and spend that this phase did not have.
Phase N should start only after those runs: (1) the Claude Code smoke, (2) the four-runtime benchmark.
