# Phase P — Model-Backed Runtime Certification & Evidence Closure

Date: 2026-10-03. This report covers the **resumed** Phase P execution and replaces the earlier gated report in this file. The
earlier execution (same day) stopped at P1 and made no model call; its non-model Claude Code contract result (5/5 on 2.1.286)
is cited below where relevant and marked as such.

Evidence classes: `VERIFIED BY SOURCE`, `VERIFIED BY TEST EXECUTION`, `VERIFIED LIVE`, `ENVIRONMENT BLOCKED`, `NOT VERIFIED`,
`INFERENCE`. Raw evidence (console logs, TRX, per-run `CERT` evidence lines) is in the session scratchpad, not in the repository.

## 1. Executive result

**`BLOCKED — FACTORY CERTIFICATION DEFECT`**

- **Hermes and OpenHands generated real code through the real Factory path with a real model** (`VERIFIED LIVE`). For each,
  the change was correct, verification and acceptance passed, cleanup left 0 Factory containers, and no credential
  reached the workspace, the logs or any process command line.
- The certification also exposed Factory defects:
  - **D-P1 (fixed, regression-tested, certification repeated):** an exit-0 run whose own report said `Failed` went
    through verification to `Completed`.
  - **D-P2 (recorded, not fixed by owner decision):** the independent review is not effective. The reviewer gets only
    file paths, and Hermes' real findings are discarded by a whole-output JSON parse. A Completed WorkItem therefore
    carries a "review" that never happened.
  - **G-P3 (gap):** nothing in the Factory detects an unrequested file. The OpenHands repeat left a stray untracked
    file and was still accepted.
- Repeatability:
  - OpenHands: the repeat on the fixed code **failed** the certification (stray file).
  - Hermes: one passing run; no repeat was taken (see §14).
- Claude Code: `ENVIRONMENT BLOCKED — CLAUDE CREDENTIAL`. Codex: `ENVIRONMENT BLOCKED — CODEX CLI NOT INSTALLED`.
- No runtime is certified.

## 2. Current baseline

| Item | Observation | Class |
|---|---|---|
| Commit / branch | `0d2ab9c9c5cdb7c0345ab3e19f1eb7e09b347f13` on `main` | `VERIFIED LIVE` |
| Uncommitted entries at resume | 53: Phase O's 52 + the earlier `docs/phase-p-report.md` | `VERIFIED LIVE` |
| Factory processes | The compose stack's `ai-factory-worker-1` was running with the Docker socket mounted and a 1-minute orphan sweep on the same labels. The owner stopped it at my request before any run; `ai-factory-api-1` and the standalone `hermes` container stayed up, untouched | `VERIFIED LIVE` |
| Factory agent containers before the first run | 0 | `VERIFIED LIVE` |
| Build | `dotnet build AI.Factory.slnx` — 0 warnings, 0 errors (incremental, before the first billed run); final `--no-incremental` in §16 | `VERIFIED BY TEST EXECUTION` |
| Default non-live tests | earlier in Phase P: 2 × 1856/0/42 with Docker down; final runs §15 | `VERIFIED BY TEST EXECUTION` |

## 3. P1 gate result

| Gate | Result | Class |
|---|---|---|
| Phase N Anthropic token rotation | Owner confirmed in chat that it is revoked/rotated | `OWNER-ASSERTED — TOKEN ROTATION/REVOCATION CONFIRMED` (not independently verified) |
| Docker | Initially unreachable; after the owner started Docker Desktop: Docker 29.8.0 reachable | `VERIFIED LIVE` |
| Claude credential | Owner said it was supplied, but no `ANTHROPIC_API_KEY` / `CLAUDE_CODE_OAUTH_TOKEN` / `ANTHROPIC_AUTH_TOKEN` exists at process, user or machine scope of this session (presence checked by name only, re-checked at the end) | `ENVIRONMENT BLOCKED — CLAUDE CREDENTIAL` |
| Codex | Not on `PATH` or in the usual install directories; no OpenAI/Codex credential | `ENVIRONMENT BLOCKED — CODEX CLI NOT INSTALLED` |
| OpenRouter credential | `OPENROUTER_API_KEY` from the git-ignored `.env.live`, loaded **only inside the opted-in test process** by the Phase O gate; never in this shell, never printed | `VERIFIED BY SOURCE` (`TestGating.cs`), `VERIFIED LIVE` (runs used it) |

## 4. Environment readiness

- **Images:**
  - `nousresearch/hermes-agent:0.20.6` (`sha256:ef2bbf05…`) present.
  - `ghcr.io/openhands/agent-server:1.49.4-python@sha256:9b215fb9…` present. Its digest matches `OpenHands:DockerImage`
    (`VERIFIED LIVE`, `docker inspect` of the running container).
- **Claude Code:** 2.1.284 and 2.1.286 are installed under `%APPDATA%\Claude\claude-code\<version>\<hash>\claude.exe`, not
  on `PATH`.
  - "Without an API key" is already supported: `claude setup-token` → `CLAUDE_CODE_OAUTH_TOKEN`
    (`CliRuntimeOptions.cs:66–98`). The adapter drops `--bare` for it (`ClaudeCodeExecutionAdapter.cs:84`).
  - The setup-token step needs the owner's browser login and was not done. The Factory deliberately does not reuse
    `~/.claude`.
- **Codex:** not installed.

## 5. Runtime routing evidence

Before each billed call, the harness:
- asserted the shipped gateway without overriding it: `ModelGateway:DefaultGateway = OpenRouter`, `Enabled`, `BaseUrl =
  https://openrouter.ai/api/v1`, `ApiKeySecretName = OPENROUTER_API_KEY`;
- asserted the profile `Execution:Profiles:<runtime>` → adapter `<runtime>`, gateway `OpenRouter`;
- mapped every agent type to the runtime under test;
- put **exactly one model** in the catalog and in every routing category, so no fallback candidate exists;
- rejected any `auto` id.

| Runtime | Gateway | Explicit model (persisted `AgentRun.Model`) | Fallback | Class |
|---|---|---|---|---|
| Hermes (run H1) | OpenRouter | `deepseek/deepseek-v4-flash-0731:free` | `FallbackCount=0`, `FallbackUsed=False` | `VERIFIED LIVE` (logs + DB) |
| Hermes (run H2) | OpenRouter | `deepseek/deepseek-v4-flash-0731` (owner's choice after H1's provider rejection) | `FallbackCount=0`, `FallbackUsed=False` | `VERIFIED LIVE` |
| OpenHands (O1–O3) | OpenRouter | `qwen/qwen3-coder` (sent to the agent server as `openrouter/qwen/qwen3-coder`, the documented LiteLLM route format; `OpenRouterGateway.cs:16–32`) | `FallbackCount=0`, `FallbackUsed=False` | `VERIFIED LIVE` / `VERIFIED BY SOURCE` |
| Claude Code, Codex | — | not mapped (`Models: {}`); the adapter rejects an unmapped model | — | `VERIFIED BY SOURCE` |

Fail-closed routing (§5.3) was checked with the existing suites: `ModelGatewayResolverTests`, `ModelGatewayRegistryTests`,
`GatewayOperationalReadinessTests`, `GatewayFailureAgentRunEndToEndTests`, `GatewayBoundaryTests`,
`CliRuntimeProfileResolutionTests` and `ModelRouterTests`. Result: **103/103 passed** (`VERIFIED BY TEST EXECUTION`).

| Case | How it is covered |
|---|---|
| Unknown gateway | rejected |
| Disabled gateway | rejected |
| Unknown category | rejected |
| Route to a gateway Hermes is not configured for | rejected without launching |
| Unmapped CLI model | rejected |

Not rejected by the Factory:
- **Automatic ids.** The shipped catalog still contains `auto/coding:free`; only the harness rejects `auto` ids.
- **An empty model on the Docker runtimes.** Hermes/OpenHands use their configured `DefaultModel`.

Both are `VERIFIED BY SOURCE` and are listed as gaps in §18.

## 6. Hermes certification

**Path.** Real Worker composition root (`Program.ConfigureServices`) → `TaskDispatchScheduler` → `AgentRunWorker` →
`AgentOrchestrator` → profile `Hermes` → `HermesExecutionAdapter` / `HermesAgentExecutor` → container → OpenRouter.
Only the database, workspace root and routing catalog were test-specific.

**Run H1** — 2026-10-03 18:55:56–18:59:30 UTC; model `deepseek/deepseek-v4-flash-0731:free`; AgentRun
`e1f3d2a5-a2b6-4bb9-8d93-eb32268571e5`; WorkItem `c45a6b23-e3f7-432b-9b0f-8b4c0d3dbcfc`.
- Hermes exited with code **2** after 206 s. Its output carried the provider's reason: `HTTP 404: This model is unavailable
  for free. The paid version is available now - use this slug instead: deepseek/deepseek-v4-flash-0731` (`VERIFIED LIVE`).
- AgentRun `Failed` (`Agent`); WorkItem `Failed`, not retried ("Not retryable (Agent)"). No other model was tried. Usage:
  NOT REPORTED.
- Workspace unchanged (`git status` clean). 0 Factory containers. The run's anonymous `/opt/data` volume (`57e2a2ac…`) is
  gone afterwards (`VERIFIED LIVE`).
- Truthfulness note:
  - `AgentRun.ErrorMessage` holds the container's s6 start-up log, not the provider's 404. The 404 is only in the output
    tail.
  - The failure is classified `Agent`, not `Provider`/`Model`.
  - The run is correctly a failure; its reason is recorded imprecisely (§18).
- Model-backed status: **provider rejection** (`ENVIRONMENT BLOCKED` for that free model id).

**Run H2** — 19:42:57–19:53:15 UTC; model `deepseek/deepseek-v4-flash-0731` (paid); AgentRun
`0d2c3850-b5b8-4af4-8d24-f3dc0b2a2187`; WorkItem `874d4b81-b6a6-4f52-aec4-1947f931fae5`.

| Check | Observation | Class |
|---|---|---|
| Runtime | exit 0 in 407 s; AgentRun `Succeeded` with `ExecutionAdapter=Hermes`, `ExitReason=exit:0` | `VERIFIED LIVE` |
| Hermes success semantics | `Succeeded` requires exit 0 + run report present + `failed:false` + `completed:true` (`HermesAgentExecutor.cs:571–633`). The report was read and then deleted by the executor, so its content was not printed | `VERIFIED BY SOURCE`; outcome `VERIFIED LIVE` |
| Real generation | `src/Calculator.cs` gained `Multiply`; `tests/CalculatorTests.cs` gained `Multiply_ReturnsProduct`; both differ from the seed (line-ending-normalized SHA-256) | `VERIFIED LIVE` |
| Changed files | exactly `src/Calculator.cs`, `tests/CalculatorTests.cs` (`git diff` vs seed + `git status --untracked-files=all`) | `VERIFIED LIVE` |
| Verification | Factory gate: build + test passed (`ValidationRun` success/build/tests = true) | `VERIFIED LIVE` |
| Acceptance | criteria `ExpectedTextPresent` ×3 + `TestsPass` satisfied; WorkItem `Running → Testing → Completed`, 0 repairs | `VERIFIED LIVE` |
| Independent check | `dotnet test --filter Multiply_ReturnsProduct`: exactly 1 passed; full suite 2/2 | `VERIFIED LIVE` |
| Review | **not effective** (§10, D-P2) | `NOT VERIFIED` |
| Credential | in the container's environment by design (`-e OPENROUTER_API_KEY` by name, value in the docker CLI's child environment), never in argv | `VERIFIED BY SOURCE` (`HermesAgentExecutor.cs:198–271`); argv `VERIFIED LIVE` (§12) |
| Ambient state | mounts were only the workspace bind and the image's anonymous `/opt/data`; no host `~/.hermes` | `VERIFIED LIVE` (`docker inspect` during H1) |
| Usage | `promptTokens=99,662`, `completionTokens=4,776`, `totalTokens=389,622` — copied verbatim from Hermes' run report; total ≠ sum (Hermes' own figures) | `VERIFIED LIVE` (values), `VERIFIED BY SOURCE` (not computed) |
| Cost | Hermes can report `estimated_cost_usd` → `CostUsd` metadata. The harness did not print metadata for H1/H2 (fixed afterwards) and the run databases are deleted | `NOT VERIFIED` |
| Cleanup | 0 Factory containers immediately and after 15 s; foreign containers unchanged | `VERIFIED LIVE` |

## 7. OpenHands certification

**Path.** Same Worker composition → profile `OpenHands` → `OpenHandsExecutionAdapter` → dedicated agent-server container
→ OpenRouter.

**Credential.** The container environment has no provider key: only `SESSION_API_KEY` and the relocated state paths
`OH_CONVERSATIONS_PATH` / `OH_BASH_EVENTS_DIR`. The adapter resolves the key from the routed gateway's secret and sends it in the
conversation request (`OpenHandsExecutionAdapter.cs:476–507`). Per the Phase L design, the agent server keeps it encrypted in
its container-only state, which is destroyed with the container. Classes: `VERIFIED LIVE` (env names) and
`VERIFIED BY SOURCE`.

| Run | Time (UTC) | AgentRun / WorkItem | Agent's own report | Factory result | Harness checks | Tokens (p/c/t) |
|---|---|---|---|---|---|---|
| O1 (before D-P1 fix) | 19:03:13–19:07:52 | `82c2430c-afce-46d7-b53d-cdf4d6354e90` / `946d520c-9836-4806-8283-7b34fc91af38` | valid envelope, `"status":"Failed"`: "could not verify tests pass due to missing .NET runtime" | `Succeeded` → Testing → **Completed** | all passed — but the WorkItem should have failed: **D-P1** | 123,799 / 1,030 / 124,829 |
| O2 (fixed code) | 19:36:07–19:38:17 | `992eb247-dfac-4272-acbd-f4e5d5752ef8` / `a603711c-9327-42c4-a583-0a172a16a6d7` | `{"execution":{"status":"repair_required",…}}` at the top level — not a valid envelope (no `version`/`status`), so "unusable report" by design | Testing → verification → **Completed** | **all passed** | 179,182 / 1,442 / 180,624 |
| O3 (fixed code, repeat) | 19:39:24–19:41:36 | `62745a30-54b3-469e-8705-b555e5f551b4` / `ce61f696-8f8e-417a-9b8b-b5b5ebf16be7` | same malformed `repair_required` shape | **Completed** | **failed:** the workspace held an extra untracked file in its root, named the single character U+F00D (git: `"\357\200\215"`) — **G-P3** | 232,533 / 1,575 / 234,108 |

In all three runs (`VERIFIED LIVE`):
- **Model and routing:** model `qwen/qwen3-coder`, gateway OpenRouter, no fallback.
- **Workspace content:** both requested changes present; `dotnet test --filter Multiply_ReturnsProduct` exactly 1 passed;
  full suite 2/2.
- **Factory checks:** `ValidationRun` success/build/tests = true.
- **Runtime state:** no `conversations/`, `bash_events/`, `base_state.json`, `.hermes`, `.openhands` in the workspace.
- **Credential:** absent from the workspace and the 28–29 captured log lines.
- **Cleanup:** 0 Factory containers immediately and after 15 s; foreign containers unchanged.

The agent cannot run `dotnet` inside the pinned agent-server image (no .NET SDK there), so it reports that it could not
verify. That self-report varied run to run (`Failed`, then malformed `repair_required`) — model behaviour, `VERIFIED LIVE`.

## 8. Claude Code certification

`ENVIRONMENT BLOCKED — CLAUDE CREDENTIAL`. No model-backed run. Non-model evidence from earlier today (not model
generation): the real 2.1.286 binary passed the 5/5 contract tests (`VERIFIED LIVE — REAL CLI, NON-MODEL BACKED`). The model
map `ClaudeCode:Models` is empty, so a certification run also needs an explicit mapping.

## 9. Codex certification

`ENVIRONMENT BLOCKED — CODEX CLI NOT INSTALLED` (no credential either). The fake-CLI contract tests were not used as evidence.

## 10. Lifecycle and acceptance audit

| Requirement | Result | Class |
|---|---|---|
| Runtime failure stays failed | H1: exit 2 → AgentRun `Failed` → WorkItem `Failed`, no verification, no acceptance, no retry, no fallback | `VERIFIED LIVE` |
| Exit-0 run whose report says it failed stays failed | **Was violated (O1, D-P1).** Fixed in `AgentRunWorkItemSync`: a valid report with `Failed`/`TimedOut`/`Cancelled` → new outcome `ExecutionReportedFailure` → WorkItem `Failed` with the agent's reason; the verification gate is never called. Regression test `SyncAsync_ExitZeroRun_WhoseOwnReportSaysFailed_FailsTheWorkItem_WithoutVerification` ×3; with the fix disabled all 3 fail, with it 3/3 pass | `VERIFIED BY TEST EXECUTION` (fix); live repeat O2/O3 ran on the fixed code but the agents' reports there were malformed, so the new branch was **not exercised live** — `NOT VERIFIED` live |
| Success path reaches verification/acceptance only via a succeeded run | H2, O2, O3: AgentRun `Succeeded` → Testing → gate → criteria → Completed | `VERIFIED LIVE` |
| Acceptance proves the requested outcome | criteria checked `Multiply`, `Add`, `Multiply_ReturnsProduct` present + tests pass; independently re-checked | `VERIFIED LIVE` |
| Acceptance rejects unrequested changes | **No** (O3 completed with a stray file; the integrity policy is a deny-list) — G-P3 | `VERIFIED LIVE` |
| Independent review | **Not effective — D-P2.** H2: the Hermes reviewer returned a real fenced JSON findings array (at least one `Medium`/`Correctness` finding), but `AgentCodeReviewer.ParseFindings` deserializes the *whole* output, which begins with the container start-up log → "could not be parsed" → one non-blocking Info finding → Completed. O1–O3 (reviewer routed to OpenHands by the harness, not by the shipped config): the reviewer reported an empty workspace. Root causes: `ReviewContextBuilder` passes only changed-file paths (line 66) and the reviewer runs in an empty scratch workspace (`AgentCodeReviewer.cs:46–59`) | `VERIFIED LIVE` + `VERIFIED BY SOURCE` |

Owner decision: D-P2 is recorded and **not fixed in Phase P**. Fixing the parser alone would make the reviewer's real
complaints, such as "cannot examine the source", start blocking, and WorkItems would loop through repair. The reviewer must
first be given the code, which cannot go on the docker command line because of the OS argument-length limit noted in
`ReviewContextBuilder`.

## 11. Cleanup / orphan evidence

| Run | Factory containers before / immediately after / after 15 s | Foreign containers | Class |
|---|---|---|---|
| H1, H2, O1, O2, O3 | 0 / 0 / 0 each | unchanged (1 foreign container, same id and state) | `VERIFIED LIVE` |

- H1's anonymous volume was observed removed with its container (`docker rm -f -v`). No dangling volume was created
  during any Phase P run: the newest dangling anonymous volume dates from Phase O (§20). `VERIFIED LIVE`
- Interruption paths (cancel before/after creation, timeout, varying-moment cancellation) are covered without a model by
  `DockerLifecycleIntegrationTests`, which ran against the real daemon in each final regression run (§15).
- No live Worker-kill orphan simulation was run (`NOT VERIFIED`).

## 12. Workspace / security evidence

- **Process command lines:** a monitor sampled every process's command line every 1.5 s during each run. It compared each
  against the key in memory and wrote only counts.

  | Run | Samples | `docker run` command lines seen | Command lines containing the credential |
  |---|---|---|---|
  | H1 | 235 | 128 | **0** |
  | O1 | 191 | 4 | **0** |
  | O2 | 100 | 2 | **0** |
  | O3 | 100 | 3 | **0** |
  | H2 | 406 | 378 | **0** |

  Class: `VERIFIED LIVE`.
- **Workspace:** every file, including `.git`, was scanned for the credential value. 0 matches in all runs (`VERIFIED LIVE`).
  Forbidden runtime state: none. Unexpected files: O3's U+F00D file (credential-free by the scan).
- **Logs:** 0 captured log lines contain the credential (`VERIFIED LIVE`).
- **Ambient state:**
  - No host Hermes home was mounted.
  - OpenHands state is relocated into the container.
  - `.env.live` was read only inside opted-in test processes.
  - No `AI_FACTORY_*` variable or provider credential exists in the session environment after the runs.

## 13. Usage / cost evidence

| Run | Prompt | Completion | Total | Cost |
|---|---|---|---|---|
| H1 | NOT REPORTED | NOT REPORTED | NOT REPORTED | NOT REPORTED |
| H2 | 99,662 | 4,776 | 389,622 | NOT VERIFIED (not captured) |
| O1 | 123,799 | 1,030 | 124,829 | NOT VERIFIED (not captured) |
| O2 | 179,182 | 1,442 | 180,624 | NOT VERIFIED (not captured) |
| O3 | 232,533 | 1,575 | 234,108 | NOT VERIFIED (not captured) |

- All values are as persisted on the AgentRun (`VERIFIED LIVE`); none is computed.
- The harness printed "cost NOT REPORTED (no cost field is persisted)" for these runs. That label was **wrong**: cost lives
  in structured-result metadata (`CostUsd`/`CostSource`). The harness now prints the full metadata, but the five runs
  were not re-run, so their cost is `NOT VERIFIED`.
- The independent review dispatch is not an AgentRun, so its usage is not persisted at all.

## 14. Repeatability evidence

| Runtime | First passing run | Independent repeat | Result |
|---|---|---|---|
| OpenHands | O2 (fixed code) | O3 (fixed code, fresh repository) | **repeat failed** (stray file); not certified |
| Hermes | H2 | not run | not certified. The repeat was skipped by owner decision because the gate is already blocked by D-P2 |

O1 passed the harness, but under the fixed code it would have failed (its report said `Failed`), so it does not count as a
pass.

## 15. Default non-live regression

Live opt-ins disabled, no credential in the environment, Docker up (so the Docker category ran), compose Worker stopped.
Command: `dotnet test AI.Factory.slnx --no-build --logger trx`, three consecutive runs after the `--no-incremental` build.

Build: `dotnet build AI.Factory.slnx --no-incremental` — **0 warnings, 0 errors** (15.5 s). `VERIFIED BY TEST EXECUTION`

| Run | Start (local) | Wall | Exit | Api (p/f/s) | Application | Worker | Total passed / failed / skipped | Factory containers after |
|---|---|---|---|---|---|---|---|---|
| 1 | 20:04:03 | 130 s | 0 | 328/0/1 | 1401/0/5 | 135/0/33 | **1864 / 0 / 39** of 1903 | 0 |
| 2 | 20:06:13 | 174 s | 0 | 328/0/1 | 1401/0/5 | 135/0/33 | **1864 / 0 / 39** of 1903 | 0 |
| 3 | 20:09:08 | 111 s | 0 | 328/0/1 | 1401/0/5 | 135/0/33 | **1864 / 0 / 39** of 1903 | 0 |

- Parsed from the TRX files with an XML parser. In each run, **all 39 skips** carry `SKIPPED — LIVE OPT-IN NOT ENABLED (set …)`;
  no live test executed and none reported Passed without running. `VERIFIED BY TEST EXECUTION`
- The 5 `DockerLifecycleIntegrationTests` (real daemon: timeout, cancellation, cancel-before-creation at varying moments,
  orphan sweep) ran and passed in every run.
- Compared with Phase O's 1861 / 0 / 37 of 1898:
  - +3 passed: the D-P1 regression theory;
  - +2 skipped: the two opt-in certification tests.
- No billed call was possible: no opt-in variable or provider credential exists in the session environment, and `.env.live`
  is read only after an opt-in (`TestGating.cs`). `VERIFIED BY SOURCE` + environment check.
- After the runs: no `aif-api-tests-projects-*` and no `aif-cert-*` directory left; `apps/tests/AI.Factory.Api.Tests/projects/`
  absent.
- `%TEMP%` litter from older tests keeps growing per run (`aif-bench-baseline-*` ≈ 8 per run, `ai-factory-backup-tests-*`).
  This is pre-existing (follow-up task from Phase O), synthetic content only.

## 16. Full regression

See §15. The three runs are the full regression; nothing was excluded.

## 17. Neutral certification matrix

| Runtime | Gateway | Explicit model | Credential | Real generation | Artifact | Verification | Acceptance | Cleanup | Repeat | Final status |
|---|---|---|---|---|---|---|---|---|---|---|
| Hermes | OpenRouter | `deepseek/deepseek-v4-flash-0731` (H1 `:free` id rejected by the provider) | Factory `OPENROUTER_API_KEY`, by name into the container | `VERIFIED LIVE` (H2) | `VERIFIED LIVE` | `VERIFIED LIVE` | criteria `VERIFIED LIVE`; review `NOT VERIFIED` (D-P2) | `VERIFIED LIVE` | not run | `BLOCKED — FACTORY DEFECT` |
| OpenHands | OpenRouter | `qwen/qwen3-coder` | Factory `OPENROUTER_API_KEY` via the agent-server API | `VERIFIED LIVE` (O1–O3) | `VERIFIED LIVE` | `VERIFIED LIVE` | criteria `VERIFIED LIVE`; review `NOT VERIFIED` (D-P2); unrequested file accepted (G-P3) | `VERIFIED LIVE` | **failed** (O3) | `BLOCKED — FACTORY DEFECT` |
| Claude Code | none (CLI) | unmapped | not supplied | `NOT VERIFIED` | `NOT VERIFIED` | `NOT VERIFIED` | `NOT VERIFIED` | non-model contract only | — | `ENVIRONMENT BLOCKED` |
| Codex | none (CLI) | unmapped | none | `NOT VERIFIED` | `NOT VERIFIED` | `NOT VERIFIED` | `NOT VERIFIED` | `NOT VERIFIED` | — | `ENVIRONMENT BLOCKED` |

No ranking or scoring is implied. Token counts differ by runtime and model and are not comparable here.

## 18. Remaining blockers

Factory (must be addressed before Phase Q):

1. **D-P2:** the independent review is not effective.
   - The reviewer needs the change content: a bounded diff, delivered other than on the docker argv.
   - `AgentCodeReviewer` must extract the findings array from noisy runtime output.
   - Only then can unparseable output become blocking.
2. **G-P3:** acceptance/integrity cannot reject unrequested files (the integrity policy is a deny-list).
3. Automatic ids are not rejected by the Factory, and the shipped catalog still lists `auto/coding:free`. Hermes/OpenHands
   silently use `DefaultModel` when a run carries no model.
4. A Hermes provider rejection (H1) is persisted with the container start-up log as `ErrorMessage` and the category
   `Agent`. The provider's reason is only in the output.
5. A malformed agent report that says `repair_required` (O2/O3) is, by the Phase 18.20 design, treated as "no usable report",
   so verification decides. Recorded, not changed.

External:

- Claude credential not present in the session (`claude setup-token` needs the owner's browser login).
- Codex not installed.
- The `:free` DeepSeek id is no longer served by OpenRouter.

## 19. Changed files (this phase)

- `apps/src/AI.Factory.Application/Orchestration/AgentRunWorkItemSync.cs` — D-P1 fix (`ExecutionReportedFailure` branch).
- `apps/src/AI.Factory.Application/Orchestration/IWorkItemExecutionResultMapper.cs` — new enum value
  `ExecutionReportedFailure`.
- `apps/tests/AI.Factory.Application.Tests/AgentRunWorkItemSyncTests.cs` — regression theory (3 cases) + `CountingVerificationGate`.
- `apps/tests/AI.Factory.Worker.Tests/ModelBackedCertificationTests.cs` — **new**, opt-in certification harness (skipped by default).
- `apps/tests/Shared/TestGating.cs` (untracked since Phase O) — two opt-in names `AI_FACTORY_LIVE_CERT_HERMES`,
  `AI_FACTORY_LIVE_CERT_OPENHANDS`.
- `docs/phase-p-report.md`, `docs/local-setup.md` (certification commands, setup-token note),
  `docs/execution-runtimes.md` (agent-reported failure semantics, Phase P limitations).

There were no API, DB schema/migration, Flutter, appsettings, compose or production-default changes (`VERIFIED BY SOURCE`,
`git status`).

## 20. Worktree state

- `git status`: 57 entries.
  - The 52 Phase O entries are carried over unchanged.
  - Phase P adds:
    - modified: `AgentRunWorkItemSync.cs`, `IWorkItemExecutionResultMapper.cs`, `AgentRunWorkItemSyncTests.cs`;
    - new untracked: `ModelBackedCertificationTests.cs`, `docs/phase-p-report.md`;
    - edits to the already-modified `docs/local-setup.md` and `docs/execution-runtimes.md`;
    - edits to the already-untracked `apps/tests/Shared/TestGating.cs`.
  - Nothing is committed, pushed or tagged; HEAD is still `0d2ab9c`.
- Untouched: API, Domain, Persistence/Migrations, Worker `src`, appsettings, compose files, Flutter. `SystemProcessRunner.cs`
  is unchanged.
- Secret scan over every tracked/untracked text file: 21 secret-shaped matches.
  - 17 carry synthetic markers.
  - 4 are the pre-existing redaction fixtures in `ProjectDiagnosticsAcceptanceTests.cs` / `SensitiveDataProtectionTests.cs`.
  - The real OpenRouter key value occurs in **0** repository files other than the git-ignored, untracked `.env.live`
    (compared in memory, never printed).
  - Class: `VERIFIED LIVE`.
- Docker:
  - 0 Factory agent containers.
  - The only container present is the standalone `hermes` (untouched; present before and after every run).
  - The `ai-factory` compose stack's and the `seniorconnect` project's containers were already absent before the first
    certification run (every run recorded exactly 1 foreign container). They were not removed by this phase; their named
    volumes still exist.
  - 16 dangling volumes. The newest anonymous ones were created 2026-10-03 11:22 and 11:44 UTC (Phase O), so **none was
    created by a Phase P run**. No volume was removed.
- Processes: no Factory-launched `claude`/`codex` process; no Hermes/OpenHands container running.
- The compose Worker that the owner stopped for the certification is still stopped. Restarting it is the owner's call.

## 21. Final gate

**`BLOCKED — FACTORY CERTIFICATION DEFECT`**

The environment allowed Hermes and OpenHands to be certified, and the Factory path executed real model generation with
correct routing, credential isolation, verification, acceptance criteria and cleanup. However:
- the independent review that every Completed WorkItem implies did not take place (D-P2);
- the Factory accepted an unrequested workspace change (G-P3);
- the OpenHands repeat failed.

D-P1 was fixed with regression coverage. D-P2 and G-P3 remain open by owner decision. Claude Code and Codex are
additionally `ENVIRONMENT BLOCKED`.

### Phase P Final Result

- Status: `BLOCKED — FACTORY CERTIFICATION DEFECT`
- Model-backed certification:
  - Hermes: not certified. Real generation, verification, acceptance criteria and cleanup were verified live; review is not
    effective (D-P2); no repeat.
  - OpenHands: not certified. Real generation verified live ×3; the repeat failed (G-P3); review is not effective.
  - Claude Code: `ENVIRONMENT BLOCKED — CLAUDE CREDENTIAL`.
  - Codex: `ENVIRONMENT BLOCKED — CODEX CLI NOT INSTALLED`.
- Verified live:
  - real generation: Hermes H2; OpenHands O1–O3
  - verification: same runs
  - acceptance (criteria): same runs
  - usage: tokens as reported (§13); cost not captured
  - cleanup: 0 Factory containers in all 5 runs
  - workspace isolation: no runtime state, no credential; O3 stray file
  - credential isolation: 0 argv / log / workspace hits
- Not verified: review effectiveness; the D-P1 branch exercised live; cost; Hermes repeat; Worker-kill orphan simulation; Claude Code and Codex model-backed anything; token revocation independently.
- Environment blocked: Claude credential, Codex CLI, the `:free` DeepSeek id.
- Benchmark: `NOT RUN — RESERVED FOR PHASE Q`
- Regression: build 0 warnings / 0 errors; 3 runs × 1864 passed / 0 failed / 39 skipped of 1903; 0 leaked Factory containers
- Security: token rotation OWNER-ASSERTED; credential leakage none observed; workspace leakage none (credential); process
  argument leakage none observed
- Worktree: see §20; committed `NO`
