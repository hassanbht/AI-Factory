# Phase O — Reliability Stabilization, Deterministic Test Isolation, Secret Hygiene, Certification Readiness

Evidence classes (exactly one per claim): `VERIFIED BY SOURCE`, `VERIFIED BY TEST EXECUTION`, `VERIFIED LIVE`,
`ENVIRONMENT BLOCKED`, `NOT VERIFIED`, `INFERENCE`. Historical reports (Phase M, Phase N) were not edited to match any
number here; Phase N's `docs/phase-n-report.md` is the baseline source of truth.

## 0. What Phase O established, in five lines

1. The default `dotnet test` no longer makes a billed model call: before Phase O it ran a real Hermes review through
   OpenRouter on every run (`.env.live` was auto-loaded). Now it makes none, and the 37 live tests that used to be counted as
   passed without running (or, for one, ran billed) are reported **skipped with a reason**. — `VERIFIED BY SOURCE`,
   `VERIFIED BY TEST EXECUTION`.
2. The Docker cancellation race (Phase N F6) was a real Factory defect, not a Docker quirk, and is fixed with a bounded
   reconciliation. Old behaviour: the real cancel test failed 10 of 10 and left 10 containers; new: passed 10 of 10. —
   `VERIFIED BY TEST EXECUTION` (real Docker), mutation-checked.
3. Docker-heavy and build-heavy tests can no longer overlap — not even across the three test assemblies, which
   `dotnet test AI.Factory.slnx` runs concurrently. — `VERIFIED BY SOURCE`, `VERIFIED BY TEST EXECUTION`.
4. Three more defects found and fixed on the way (§3: D5 Api tests wrote 2,371 directories — and 14 nested git repositories
   that the Phase N commit swept in — into the repository; D6 a test that would have removed a developer's live containers;
   D7 weak credential-isolation coverage).
5. Real model-backed Claude Code / Codex certification is still **not done**, for reasons Phase O cannot change: no
   credential, no Codex CLI, and an exposed token that only its owner can rotate. The benchmark was **not run**.

Final gate: see the last section.

## 1. O0 — Baseline (before any modification)

| Item | Value |
|---|---|
| Repository | `main` at `0d2ab9c` (the Phase N commit), worktree clean — `VERIFIED LIVE` (`git status`) |
| SDK / Docker | .NET SDK 10.0.401; Docker server 29.8.0 — `VERIFIED LIVE` |
| Runtimes present | Claude Code 2.1.284 **and 2.1.286** (desktop-bundled, `%APPDATA%\Claude\claude-code\<version>\<hash>\claude.exe`, not on PATH); Codex: **not installed**; images `nousresearch/hermes-agent:0.20.6`, digest-pinned `ghcr.io/openhands/agent-server`, `alpine` — `VERIFIED LIVE` |
| Environment (names only) | no `AI_FACTORY_*`; no `ANTHROPIC_API_KEY`, `OPENAI_API_KEY`, `OPENROUTER_API_KEY`, `CLAUDE_CODE_OAUTH_TOKEN` in this process; the desktop app's `CLAUDE_*` variables and `ANTHROPIC_BASE_URL` **are** set (an ambient routing variable the isolation tests must beat) — `VERIFIED LIVE` |
| `.env.live` | present, gitignored, untracked; holds `OPENROUTER_API_KEY`, `OMNIROUTE_API_KEY` and `AI_FACTORY_LIVE_*` settings (names only) — `VERIFIED LIVE` |
| Foreign Docker containers | `seniorconnect_db`, `sc_test_pg`, `hermes`, and the compose set `ai-factory-api-1` (restart loop), `ai-factory-worker-1`, `ai-factory-migrator-1` — none touched, same six before and after — `VERIFIED LIVE` |
| Clean build | `dotnet build AI.Factory.slnx --no-incremental`: 0 warnings, 0 errors (36 s) — `VERIFIED LIVE` |

Baseline tests (command `dotnet test <project> --no-build`; the **3** tests of `ReviewExecutionLiveTests` /
`CheckOpenRouterConnectivity` were excluded so the baseline itself would not make a billed call):

| Project | Passed | Failed | Skipped | Total |
|---|---|---|---|---|
| Application.Tests | 1358 | 0 | 0 | 1358 |
| Worker.Tests (151 − 3 excluded) | 147 | **1** (`Cancellation_CleansUpContainer…`, the F6 race, in an isolated run) | 0 | 148 |
| Api.Tests | 314 | 0 | 0 | 314 |

1358 + 151 + 314 = 1823: Phase N's count is still exact. **Baseline caveat, recorded as it happened:** my first
background launch of the Worker baseline was not actually stopped and ran concurrently with the second; that log shows
148/148 and 147/148 interleaved. The isolated rerun (59 s) is the number above; it failed the same test. — `VERIFIED LIVE`.
The same baseline Api run left two new untracked directories in the repository (D5).

Live-test activation at baseline (from source): `AI_FACTORY_HERMES_SMOKE_TEST`, `AI_FACTORY_OPENHANDS_SMOKE_TEST`,
`AI_FACTORY_LIVE_HERMES_CREDENTIALS`, `AI_FACTORY_LIVE_GATEWAY_AGENTRUN`, `AI_FACTORY_LIVE_SMOKE`,
`AI_FACTORY_LIVE_CLAUDE_CONTRACT`, `AI_FACTORY_LIVE_BENCHMARK`/`AI_FACTORY_RUN_BENCHMARK` — each test with its own `if
(!Enabled) return;`. Five call sites in four classes called `BenchmarkHarness.TryLoadEnvLive()` regardless. — `VERIFIED BY SOURCE`.

## 2. Final regression results (O16)

Command (every run): `dotnet test AI.Factory.slnx --no-build --logger trx` after a build, nothing else of mine running.
Runs 1–3 were taken before the last edits (listed below the table) and are recorded for completeness; the three **final**
runs are the ones that count.

| Run | Started (UTC) | Wall | Exit | Api (pass/fail/skip) | Application | Worker | Total pass / fail / skip | Factory containers left |
|---|---|---|---|---|---|---|---|---|
| 1 | 11:04:04 | 115 s | 0 | 328/0/1 | 1398/0/5 | 135/0/31 | **1861 / 0 / 37** of 1898 | 0 |
| 2 | 11:08:36 | 173 s | 0 | 328/0/1 | 1398/0/5 | 135/0/31 | **1861 / 0 / 37** of 1898 | 0 |
| 3 | 11:11:29 | 166 s | 0 | 328/0/1 | 1398/0/5 | 135/0/31 | **1861 / 0 / 37** of 1898 | 0 |
| **final 1** | 11:17:17 | 131 s | 0 | 328/0/1 | 1398/0/5 | 135/0/31 | **1861 / 0 / 37** of 1898 | 0 |
| **final 2** | 11:19:28 | 126 s | 0 | 328/0/1 | 1398/0/5 | 135/0/31 | **1861 / 0 / 37** of 1898 | 0 |
| **final 3** | 11:21:34 | 131 s | 0 | 328/0/1 | 1398/0/5 | 135/0/31 | **1861 / 0 / 37** of 1898 | 0 |

- **Final code, three consecutive runs, zero failures, identical counts.** `VERIFIED BY TEST EXECUTION`. Pass 1861, fail 0,
  skipped 37, total 1898 (baseline 1823; +75 = 45 gate tests + 30 net new in Application + 1 real-Docker test − 1 for a theory
  that skips as one result).
- **No failure was replaced by a rerun**: there was none in any post-change run. The only failure in the whole phase is the
  baseline's Docker cancel race (§1), which is the defect D3 fixed.
- Run 1 predates the machine-wide lock (hence 115 s); runs 2–3 have the lock; the finals additionally carry the real-Docker
  test's client-process assertions, the 30 s Docker probe, comment-only edits and formatting. Per-assembly durations in the
  console include time spent waiting for the lock (the Api assembly's only Docker-collection test is skipped but its
  collection still queues), so they overlap and do not add up to the wall time.
- Build: `dotnet build AI.Factory.slnx --no-incremental` — **0 warnings, 0 errors** (before the finals).
- Environment notes: Windows 10 Home 19045; .NET SDK 10.0.401; Docker 29.8.0; 15.8 GB RAM, **3.3 GB free** when measured idle
  after the finals (2.9 GB in Phase N); no `AI_FACTORY_*` variable and no provider credential in the test process.
- After every run: 0 Factory-labelled containers; `git status` shows only the intended files (below); no
  `aif-api-tests-projects-*` temp directory left; the foreign containers unchanged — one more appeared during the finals
  (`sc_phase4_pg`, another project's), also untouched.
- The default run's skips, with the reason text xunit reports (`VERIFIED BY TEST EXECUTION`, TRX of final 3): 19 ×
  `SKIPPED — LIVE OPT-IN NOT ENABLED (set AI_FACTORY_HERMES_SMOKE_TEST)`, 5 × benchmark, 4 × Claude contract, 3 × OpenHands,
  2 × gateway, 2 × Hermes credentials, 1 × `AI_FACTORY_LIVE_SMOKE`, 1 × OpenHands isolation = 37. Skipped is not certification.

Targeted subsets (O16 order; counts from the final TRX; all passed): process runner `SystemProcessRunnerTests` 10 · Docker
lifecycle `DockerLifecycleIntegrationTests` 5 (+ `DockerCleanupReconciliationTests` 20, `OpenHandsExecutionAdapterTests` 21) ·
adapter contract `CliProcessTreeLifecycleTests` 27 · profile/credential `CliRuntimeProfileResolutionTests` 17,
`CredentialIsolationAndReaperTests` 9 · workspace integrity `WorkspaceIntegrityTests` 26, `VerificationGateTests` 15 ·
gate `LiveTestGateTests` 45 (15 × 3 assemblies) · `GoldenProjectLifecycleE2ETests` 1.

## 3. Defects found, root cause, fix, regression test

| # | Defect | Root cause | Fix | Regression protection |
|---|---|---|---|---|
| D1 | The default `dotnet test` made a **billed** OpenRouter call through real Hermes (and a keyed HTTP request to `openrouter.ai`) | `ReviewExecutionLiveTests.LiveReview…` and `CheckOpenRouterConnectivity` called `BenchmarkHarness.TryLoadEnvLive()`, which loaded the gitignored `.env.live`; nothing required an opt-in | A single gate (`apps/tests/Shared/TestGating.cs`, linked into all three test projects): opt-in variable → else skipped; `.env.live` is read only for an opted-in test that declares `RequiredEnvironment`, never switches an opt-in on, never read otherwise; `BenchmarkHarness.TryLoadEnvLive` removed (no caller left) | `LiveTestGateTests` (15 per assembly): skip reasons, `.env.live` not read without opt-in / without declared need, names-not-values, opt-in alternatives; a reflection guard that **failed on first run** and named the gaps (below) |
| D2 | 37 live tests "passed" without running (`if (!Enabled) return;`, also Docker-absent and non-Windows early returns) — a skipped certification counted as green | xunit 2.9.2 has no runtime skip; the pattern returned early | `LiveFact`/`LiveTheory`/`DockerFact`/`WindowsFact`/`WindowsTheory`: static skip at discovery with `SKIPPED — LIVE OPT-IN NOT ENABLED (set …)`, `ENVIRONMENT BLOCKED — …`; `CliRuntimeLiveSmokeTests` has its own runtime-aware attribute (needs `AI_FACTORY_LIVE_SMOKE` = `ClaudeCode`/`Codex` and that runtime's credential) | the reflection guard `EveryLiveShapedTest_UsesAGatedAttribute_NeverAPlainFact` (a new live-shaped plain `[Fact]` fails the run; non-live methods in live-named classes are an explicit allow-list) |
| D3 | **Docker cancel race (F6)** — a container appears after cancellation cleanup | `docker rm -f` of a missing container exits **0 with no output** (`VERIFIED LIVE`, Docker 29.8.0), so the single cleanup could not tell "never existed" from "not created yet"; killing the `docker run` client before the daemon finished creating left a `Created` container (`VERIFIED LIVE`; its anonymous volume by Docker semantics — `INFERENCE`) | `DockerContainerCleanup` (§4); wired into `HermesAgentExecutor` and `OpenHandsExecutionAdapter`; `IContainerLifecycleManager.ReconcileCleanupAsync` (default method — runtimes without creation latency are unchanged) | §4 |
| D4 | Heavy tests overlap and starve each other (F6–F8) | per-assembly xunit parallelism, **and** `dotnet test AI.Factory.slnx` starts the three assemblies concurrently (all three TRX files start within 2 s) | two serialized collections; the Docker/real-build one holds a machine-wide file lock (§5) | §5 |
| D5 | The Api tests wrote **2,371 directories** into the repository on every run, including 14 nested git repositories (`src-*`) that the Phase N commit tracked as gitlinks | `AgentWorkspace:ProjectsBaseDirectory` resolves to `../../../projects` relative to the test binary, i.e. inside `apps/tests/AI.Factory.Api.Tests` | `TestWorkspaceIsolation` (module initializer): per-process temp base + allowed root via environment variables (they win over appsettings for every `WebApplicationFactory` at once), removed at exit; the leaked tree was deleted from the working tree (unstaged: git shows 14 deleted gitlinks — your decision to commit) | an Api run now adds nothing to `git status` and leaves no `aif-api-tests-projects-*` temp directory — `VERIFIED BY TEST EXECUTION` (every run below) |
| D6 | `OrphanSweep_…` ran the **global** orphan sweep against an empty DB: every Factory-labelled agent container on the daemon looked orphaned — including a developer's live Worker's active runs | the sweep is global by design; the test did not scope it | the sweep's `docker ps` is narrowed to the test's own container (`name=` filter added by a decorator); sweep logic and removal stay real | the test itself (passes; a foreign container is asserted untouched) |
| D7 | Credential-isolation tests were weaker than their name: the ambient routing/home variables were only *asserted absent* (vacuous on a clean CI box), assertion failures echoed secrets, and the test cleared the developer's real variables | — | the matrix now **sets** ambient `ANTHROPIC_BASE_URL`, `OPENAI_BASE_URL`, `OPENAI_API_BASE`, `OPENAI_ORG_ID`, `ANTHROPIC_AUTH_TOKEN`, `CLAUDE_CONFIG_DIR`, `CODEX_HOME` + the four credentials; asserts none reaches the child and that HOME/USERPROFILE/APPDATA/TEMP/CLAUDE_CONFIG_DIR/CODEX_HOME are inside the per-run state root, never the user's profile or the ambient dir; failures name the surface, never the value; previous values restored | `SyntheticSecret_ReachesOnlyTheChildEnvironment_AndNoOtherSurface` (2) |
| D8 | docs gave a Claude Code path that no longer exists | the bundle now nests `<version>\<hash>\claude.exe` | discovery snippet in `docs/local-setup.md` (tested) | — |

What the reflection guard caught when first run (`VERIFIED BY TEST EXECUTION`): four non-live tests in
`RuntimeBenchmarkE2ETests`, one in `ReviewExecutionLiveTests`, and the guard's own tests — the allow-list now names them
with the reason they are non-live.

## 4. O4/O5 — Docker cancellation race

**Behaviour now.** After an *interrupted* launch (cancelled, timed out, or `docker run -d` itself timed out) the Hermes
executor and the OpenHands adapter call `DockerContainerCleanup.RemoveAsync(reconcile: true)`:

1. `docker rm -f -v ai-factory-agent-<run id>` — the deterministic name of **this** run's container, nothing else is ever
   listed, filtered or removed (every command is asserted to be exactly that);
2. a removal that prints the name ⇒ `Removed`, immediately (one container per name; the launch client is dead);
3. nothing printed ⇒ retry every 250 ms, backing off to 1 s, until a **10 s window** is spent ⇒ `NotFound`;
   "removal already in progress" is remembered and, once gone, reported `Removed`; docker not startable ⇒ `Failed` at once;
4. the explicit outcome (`Removed`/`NotFound`/`Failed`) is logged as `ContainerCleanup … Outcome= Attempts= ElapsedMs=`.

The ordinary path is unchanged: container exists or the launch finished ⇒ one removal, no waiting. `OpenHands` keeps the
single-shot removal when `docker run -d` answered definitively (a refusal, or the run is already up). Own timeout per
removal, independent of the (already cancelled) run token; idempotent and cancellation-safe. — `VERIFIED BY SOURCE`.

**Bound.** Window 10 s + one removal (≤ 10 s) in the worst case; a container created later than that belongs to a
terminal AgentRun and is removed by the periodic orphan sweep, so a cancelled launch converges to zero within the window
plus `Recovery:PeriodicSweepInterval` (1 min by default in `RecoveryOptions` and the Worker `appsettings.json`; a stale `bin/Release` copy shows 5 min from an older build and is not source). — `VERIFIED BY SOURCE`
(the sweep is `AgentRunOrphanRecovery` → `SweepOrphanContainersAsync`; the sweep's own behaviour was already tested).

**Why 10 s (measured, real daemon, this host).** First attempt was 5 s; the real test then leaked in 2 of 3 runs. With
launches cancelled together the container was found 2.8–11.8 s after cleanup began (eight at once) and 1.8–8.5 s (four);
2–3 of eight were created after a 5 s window. The wait is paid only when no container is found. — `VERIFIED LIVE`.

**Proof.**

| Test | Level | Result |
|---|---|---|
| `DockerCleanupReconciliationTests` (20) | fake daemon on a manual clock: `cancel → delayed container appearance → cleanup reconciliation` for appearances at 0 / 0.3 / 1.2 / 4.7 / 9.4 s; never-appears (bounded, `NotFound`, attempts bounded by backoff); appears after the window (documented limit); exists-already (1 attempt, no wait); single-shot never polls; in-progress then gone; daemon unreachable (`Failed`, never `NotFound`); docker not installed (`Failed` at once); caller abandonment (no hang). Every command is `docker rm -f -v <exactly this run's name>`; a foreign container in the same fake daemon survives | pass — `VERIFIED BY TEST EXECUTION` |
| same class, executor level | the real `HermesAgentExecutor` with a racing runner: client killed on cancel, daemon creates the container 0.2 / 0.7 / 1.5 s **after**; also launch-client-timeout; also cancelled before any launch (docker never started) — asserts zero containers of the run after the late creation, `Cancelled`/`TimedOut` kept, foreign container untouched | pass |
| `OpenHandsExecutionAdapterTests.ContainerMode_CleanupIsReconciled_OnlyWhenTheLaunchWasInterrupted` (4) | cancelled during launch / launch client timed out ⇒ reconciliation; launch refused / cancelled while running ⇒ single-shot | pass |
| `DockerLifecycleIntegrationTests.Cancellation_CleansUpContainer…` | **real Docker, real Hermes image**: now asserts zero immediately **and** 2 s later (the old test slept 1 s and flaked) | new code 10/10 pass |
| `…CancelBeforeContainerCreation_AtVaryingMoments_ConvergesToZeroFactoryContainers` | real Docker, 4 launches killed at 0/40/120/350 ms: no `Failed`, bounded, every launch **client process dead** (pid + start time), the system converges to zero (sweep emulated per run, never the global sweep), foreign container untouched | pass; typical: 0/40 ms `NotFound` after 10 s (client died before the request), 120/350 ms `Removed` after 2–8 s, 0 left for the sweep |

**Mutation check** (`VERIFIED BY TEST EXECUTION`): with the executor temporarily switched back to the single-look cleanup,
4 of the 18 then-existing reconciliation tests failed (all executor-level race tests); restored, 18/18 (now 20/20).
**Before/after on the real daemon:** old behaviour, `Cancellation_CleansUpContainer…` ×10 → **0 pass, 10 fail, 10
`Created` Hermes containers left behind** (mine; verified recent and removed by exact name); new behaviour ×10 → **10 pass**.

## 5. O6 — Test contention and parallelism

Evidence: `dotnet test AI.Factory.slnx` — all three TRX files start within two seconds (`VERIFIED LIVE`); the heaviest default
tests are `GoldenProjectLifecycleE2ETests` (≈ 90 s, real `dotnet build`/`test`), the Docker lifecycle tests (6–19 s each,
real containers) and the process-tree lifecycle tests (≈ 6 s each, real processes) (`VERIFIED LIVE`, TRX). Host: 15.8 GB,
about 2.9 GB free in Phase N, 3.3 GB free when measured idle after the Phase O finals (no pre-change Phase O baseline was
taken — `NOT VERIFIED` for that).

Change (minimum, no timeout was raised, parallelism is not disabled globally):

| Collection | Tests | Rule |
|---|---|---|
| `Real processes (serialized per assembly)` | `CliProcessTreeLifecycleTests`, `SystemProcessRunnerTests`, `ClaudeCodeLiveContractTests`, `CliRuntimeLiveSmokeTests` | one at a time within their assembly, after the parallel collections (they also mutate process environment variables, which used to leak into concurrent tests) |
| `Docker and real builds (serialized machine-wide)` | `DockerLifecycleIntegrationTests`, `GoldenProjectLifecycleE2ETests`, `VerificationGateRealBuildIntegrationTests`, every Docker-based opt-in live suite | as above **plus** an exclusive file lock in `%TEMP%` held for the collection (bounded 30 min, released by the OS if a run dies), so the three assemblies cannot overlap in it |

Cost: a full run takes ≈ 126–131 s on the final code (166–173 s in runs 2–3, 115 s in run 1 before the lock). The three
flaky tests of Phase N (F6, F7, F8) did not recur in any of the six post-change full runs — see §2; that is evidence, not
proof, of the cause (`INFERENCE`: contention).

## 6. O7/O8 — Process runner and real-process lifecycle coverage

`SystemProcessRunner.cs` is byte-identical to `HEAD` (the Phase N pipe-release fix is preserved) — `VERIFIED BY SOURCE`.
Coverage matrix (all `VERIFIED BY TEST EXECUTION`, real processes):

| Requirement | Test(s) |
|---|---|
| root exit observed independently of pipe closure; bounded drain; descendants don't hang the run | `RunAsync_RootExitedButADescendantHoldsThePipes…`, `FinishedCli_WithALingeringGrandchildHoldingThePipes…` (×2 runtimes) |
| stdout **and** stderr drained, no deadlock on full pipes | **new** `RunAsync_MegabytesOnBothStdoutAndStderr_DoesNotDeadlock…` (20,000 lines on each pipe), `NonZeroExit_WithStderr_IsFailed…` (**new**, stderr drained, redacted, logged) |
| timeout; cancellation (before launch, right after, during); whole tree killed | `RunAsync_TimesOutAndKillsProcess…`, `Timeout_KillsTheWholeProcessTree…`, `Cancellation_*` ×3 (grandchild pid recorded and proven dead) |
| stdin closed, large UTF-8 prompt | `LargePrompt_IsDeliveredCompletely_AndStdinIsClosed`, `RunAsync_ChildThatNeverReadsALargeStdin…` |
| exit-zero success vs. no/partial result event; non-zero failure | `ExitZeroWithoutAResultEvent…`, `ExitZeroWithPartialFinalEvent…`, `SuccessfulStructuredResult…`, `RunAsync_ReturnsNonZeroExitCode…`, **new** non-zero case |
| callback failures cannot corrupt lifecycle | `RunAsync_ThrowingOutputObserver…`, `RunAsync_ThrowingOnStartedCallback…` |

The lifecycle class is not duplicated elsewhere; I added only failure modes that were uncovered (large output, non-zero exit
with stderr, 401-then-late-success, an endless retry-event stream). Known limit, unchanged: a grandchild already
re-parented before the kill is not found by the tree walk (documented in the runner).

## 7. O9–O11 — Claude Code contract, credential isolation, 401 boundary

Installed: **2.1.284 and 2.1.286**. Re-discovered, not hard-coded. All 13 flags the adapter passes (`-p`, `--output-format`,
`--verbose`, `--model`, `--session-id`, `--no-session-persistence`, `--permission-mode`, `--setting-sources`,
`--strict-mcp-config`, `--disable-slash-commands`, `--bare`, `--max-budget-usd`, `--allowedTools`) are present in
`claude --help` of 2.1.286 — `VERIFIED LIVE`.

`ClaudeCodeLiveContractTests` against **each** version, with `AI_FACTORY_LIVE_CLAUDE_CONTRACT=1` — 5/5 passed on both:
only the Factory credential reaches the (local, scripted) provider in API-key and OAuth modes although ambient credentials
and the real `~/.claude` exist; a 401 stops the run at the first request (one request; 1.7–8.7 s, not the CLI's multi-minute
retry loop); the real `result` event is parsed; a hung provider ends `TimedOut` at the Factory deadline and `Cancelled` on
cancellation with the whole process tree gone. **`VERIFIED LIVE — REAL CLI, NON-MODEL BACKED`.** This is not model
generation and certifies nothing about any model.

O10 matrix (D7) and the O11 regressions, `VERIFIED BY TEST EXECUTION` with real processes (Claude Code **and** Codex adapters):
`AuthenticationFailure_StopsTheRunOnTheFirstEvent…`, **new** `AuthenticationFailure_NeverBecomesSuccess_EvenIfTheCliWouldLaterReportOne`
(the CLI "retries" 8 s and then prints a success result; the Factory killed it at the first 401 — `Failed`, `Authentication`,
tree dead, no late output), **new** `CliThatKeepsEmittingRetryEventsForever_IsStoppedByTheFactoryDeadline` (busy pipes, the
configured deadline still ends it `TimedOut`). Codex's behaviour here is the adapter's against a **fake** CLI: the real Codex
binary is not installed (`ENVIRONMENT BLOCKED`).

## 8. O12–O14 — Hermes/OpenHands, workspace integrity, profile fail-closed

| Check | Result |
|---|---|
| Hermes credential precedence (`AI_FACTORY_LIVE_HERMES_CREDENTIALS=1`, local recording provider — no external call) | 2/2 — the old mount style leaks the ambient key; the Phase M executor sends only the Factory key — `VERIFIED LIVE` |
| OpenHands state isolation (`AI_FACTORY_OPENHANDS_SMOKE_TEST=true`, closed loopback + fake key — no provider contacted) | 1/1 — two concurrent runs leave no `conversations/`/`bash_events`, clean `git status`, no key in the workspace, zero containers — `VERIFIED LIVE` |
| Hermes cleanup after startup failure / timeout / cancellation | `DockerLifecycleIntegrationTests` — `VERIFIED LIVE`. Cleanup after a **successful** run: `NOT VERIFIED` in Phase O (needs a model call); by source `--rm` + `rm -f -v` |
| Workspace integrity | `WorkspaceIntegrityTests` (+ `VerificationGateTests`) pass in every run; `GitInspector_WithoutABaseline_ReportsUnavailable_NotClean` and the source (`git status` failure ⇒ exception ⇒ `InfrastructureError`, never "clean") — `VERIFIED BY TEST EXECUTION`, `VERIFIED BY SOURCE` |
| Fail closed | `CliRuntimeProfileResolutionTests` (17): unknown/unregistered/disabled adapter fails closed, unmapped model fails closed, no runtime fallback, no gateway fallback, a structural test that no `Fallback`/`Failover` option exists — pass in every run — `VERIFIED BY TEST EXECUTION` |

## 9. O15/O17 — Test categories and live-test safety

See `docs/local-setup.md` ("Running the tests") and `docs/execution-runtimes.md` for the categories A–E, the opt-in
variables and commands. Default `dotnet test` = categories A, B, C; D and E skipped with a reason. Skipped in a default run
(`VERIFIED BY TEST EXECUTION`): Worker 31, Application 5 (4 `ClaudeCodeLiveContractTests` results — the theory skips as
one — and `HermesSmokeTest`), Api 1 (`RealHermesApiE2ETests`) = **37**.

Safety audit before any live test ran in Phase O (only category D, all non-billed): explicit opt-in set for that one
process only; no credential needed or present (the Hermes test uses a local recording provider, OpenHands a closed loopback
port and a fake key, Claude a scripted local endpoint — `VERIFIED BY SOURCE`); output contains synthetic markers only; each
test works in a throwaway temp workspace under a Factory-owned per-run directory; cleanup is bounded (§4) and was verified
(zero Factory containers after, `docker ps` before/after each run); billing risk: none — **no billed model call was made in
Phase O** (`VERIFIED BY SOURCE`: only the gated tests read provider keys or contact provider hosts; `NOT VERIFIED` at network
level — no packet capture; the default run starts Hermes containers with no provider key configured, so nothing billable can
authenticate). `.env.live` was not loaded by any run in this phase.

## 10. O1 — Credential and token hygiene audit

Patterns scanned (values never printed; counts and paths only): `sk-ant-`, `sk-ant-oat`, `sk-or-v1-`, `sk-`+32, GitHub
tokens, AWS key ids, JWT shapes, Slack tokens, `Authorization: Bearer <value>`, JSON `"api_key"/"token"/"secret"/"password"`
with a literal, `*_API_KEY`/`*_TOKEN` assignments with a literal, auth-artifact filenames — over tracked + untracked files,
`docs/`, `apps/`, `bench/`, `data/`, `projects/`, `prompts/`, `.serena/`, `docs/releases/`, the session scratchpad/evidence
directory, and the git history.

| Location | Category | Finding | Active? | Status |
|---|---|---|---|---|
| tracked/untracked source and tests | synthetic markers (`sk-ant-api03-PHASEN…`, `sk-ant-oat01-PHASEN…`, `sk-or-v1-0123…`, `sk-or-v1-PHASE…`) | 14 files; all obviously synthetic, used as leak-test markers | no | expected |
| `.ai/context/phase-17.4.10-production-runbook.md` | placeholder (`OPENROUTER_API_KEY=you…`) | documentation placeholder | no | expected |
| `.env.live` | provider keys | gitignored, untracked, not read by any Phase O run; names only reported | unknown (not used) | expected |
| `data/factory.db` (ignored) | token-shaped bytes | 0 matches | — | clean |
| scratchpad/evidence, `bench/`, `projects/`, `prompts/`, `.serena/`, `docs/releases/` | token-shaped strings | 0 files | — | clean |
| `docs/phase-m-report.md` | **Anthropic OAuth token** (`sk-ant-oat…`, Phase N) | redacted in Phase N; `HEAD` has 0 `sk-ant-` there; `git log --all -G` finds token-shaped strings only in `0d2ab9c` and only in my synthetic test constants | **NOT VERIFIED** (not used, per the rules) | `TOKEN REMOVED FROM REPOSITORY` |
| **`~/.claude/projects/D--Project-AI-Factory/d2284c62-….jsonl`** (this session's own conversation transcript, outside the repository, managed by the Claude app) | the same token shape | **6 long OAuth-token-shaped strings** (count only; it holds the Phase N conversation in which the file was read) | **NOT VERIFIED** | not mine to edit; **`ENVIRONMENT BLOCKED — credential rotation requires external action`** |

`TOKEN REVOKED/ROTATED`: **`NOT VERIFIED`** — it is an external action and no evidence of it exists. Revoke/rotate the token
(the transcript copy is what makes rotation, rather than deletion, the only real remediation). No history rewrite was
done; none is needed for the repository.

## 11. O18 — Environment readiness matrix

No scores, no ranking. "Contract" = non-model; "model-backed" = a real model produced work through the Factory path.

| Runtime | CLI / image present | Adapter | Credential present | Non-model contract | Model-backed certification | Workspace isolation | Cleanup |
|---|---|---|---|---|---|---|---|
| Hermes | image `nousresearch/hermes-agent:0.20.6` — `VERIFIED LIVE` | `HermesExecutionAdapter` + executor — `VERIFIED BY SOURCE` | none in the environment; one in the gitignored `.env.live` (unused) — `ENVIRONMENT BLOCKED` for any billed run | credential precedence 2/2 — `VERIFIED LIVE` | not run in Phase O — `NOT VERIFIED` (Phase N: one incidental review, historical) | isolated `/opt/data` volume, no ambient `~/.hermes`, run report deleted — `VERIFIED LIVE` (credential test) | startup-failure / timeout / cancellation cleanup — `VERIFIED LIVE`; success path — `NOT VERIFIED` |
| OpenHands | digest-pinned `ghcr.io/openhands/agent-server` — `VERIFIED LIVE` | `OpenHandsExecutionAdapter` — `VERIFIED BY SOURCE` | none | state isolation 1/1 — `VERIFIED LIVE` | not run — `NOT VERIFIED` | no state or key in the workspace — `VERIFIED LIVE` | zero containers — `VERIFIED LIVE`; interrupted launch reconciled — `VERIFIED BY TEST EXECUTION` (fake runner) |
| Claude Code | 2.1.284 and 2.1.286, not on PATH — `VERIFIED LIVE` | `ClaudeCodeExecutionAdapter` — `VERIFIED BY SOURCE` | **none** (no `ANTHROPIC_API_KEY`/`CLAUDE_CODE_OAUTH_TOKEN`) — `ENVIRONMENT BLOCKED` | 5/5 on both versions — `VERIFIED LIVE — REAL CLI, NON-MODEL BACKED` | `ENVIRONMENT BLOCKED` | per-run HOME/config, ambient credentials and routing beaten — `VERIFIED LIVE` + `VERIFIED BY TEST EXECUTION` | process tree killed on timeout/cancel, state dir removed — `VERIFIED LIVE` + `VERIFIED BY TEST EXECUTION` |
| Codex | **not installed** — `ENVIRONMENT BLOCKED` | `CodexExecutionAdapter` — `VERIFIED BY SOURCE` | none — `ENVIRONMENT BLOCKED` | adapter vs. a **fake** CLI only — `VERIFIED BY TEST EXECUTION`; the real event schema/flags: `NOT VERIFIED` | `ENVIRONMENT BLOCKED` | per-run `CODEX_HOME`, ambient beaten — `VERIFIED BY TEST EXECUTION` (fake) | same as Claude — fake CLI only |

## 12. O19 — Blocker separation

**Factory-controlled — fixed in Phase O:** D1–D8 above (accidental billed calls; skipped-as-passed; Docker cancel race;
uncontrolled overlap; repository pollution by the Api tests; a global-sweep test hazard; weak isolation coverage; stale docs).

**Environment-controlled:** Codex CLI not installed; Claude Code is desktop-bundled and not on PATH (path layout changed —
discover it, never pin it); a Docker host with little free memory makes daemon work slow (creation 2–12 s here) — it was
handled by a bounded window, not hidden by timeouts; 7 dangling anonymous Docker volumes pre-date Phase O (the five I could
inspect were created 26 Sep–2 Oct; origin `NOT VERIFIED`, not Factory-labelled, not touched); `ai-factory-api-1` (foreign compose stack) is in a restart loop.

**External credential / account:** no Claude credential supplied; no OpenAI/Codex credential; the exposed OAuth token must be
rotated by its owner.

**Not yet measured:** any model-backed Factory run on Claude Code or Codex (generation, verification, acceptance, usage and
cost); the real Codex CLI's event schema and flags; Hermes/OpenHands success-path cleanup; a live Worker-kill orphan
simulation; network-level absence of provider traffic in the default run; the Phase O host's free memory.

## 13. O20 — Benchmark

`NOT RUN — CERTIFICATION READINESS INCOMPLETE`. Preconditions not met: no Claude credential, no Codex CLI/credential, the
exposed token unrotated. Nothing was manufactured.

## 14. Remaining limitations (honest list)

- The Docker reconcile window is 10 s, not unbounded; a daemon slower than that leaves a `Created` container until the next
  periodic sweep (1 min by default). A launch killed before Docker was asked costs the full 10 s wait.
- Test locking is cooperative and local to one machine (`%TEMP%`); two CI agents sharing a Docker daemon are not coordinated.
- Docker availability is probed once per assembly at discovery (`docker info`, 30 s); a daemon that is only slow would
  surface as `ENVIRONMENT BLOCKED`, visibly, not as a pass.
- `.env.live`-based credentials are available to every opted-in test that declares a need in the same process; run live
  suites one at a time and unset the opt-ins afterwards (a user-level `AI_FACTORY_HERMES_SMOKE_TEST` would make a later plain
  `dotnet test` billed again — documented in `docs/local-setup.md`).
- Whitespace-format complaints remain in lines I did not touch (`LivePlannerE2ETests`, `VerificationGateRealBuild…`,
  `CliRuntimeLiveSmokeTests`); formatting-only edits outside the touched scope were not made.
- Pre-existing, untouched (a follow-up task is open): `%TEMP%` litter from other tests — 101 `ai-factory-backup-tests-*`, about 150
  `aif-bench-baseline-*` (≈ 8 more per run), a few `aif-oh-state-*`/`aif-phasen-*` when a delete fails on read-only git objects. Synthetic
  content only.

## 15. Files

New: `apps/src/AI.Factory.Infrastructure/Agents/DockerContainerCleanup.cs`;
`apps/tests/Shared/TestGating.cs`; `apps/tests/AI.Factory.Api.Tests/TestWorkspaceIsolation.cs`;
`apps/tests/AI.Factory.Application.Tests/Execution/DockerCleanupReconciliationTests.cs`; `docs/phase-o-report.md`.
Changed source (4): `IContainerLifecycleManager`, `DockerContainerLifecycleManager`, `HermesAgentExecutor`,
`OpenHandsExecutionAdapter`. Changed tests: the live/Docker/lifecycle/runner/OpenHands tests listed above and the three test
`.csproj` files (one linked file each). Docs: `docs/local-setup.md`, `docs/execution-runtimes.md`. Deleted from the working
tree (unstaged): `apps/tests/AI.Factory.Api.Tests/projects/` (leaked test output). **Not touched:** `SystemProcessRunner.cs`,
the API contracts, the database schema and migrations, Flutter, production defaults and profiles, every foreign Docker
resource. Nothing was committed, pushed, reset or cleaned.

# Phase O Final Result

Status: LIMITED — EXTERNAL RUNTIME CONSTRAINT

Factory-controlled defects fixed:
- D1 default `dotnet test` made a billed OpenRouter call (`.env.live` auto-loaded) — gated, `.env.live` read only on explicit opt-in
- D2 37 live tests counted as passed without running — now skipped with a reason; a guard fails any new plain-`[Fact]` live test
- D3 Docker cancel-before-container-creation race — bounded reconciliation (10 s) + periodic-sweep backstop; mutation-checked; real Docker 10/10 (was 0/10)
- D4 uncontrolled overlap of Docker/build-heavy tests, including across the three concurrently run test assemblies — serialized collections + machine-wide lock
- D5 Api tests wrote 2,371 directories (14 nested git repositories tracked by the Phase N commit) into the repository — redirected to a per-run temp dir, leaked tree removed
- D6 orphan-sweep test would have removed a developer's live Factory containers — scoped to its own container
- D7 credential-isolation matrix: ambient routing/home variables now set and asserted, safe assertion messages, host variables restored
- D8 stale Claude Code path in docs — discovery snippet

Environment blockers:
- Codex CLI not installed
- Claude Code is desktop-bundled, not on PATH (2.1.284 and 2.1.286 present; the path layout changed)
- Docker host with ~3 GB free RAM: daemon creation latency 2–12 s (handled by a bounded window, not hidden)

External credential/account blockers:
- no Claude credential (ANTHROPIC_API_KEY / CLAUDE_CODE_OAUTH_TOKEN) supplied
- no OpenAI/Codex credential
- the Phase N OAuth token is removed from the repository but its revocation/rotation is NOT VERIFIED and needs its owner; it also exists in this session's own transcript outside the repository (6 occurrences, not edited)

Not verified:
- any model-backed Factory run on Claude Code or Codex (generation, verification, acceptance, usage, cost)
- the real Codex CLI's event schema and flags
- Hermes/OpenHands success-path container cleanup in Phase O
- a live Worker-kill orphan simulation
- network-level absence of provider traffic in the default run (established by source and skip evidence only)
- token rotation/revocation

Default test safety:
PASS

Docker lifecycle safety:
PASS

Credential isolation:
PASS

Workspace integrity:
PASS

Full regression:
`dotnet test AI.Factory.slnx --no-build` ×3 on the final code: 1861 passed, 0 failed, 37 skipped, 1898 total in each (126–131 s); build 0 warnings, 0 errors

Live model-backed certification:
ENVIRONMENT BLOCKED

12-run benchmark:
NOT RUN — CERTIFICATION READINESS INCOMPLETE

Uncommitted changes:
yes

Foreign Docker resources touched:
no

