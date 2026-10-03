# Phase N — Live Runtime Certification and Four-Runtime Benchmark

Date: 2026-10-03. Branch `main`, HEAD `ba2ecda` (Phase I commit); the worktree carries the uncommitted Phase J–M work
(111 tracked files modified + untracked files). Nothing was committed, reset, cleaned, pushed or published.

Evidence labels: **VERIFIED BY SOURCE**, **VERIFIED BY TEST EXECUTION**, **VERIFIED LIVE** (the real runtime/binary ran),
**ENVIRONMENT BLOCKED**, **NOT VERIFIED**, **INFERENCE**. "Live" never means "a model generated code" unless stated.

## Result

**LIMITED** — `LIMITED — EXTERNAL RUNTIME CONSTRAINT`.

Neither Claude Code nor Codex could be certified *model-backed*: there is no Claude credential (`ANTHROPIC_API_KEY` /
`claude setup-token` token) and no OpenAI credential on this machine, and the Codex CLI is not installed. Everything that
does not need a model was certified against the **real** binaries/containers, one shared-infrastructure defect was found
and fixed, and the four-runtime benchmark was **not run** (its precondition — two live smokes — is not met; no run was faked).

## N0 — Baseline

| Item | Value |
|---|---|
| Branch / commit | `main` / `ba2ecda84bf71284e1c791d64982f49f1f873289` |
| Worktree at start | 140 entries (111 tracked modified, +2978/−4059 lines, plus untracked) — all Phase J–M, preserved |
| Build (`dotnet build apps/AI.Factory.slnx`) | **0 warnings, 0 errors** (before and after) |
| Tests at start | Api 314 + Application 1314 + Worker 151 = **1779 passed**, 0 failed, 0 skipped (solution run + standalone Worker run) |
| Tests at end | Api 314 + Application **1358** + Worker 151 = **1823** (+44 new); green in full run 1, one timing-sensitive failure in each of runs 2–4 — see the run history in N19 |
| Anomaly | One full-solution run reported Worker.Tests = 44 tests / 6 s. Not reproducible in the 3 later full Worker runs (standalone and solution). Cause **NOT VERIFIED**; logs kept for later runs. |

Caveats on what "passed" means in the default run:

- The opt-in live tests (`CliRuntimeLiveSmokeTests`, `ClaudeCodeLiveContractTests`, `HermesCredentialPrecedenceLiveTests`,
  `OpenHandsStateIsolationLiveTests`, the `Real*` E2E suites) return early unless enabled, so they count as *passed* without
  executing. They were executed explicitly where stated below.
- **Some live tests enable themselves.** Tests that call `BenchmarkHarness.TryLoadEnvLive()` (`ReviewExecutionLiveTests`,
  `ModelGatewayLiveTests`, `GatewayLiveAgentRunTests`, `RuntimeBenchmarkE2ETests`) load the gitignored `.env.live` (an OpenRouter
  key) into the process. `ReviewExecutionLiveTests.LiveReview_ExecutesRealModel_ThroughHermesExecutionAdapter` therefore makes a
  **real, billed OpenRouter model call through a real Hermes container** on every default `dotnet test` on this machine.
  This phase executed that test 11 times, so that spend is real though small (not measured). It is existing behaviour; Phase N did not change it.

## N1 — CLI / runtime versions

| Runtime | Version | Evidence |
|---|---|---|
| Hermes | image `nousresearch/hermes-agent:0.20.6` (present locally) | VERIFIED LIVE — container ran in the credential-precedence test |
| OpenHands | `agent-server:1.49.4-python@sha256:9b215f…` (pinned, present locally) | VERIFIED LIVE — container ran in the isolation test |
| Claude Code | **2.1.284** (`%APPDATA%\Claude\claude-code\2.1.284\claude.exe`; 2.1.281 also present; **not on PATH**). Phase M's 2.1.121 is not installed. | VERIFIED LIVE — `--version` and the `init` event (`claude_code_version`) |
| Codex | **not installed** | ENVIRONMENT BLOCKED — no `codex`/`codex.exe` on PATH, in npm global (`@openai/*` absent), app dirs; `~/.codex` holds app data only and **no `auth.json`** |
| Docker | 29.8.0 Desktop, engine usable (`docker info`) | VERIFIED LIVE |
| .NET SDK | 10.0.401 | VERIFIED LIVE |

Claude 2.1.284 vs the adapter: every flag the adapter passes (`-p --output-format stream-json --verbose --model --session-id
--no-session-persistence --permission-mode dontAsk --setting-sources --strict-mcp-config --disable-slash-commands --bare
--max-budget-usd --allowedTools`) is present in `claude --help` and was accepted by the real binary. **No adapter syntax
change was needed.** The CLI was not updated. New in 2.1.284 (unused, not needed): `--restricted`, `--safe-mode`,
`--permission-prompts none`, `claude auth …`.

## N2 — Authentication

| Runtime | Method | Credential source | Isolation | Result |
|---|---|---|---|---|
| Claude Code | `ANTHROPIC_API_KEY` (adapter adds `--bare`: auth strictly that key) **or** `CLAUDE_CODE_OAUTH_TOKEN` (`claude setup-token`, subscription). `claude auth login` (ambient login) is never used. | Factory `ISecretProvider` → child env only | per-run `HOME`/`USERPROFILE`/`APPDATA`/`TEMP`/`CLAUDE_CONFIG_DIR` under `{StateRoot}/{runId}/home` | **VERIFIED LIVE** with synthetic credentials, both modes: the provider endpoint saw exactly the Factory credential and no ambient one (ambient `ANTHROPIC_API_KEY`, `CLAUDE_CODE_OAUTH_TOKEN` and the user's real `~/.claude` were present). Real-credential authentication **NOT VERIFIED**. |
| Codex | `CODEX_API_KEY` from secret `OPENAI_API_KEY` (adapter design); ChatGPT-plan/`CODEX_ACCESS_TOKEN` flows were **not inspectable** — CLI not installed | Factory secret | `CODEX_HOME`/`HOME`, `--ignore-user-config`, `--ignore-rules`, `--ephemeral` | ENVIRONMENT BLOCKED live; VERIFIED BY TEST EXECUTION for routing/isolation with a real process (below) |
| Hermes | `OPENROUTER_API_KEY` via env by name | Factory secret | `HERMES_HOME` = per-container anonymous volume | **VERIFIED LIVE** (local mock provider, zero cost): old mount style → the **AMBIENT** key reached the provider; Phase M executor → only the **FACTORY** key |
| OpenHands | gateway API key → conversation request to the container-local Agent Server; docker argv carries only variable names | Factory secret | container-local state, workspace mount | VERIFIED BY SOURCE + TEST; container run VERIFIED LIVE with a fake key (no provider reachable) |

No credential value appears in this document. A real-looking OAuth token **was found in `docs/phase-m-report.md`** — see N17.

## N3 — Claude Code live

| Field | Value |
|---|---|
| Real CLI? | **Yes** — 2.1.284 via the real adapter and real `SystemProcessRunner` |
| Real model? | **No.** Provider = local scripted endpoint (`ANTHROPIC_BASE_URL`). **Model generation NOT VERIFIED.** |
| Auth mode | synthetic `ANTHROPIC_API_KEY` (`--bare`) and synthetic `CLAUDE_CODE_OAUTH_TOKEN` |
| Scripted task | create `FACTORY_CLAUDE_SMOKE.txt` = `AI_FACTORY_CLAUDE_SMOKE_OK` via the CLI's own file tool → **file created, exact content, only file in the workspace** |
| Terminal result | `Succeeded` (exit 0, real `result` event: `subtype success`, `is_error false`) in ~1 s |
| Session | `session_id` = the AgentRun id (the CLI echoes `--session-id`; not a provider-generated id) |
| Usage / cost | 240 in / 39 out tokens and `total_cost_usd 0.000435` — **computed by the CLI from the scripted stream; not real usage or spend** |
| Reported model | `claude-haiku-4-5` from `init` (the configured `--model`; "actually served by Anthropic" NOT VERIFIED) |
| 401 | first `api_retry` (401, `max_retries 10`) → run stopped; **1.0 s (OAuth) / 3.2 s (API key)**, 1 request, `Failed / Provider / Authentication`. Also observed against the real `api.anthropic.com` with a synthetic key: `api_retry` 401 at 1.4 s. |
| Timeout | Factory deadline 25 s → `TimedOut`, real process tree (claude + descendants sampled) gone, state dir removed |
| Cancellation | cancelled mid-request → `Cancelled`, tree gone, state dir removed |
| Factory verification / acceptance | **NOT VERIFIED** — needs the credentialed `CliRuntimeLiveSmokeTests` (full Factory path) |
| Cleanup | per-run state removed on every path (VERIFIED LIVE) |

Test: `ClaudeCodeLiveContractTests` (5 tests, opt-in, labelled as not model generation).

## N4 — Codex live

**ENVIRONMENT BLOCKED** — CLI not installed, no OpenAI credential. Not installed during this phase (a download needs your approval,
and an install without an account would add no live evidence). Codex is certified only at the adapter/process level (N7, N8).
CLI-schema compatibility of the `codex exec --json` event parser with the current Codex release: **NOT VERIFIED**.

## N5 — Hermes revalidation (post-Phase-M)

Docker is usable. `HermesCredentialPrecedenceLiveTests` 2/2 passed in 4 of 4 executions (against the Phase M
process-runner, and again with the Phase N runner fix): old mount style → AMBIENT key seen; Phase M executor → FACTORY key only;
containers removed. **VERIFIED LIVE.** The mock provider answers 401 by design, so that test involves no model.

**Model-backed Hermes, also VERIFIED LIVE (incidental):** `ReviewExecutionLiveTests.LiveReview_ExecutesRealModel_ThroughHermesExecutionAdapter`
(see N0) ran a real review (`deepseek/deepseek-v4-flash-0731` via OpenRouter, real Hermes container, `HermesExecutionAdapter`, Factory
credential) and returned a real verdict (`BlocksCompletion=False`, 1 finding) with 0 containers left. It passed in 10 of 11 recorded
executions across the Phase M runner and the Phase N runner (incl. 2 of 2 alone, 61–128 s); the one failure is F7. This is a
**Review** run, not an Implementation run: Hermes *coding* generation and the Hermes benchmark were not rerun.

## N6 — OpenHands revalidation (post-Phase-M)

`OpenHandsStateIsolationLiveTests` (two concurrent real Agent Server containers, fake key, no provider): workspace has no
`conversations/`/`bash_events/`, `git status` clean, artifacts = project files only, key not in any workspace file, no
container left. Results: **passed alone (1/1)**; with the Hermes tests running in parallel **failed 2 of 4**
(`TimedOut`: the Agent Server missed the 45 s startup-health limit while Docker was loaded — Hermes' own test took ~1m50s
in those runs vs ~47 s in the passing ones). Passed in the remaining 2 parallel runs, once with the Phase M runner and once with
the Phase N runner. Classified as Docker-infrastructure timing (K), non-blocking, not attributable to the runner change.
VERIFIED LIVE for isolation; **model-backed OpenHands execution NOT VERIFIED** (no spend authorised).

## N7 — Process lifecycle (shared infrastructure)

Proven with **real processes** through the real adapters (`CliProcessTreeLifecycleTests`, fake CLI = `.cmd` → PowerShell that
spawns a real grandchild inheriting the pipes) — **VERIFIED BY TEST EXECUTION**, both Claude and Codex adapters — and, for
Claude, against the real 2.1.284 binary (N3).

| Property | Result |
|---|---|
| stdin | 600 KB UTF-8 prompt delivered byte-exact; CLI that reads to EOF finishes (stdin closed); existing test: 4 MB to a child that never reads still times out |
| stdout / stderr | line observer sees events live; a throwing observer cannot break the run; stderr logged redacted |
| **pipe release** | **DEFECT FOUND AND FIXED** (N18-F1): a finished CLI whose grandchild held the pipes was reported `TimedOut` after the full deadline; now returns the root's result within the 5 s drain grace |
| timeout | deadline 6 s → `TimedOut`, whole tree (cmd → powershell → ping) dead, state removed |
| cancellation | before launch, immediately after launch, during execution → `Cancelled` (never Failed/TimedOut), tree dead, no leftover process or state |
| deterministic 401 | first auth event stops the run (Claude `api_retry` 401, Codex `error` 401), tree killed, `Failed/Provider/Authentication`, well under 30 s |
| orphan recovery | `Reaper_KillsTheOrphanedCliProcessTree_…`, `Reaper_NeverKillsAProcessWhoseStartTimeDoesNotMatch…`, `ExecutionReliabilityTests` (real processes) — passed. A live Worker-kill simulation was not run (**NOT VERIFIED**; deterministic coverage only). |
| leak audit after all runs | 0 stray `ping`/`claude` processes from tests, 0 leftover containers; 2 OpenHands temp dirs + 1 debug dir created by my runs were removed |

## N8 — Credential isolation / leakage matrix

Synthetic secret markers only (`sk-ant-PHASEN-…`). Claude and Codex, real runner + real child process:

| Surface | Result |
|---|---|
| argv | credential absent (recorded by the child's own batch wrapper); ambient markers absent |
| stdin / prompt | absent |
| child environment | Factory credential present under **exactly one** variable (`ANTHROPIC_API_KEY` / `CODEX_API_KEY`); ambient `ANTHROPIC_API_KEY`, `CODEX_API_KEY`, `OPENAI_API_KEY`, `CLAUDE_CODE_OAUTH_TOKEN`, and the host's own `ANTHROPIC_BASE_URL` and 21 `CLAUDE_CODE_*` session variables (present in the test process) absent |
| outcome (output/error/report) | CLI-echoed secret replaced by `[REDACTED]` |
| logs | secret printed to stderr by the child is logged redacted |
| workspace | unchanged file set |
| artifacts | adapters record none (source) |
| runtime home / temp | deleted on every path |
| process list | argv covers it (credential never on the command line) |
| **Documented exception** | the credential is in the CLI child's environment by design; an agent-spawned `Bash(dotnet *)` subprocess inherits it (Phase M limitation, unchanged). Codex strips `*KEY*` from its shells. |

Hermes: `Hermes_FactoryKeyReachesTheContainerOnlyByName_NeverOnTheCommandLine` (test) + the live proof (N5). OpenHands: argv by
name only (source), error text echoing the key is redacted (`ExecuteAsync_AgentServerErrorEchoingTheApiKey_IsRedactedAndBounded`).

## N9 — Workspace integrity

Real Claude run (scripted endpoint): workspace delta = the one intended file; no `.claude`, session, credential or cache file in
the workspace. `WorkspaceIntegrityTests` (12) pass. Dependency/manifest scope-creep (N35) on real model runs: **NOT VERIFIED**
(no model-backed runs).

## N10 — Usage / cost availability

| Runtime | Tokens | Cost | Evidence |
|---|---|---|---|
| Hermes | when its report has them | Hermes' own *estimate* | source/tests; not exercised live (no model) |
| OpenHands | when reported | not exposed | source/tests; not exercised live |
| Claude Code | `result.usage` | `total_cost_usd` | real result event schema observed (values scripted) |
| Codex | `turn.completed.usage` | not exposed | source/tests only (CLI absent) |

Absent telemetry stays null/`NOT AVAILABLE`; nothing is zero-filled or estimated.

## N11 — Session / request correlation

| Runtime | Session/Thread ID | Upstream request ID |
|---|---|---|
| Hermes | AVAILABLE in run report (source) — NOT VERIFIED live | NOT VERIFIED |
| OpenHands | AVAILABLE (conversation id, source) — NOT VERIFIED live | NOT VERIFIED |
| Claude Code | AVAILABLE — `session_id` in `init`/`result` (echo of the Factory `--session-id`); VERIFIED LIVE | NOT AVAILABLE in the stream (NOT VERIFIED beyond that) |
| Codex | `thread_id` (source) — NOT VERIFIED live | only when printed (`request id: req_…`) — NOT VERIFIED |

An AgentRunId is not an upstream request id; the Claude session id equals the AgentRunId by construction and is recorded as such.

## N12 — Profile resolution

`CliRuntimeProfileResolutionTests` (17) — VERIFIED BY TEST EXECUTION: `ClaudeCode → ClaudeCode`, `Codex → Codex`, `Hermes`, `OpenHands`
each resolve to exactly their adapter; mapping `Implementation` to a CLI profile changes nothing for Repair, Review, Planner,
unmapped types or the default (Hermes); declaring the profiles without mapping them changes nothing; disabled or unregistered
adapters fail closed and never resolve another runtime; a failing/timing-out/throwing CLI runtime is never retried on another adapter by
the dispatcher; no `Fallback/Failover/Secondary/Backup` option exists. Production defaults were not touched.

## N13 — Four-runtime benchmark

**NOT RUN — `LIMITED — RUNTIME EXTERNAL CONSTRAINT`.** Precondition N25 unmet: Claude Code and Codex live smokes did not pass
(no credentials; Codex not installed). Executed runs: Hermes 0, OpenHands 0, Claude Code 0, Codex 0 — **none of the 12 runs
were executed**. No benchmark framework was created; `bench/archon-vs-factory/PROTOCOL.md` and the harness are untouched. The only
benchmark data in the repository remains the historical Phase F/G/M evidence (Archon + Claude Code, pre-Phase-M Hermes), unchanged and not newly observed.
Any future Claude Code/Codex run must be classified `NON-EQUIVALENT MODEL ROUTING` (provider-native models) unless routed equally.

## N14–N16 — Functional comparison, reliability, performance

No benchmark data exists, so there is nothing to compare, count or rank. Measured mechanics only (N3/N7): 401 abort 1.0–3.2 s
(the CLI's own loop is 10 retries with backoff); Factory deadline honoured within ~1 s plus process start; cancellation ≤ 3 s.

## N17 — Security

- **Exposed OAuth token (action required).** `docs/phase-m-report.md` line 84 contained a literal `sk-ant-oat01-…` token (as the value of
  `AI_FACTORY_LIVE_SMOKE`). The file is untracked, so it was never committed; the token was redacted in the file. **Treat the token as exposed and revoke/rotate
  it.** It was not used for any test.
- Ambient session variables (`ANTHROPIC_BASE_URL`, `CLAUDE_CODE_*`) exist in this developer environment; none reach a CLI child (allow-list) — asserted by `SyntheticSecret_ReachesOnlyTheChildEnvironment…` with a real child process.
- Known, unchanged: a re-parented grandchild is not found by the tree walk; a descendant lingering after a normal exit is not killed.

## N18 — Failure analysis

| # | Run / runtime | Class | Command & error | Repro | Evidence | Impact | Blocking | Action |
|---|---|---|---|---|---|---|---|---|
| F1 | `FinishedCli_WithALingeringGrandchildHoldingThePipes…` / Claude (shared runner) | **F** process lifecycle | CLI exits 0 with a success result; its grandchild holds stdout; `RunAsync` waited for pipe EOF until the 45 s deadline → `TimedOut` | 1/1 (before fix) | `tree-1.log` red, now green; runner unit test added | a finished run misreported TimedOut and would consume a retry attempt; affects every CLI runtime | **Fixed** | `SystemProcessRunner`: observe root exit, 5 s bounded drain |
| F2 | `OpenHandsStateIsolationLiveTests` / OpenHands | **K** Docker/infrastructure | `Expected Failed, Actual TimedOut` — Agent Server missed the 45 s startup limit under parallel Docker load | 2/4 parallel, 0/1 alone | logs; Hermes test took 2.4× longer in failing runs | live suite flaky on a loaded host | non-blocking | run Docker live suites serially; consider a larger `StartupTimeoutSeconds` (not changed) |
| F3 | one solution test run, Worker.Tests = 44 | **N** unknown | totals 44/6 s once | 1/4 | log not retained for that run | none observed | non-blocking | keep logs of each run; investigate if it recurs |
| F4 | Claude Code live smoke | **O** external account | no credential | — | — | live generation uncertified | blocks the benchmark | user: `claude setup-token`, run the smoke (`docs/local-setup.md`) |
| F5 | Codex live smoke | **L/O** environment + account | CLI not installed, no account | — | search of PATH/npm/app dirs | live generation uncertified | blocks the benchmark | user: install Codex and provide a credential |
| F6 | `DockerLifecycleIntegrationTests.Cancellation_CleansUpContainer_LeavingZeroContainers` / Hermes (Docker) | **N** pre-existing (+K) | cancel at 500 ms → `docker rm -f` finds no container yet; the daemon then creates it; test sees `ai-factory-agent-<id>` 1.5 s later | 2/2 in full runs 4+worker-timed; 6/6 alone for a while, then 5/6 pass later; **same on the old runner** (A/B) | `dockerlife-*.log`, `ab-NEW/OLD-*.log` | a run cancelled within ~hundreds of ms of launch can leave its container running (the agent keeps editing the mounted workspace) until Hermes ends or `--rm` removes it; real lifecycle weakness on Docker runtimes | non-blocking for Phase N; **recommended fix** | in `CleanupContainerAsync` (Hermes/OpenHands): when the run ended by cancel/timeout, retry `rm -f` for a short bounded window; or `create`+`start` separately. **Not changed** (outside this phase's no-new-mechanism rule). |
| F7 | `ReviewExecutionLiveTests.LiveReview…` / Hermes | **K** Docker/infrastructure | review failed after 20 s with the container's s6-overlay start-up text as the error; `Post-execution container count: 1` (label count; container gone moments later) | 1/11 | `final-2.log`; 2/2 pass alone | one flaky live test | non-blocking | none |
| F8 | `GoldenProjectLifecycleE2ETests.GoldenPath…` | **L** environment load | `TimeoutException` waiting for a WorkItem in `Testing` (90 s budget; the test needs ~56 s) while the host ran ~2× slower | 1 full run; 3/3 pass alone (55–56 s) | `final-3.log`, `golden-*.log` | flaky E2E | non-blocking | none |

## N19 — Regression

| Check | Result |
|---|---|
| format (`dotnet format --verify-no-changes`, touched files) | clean (whitespace applied to my 3 new files) |
| build | 0 warnings, 0 errors |
| full suite | **not consistently green on this host** — see the run history below |
| focused (real-process lifecycle 21, SystemProcessRunner 9, `CliExecutionAdapterTests`, reaper/credential isolation, profile 17, workspace integrity, architecture, composition-root) | all passed (92 in the combined lifecycle/runner/adapter/reaper filter, plus the full suite) |
| live (opt-in, explicit) | Claude contract 5/5; Hermes credential 2/2; OpenHands isolation per N6 |

Full-solution run history (`dotnet test apps/AI.Factory.slnx --no-build`), in order:

| Run | Code state | Result |
|---|---|---|
| baseline #1 | as found | **anomaly**: Api 314 / Application 1314 / Worker **44** (F3) |
| baseline #2 | as found | 1779/1779 green |
| 1 | + Phase N | **1823/1823 green** |
| 2 | + Phase N | 1 failure: `LiveReview…` (F7) |
| 3 | + Phase N | 1 failure: `GoldenProjectLifecycleE2ETests` (F8) |
| 4 | + Phase N | 1 failure: `DockerLifecycleIntegrationTests.Cancellation…` (F6) |
| 5, 6 | + Phase N, **excluding** `CliProcessTreeLifecycleTests` | 1802/1802 green, twice |

Each failing test passed when re-run alone (F7 2/2, F8 3/3) except F6, which is a pre-existing Docker race that fails on the old
runner too (A/B: new runner 5/6 and old runner 4/6 pass, plus 2/3 old alone). Runs 5–6 vs 2–4 hint that the added real-process
test class (~25 s, ~25 PowerShell starts, run in parallel with the Worker host) adds contention on a host with only 2.9 of 15.8 GB RAM
free; with 2 vs 3 flaky runs this is **INFERENCE, not proven**. Honest summary: the Phase N code is green when the host is quiet,
and three timing-sensitive Docker/E2E tests flake when it is not. Do not read 1823/1823 as a stable result.

## N20 / N21 / N22 — API, database, Flutter

`NO PUBLIC API CHANGE` · `NO SCHEMA CHANGE` (no migration or entity files touched; none added) · `NO CHANGE` (Flutter untouched).
Acceptance, Review, HumanReview, Approval, Project completion and production profiles/defaults: untouched.

## N23 — Evidence classification

- **VERIFIED BY SOURCE:** adapter argument sets; credential only via env; Codex/OpenHands/Hermes telemetry fields; absence of fallback options.
- **VERIFIED BY TEST EXECUTION:** 1823 tests incl. real-process lifecycle (timeout, 3 cancellation points, tree kill, stdin, pipe release, 401 abort, leakage matrix), reaper, profiles.
- **VERIFIED LIVE:** Claude Code 2.1.284 accepts the adapter invocation; credential routing in API-key and OAuth modes; 401 abort; timeout; cancellation; real `result`/`init`/`api_retry` schemas (scripted endpoint, no model); real `api.anthropic.com` 401 with a synthetic key; Hermes credential precedence; OpenHands container isolation (with the load caveat).
- **ENVIRONMENT BLOCKED:** Codex CLI/account; Claude credential.
- **NOT VERIFIED:** any model-backed Claude Code / Codex / Hermes / OpenHands generation in Phase N; full Factory path (verification + acceptance) for Claude/Codex; Codex event schema vs its current release; real usage/cost values; live Worker-kill orphan simulation; dependency scope-creep on real model runs; the 12-run benchmark.
- **INFERENCE:** the OpenHands timeout is Docker load (the A/B and Hermes durations point there, not a proof).

## N24 — Known limitations

1. No model-backed certification of Claude Code, Codex, Hermes or OpenHands in Phase N.
2. Codex adapter's JSONL reader is validated only against recorded 0.160.0 shapes (Phase M) — CLI absent now.
3. `--bare`/API-key runs expose Bash/Edit/Read only; file creation uses `Edit`. A real model's behaviour with that toolset is unverified.
4. Process-tree kill walks the tree at kill time; a descendant that lingers after a normal exit, or is re-parented, is not killed.
5. Opt-in live tests count as passed in the default run.
6. OpenHands live isolation test is timing-sensitive on a loaded Docker host (45 s startup limit).
7. The default suite is timing-sensitive on a memory-constrained Docker host (F6–F8) and makes billed OpenRouter calls when `.env.live` exists (N0).
8. Docker runtimes: cancellation within ~hundreds of ms of launch can briefly leave the container alive (F6).
9. The scripted-endpoint tests need Windows (PowerShell/`cmd`) and the installed `claude.exe`; the real-process lifecycle tests are Windows-only (they return early elsewhere).

## N25 — Documentation

Updated: `docs/phase-n-report.md` (this file), `docs/execution-runtimes.md` (2.1.284 re-verification, pipe-release guarantee,
live-test table, limitations), `docs/local-setup.md` (Claude Code location, offline contract test, credentialed smoke),
`docs/phase-m-report.md` (token redacted). Source comment in `ClaudeCodeExecutionAdapter` notes the 2.1.284 re-verification.

## Changes made in Phase N

| File | Change |
|---|---|
| `apps/src/AI.Factory.Infrastructure/Agents/SystemProcessRunner.cs` | observe the root's exit via `Exited`, then a bounded 5 s pipe drain (fix F1) |
| `apps/tests/…/SystemProcessRunnerTests.cs` | +1 test (descendant holding pipes) |
| `apps/tests/…/Execution/CliProcessTreeLifecycleTests.cs` (new) | 21 real-process adapter lifecycle tests |
| `apps/tests/…/Execution/ClaudeCodeLiveContractTests.cs` (new) | 5 opt-in tests, real claude.exe + scripted endpoint |
| `apps/tests/…/Execution/CliRuntimeProfileResolutionTests.cs` (new) | 17 profile/no-fallback tests |
| docs, one adapter comment | as above |

## Recommended next steps (yours, they need credentials/approval)

1. Rotate the exposed token (N17).
2. `claude setup-token`; run `CliRuntimeLiveSmokeTests` with `AI_FACTORY_LIVE_SMOKE=ClaudeCode` (`docs/local-setup.md`) — that closes N3's full-Factory/verification/acceptance gap.
3. Decide on Codex: install + credential, or drop it from the benchmark scope.
4. Only then run the 12-run benchmark per `PROTOCOL.md`.

## Final gate

`LIMITED — EXTERNAL RUNTIME CONSTRAINT`
