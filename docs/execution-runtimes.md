# Execution runtimes (Phase M)

AI Factory is the control plane. It owns Project/WorkItem/AgentRun lifecycle, retries, deadlines, cancellation,
recovery, verification, acceptance, review, approval and audit. Coding-agent CLIs are interchangeable
**execution runtimes** behind one provider-neutral boundary and never become a second control plane.

```text
API / Flutter → Control Plane (Project, WorkItem DAG, AgentRun, Acceptance, Review, Approval, Audit)
             → IAgentExecutionDispatcher (AdapterDispatchingAgentRuntime)
             → Execution profile → IExecutionAdapter: Hermes | OpenHands | ClaudeCode | Codex
             → model / provider / gateway
```

## Adapter contract

`IExecutionAdapter.ExecuteAsync(AgentExecutionSpec) → ExecutionOutcome`. An adapter executes exactly one spec and
reports one of `Succeeded | Failed | TimedOut | Cancelled`, a `FailureCategory`, an exit reason, token usage and an
`ExecutionRuntimeReport` (session id, cost, request id, version, turns, timestamps) — every field nullable, filled
only from the runtime's own structured output. Adapters never touch repositories, AgentRun/WorkItem state,
artifacts or acceptance; architecture tests enforce this (`RuntimeBoundaryArchitectureTests`,
`ExecutionAdapterArchitectureTests`).

| | Hermes | OpenHands | ClaudeCode | Codex |
|---|---|---|---|---|
| Factory-routed model (`FactoryRoutedModel`, the only declared capability) | yes | yes | yes (explicit map) | yes (explicit map) |
| Structured result | run report file | conversation state | `stream-json` result event | `--json` JSONL events |
| Usage | when reported | when reported | `result.usage` | `turn.completed.usage` |
| Cost | Hermes' own *estimate* (`hermes:estimated_cost_usd(<status>)`) | not exposed | `total_cost_usd` (`claude:total_cost_usd`) | not exposed |
| Session id | `session_id` | conversation id | = AgentRun id | `thread_id` |

No further capability flags were added: nothing in the Factory branches on them yet.

## Profiles

`Execution` (appsettings): `DefaultProfile`, `Profiles{name → Adapter[, Gateway]}`, `AgentTypeProfiles{AgentType →
profile}`, `Adapters{key → Enabled}`. The base Worker configuration now declares `ClaudeCode` and `Codex` profiles,
but **no AgentType is mapped to them** — defaults did not change, and Review keeps its explicit route. Select a
runtime per AgentType, e.g. `Execution__AgentTypeProfiles__Implementation=ClaudeCode`.

## Claude Code adapter (`ClaudeCode`)

Verified against Claude Code **2.1.121** in Phase M and **re-verified against 2.1.284** in Phase N (every flag below is
present in `claude --help`; the `init`/`api_retry`/`result` event shapes were observed from the real binary — see
[phase-n-report.md](phase-n-report.md)). On this machine the binary is the Claude desktop app's bundled copy, not on
PATH: set `ClaudeCode__ExecutablePath`.

```text
claude -p --output-format stream-json --verbose --model <mapped> --session-id <AgentRunId>
       --no-session-persistence --permission-mode dontAsk --setting-sources "" --strict-mcp-config
       --disable-slash-commands [--bare] [--max-budget-usd N] --allowedTools <list>
stdin: system prompt + Factory instructions        cwd: the Factory-resolved workspace
```

- **Model**: `ClaudeCode:Models` maps the routed Factory model id to a Claude model. Unmapped, missing or `auto`
  ⇒ the run fails closed (Validation) without launching.
- **Permissions**: `dontAsk` + an explicit `AllowedTools` list (default: file tools, `Bash(dotnet *)`, read-only git).
  Nothing prompts, nothing outside the list is permitted. No user/project/local settings, hooks, plugins or MCP.
- **Credential**: Factory secret `ClaudeCode:CredentialSecretName` injected as `ANTHROPIC_API_KEY` (adds `--bare`:
  auth strictly that key) or `CLAUDE_CODE_OAUTH_TOKEN` (subscription token from `claude setup-token`). Child
  environment only — never argv, never a file.
- **Result**: success needs the final `result` event with `is_error:false`, `subtype:"success"` **and** exit 0.
  Observed live: an unauthenticated run reports `subtype:"success"` with `is_error:true`.
- **Auth failures**: Claude Code retries a 401 ten times with exponential backoff (observed live, minutes). The first
  `api_retry` with 401/403 aborts the run as `Provider`/`Authentication`.
- **Resume**: not implemented. A retry is a new AgentRun owned by the Factory; sessions are not persisted.

## Codex adapter (`Codex`)

Verified against **codex-cli 0.160.0** (`codex exec --help`, live probes):

```text
codex exec --json --ephemeral --skip-git-repo-check --ignore-user-config --ignore-rules --color never
           -c approval_policy="never" -s workspace-write -m <mapped> -C <workspace> -
stdin: system prompt + Factory instructions
```

- **Executable**: on Windows npm installs only a `codex.cmd` shim on PATH; set `Codex:ExecutablePath` to the vendored
  native `codex.exe` (`…/@openai/codex-win32-x64/vendor/x86_64-pc-windows-msvc/bin/codex.exe`).
- **Policy**: approvals `never`, sandbox `workspace-write` (or narrower `read-only`); broader sandboxes are refused at
  startup.
- **Credential**: Factory secret `Codex:CredentialSecretName` (default `OPENAI_API_KEY`) injected as `CODEX_API_KEY`.
  Codex strips `*KEY*` variables from the shells it spawns.
- **Result**: success needs `turn.completed`, no `turn.failed`, exit 0. The output is the last `agent_message`.
  Upstream request ids are recorded only when Codex printed one (`request id: req_…`). The first 401 aborts the run.
- **Model**: `Codex:Models`, fail closed as above.

## Credential precedence (all runtimes)

`Factory ISecretProvider → per-run credential → isolated runtime environment`. Nothing else can win:

| Runtime | Isolation | Ambient sources that can no longer apply |
|---|---|---|
| Hermes | `HERMES_HOME` (`/opt/data`) is a per-container **anonymous volume** (`-v /opt/data`), removed with the container (`--rm`, `rm -f -v`). `HermesOptions.DataVolume` was deleted. | `~/.hermes/.env` (Hermes loads it with `override=True`), persisted provider config |
| OpenHands | unchanged (Agent Server container, Factory-injected key) | — |
| Claude Code | `CLAUDE_CONFIG_DIR`/`HOME`/`APPDATA`/`TEMP` → `{StateRoot}/{runId}/home` | `~/.claude` login, settings, hooks, plugins, MCP, CLAUDE.md |
| Codex | `CODEX_HOME`/`HOME` → run home, `--ignore-user-config`, `--ignore-rules`, `--ephemeral` | `~/.codex/auth.json`, `config.toml`, rules |

CLI children get an explicit environment: an allow-list of host variables (`CliRuntimes:InheritedEnvironmentVariables`,
anything credential-like dropped), the isolated home variables, and the one credential. A configured credential
that cannot be resolved stops the run before launch (Hermes, Claude Code, Codex). The exact credential value is
redacted from anything a CLI prints.

## Lifecycle guarantees

- **Timeout** — the Factory deadline (AgentRun) bounds every runtime; the adapter ceiling only shortens it. On
  expiry the process tree is killed (bounded wait), runtime state is removed, the run is `TimedOut`.
- **Finished CLI, lingering descendant** (Phase N) — `SystemProcessRunner` observes the root's exit itself and then
  gives the redirected pipes a bounded 5 s to drain. A process the CLI started (build server, MSBuild node) that keeps
  the inherited pipes open no longer turns a finished run into `TimedOut`; such a descendant is not killed.
- **Retry** — every runtime failure consumes one attempt of the WorkItem's existing budget
  (`AttemptCount`/`MaxAttempts`, default 3). Timeouts and Infrastructure failures (incl. orphan recovery) return the
  WorkItem to `Ready` (`Running → Ready`, a new AgentRun with `Attempt = n+1`); others fail with the reason. A
  `TimedOut` run with `attemptCount 0` and an idle project can no longer happen.
- **Dead ends** — a terminally Failed/Cancelled WorkItem fails the Project closed (once no AgentRun is active) with
  the WorkItem's reason; a Blocked one is surfaced on `Project.CurrentError`. A missing Planner definition fails the
  Project closed with an actionable message instead of looping.
- **Crash recovery** — `AgentRunOrphanRecovery` (Worker startup + every `Recovery:PeriodicSweepInterval`, 1 min):
  a Running AgentRun without a heartbeat for `Recovery:OrphanHeartbeatTimeout` (2 min; heartbeats every 30 s) gets,
  in order, (1) its runtime terminated via every `IContainerLifecycleManager` (Docker containers, local CLI process
  trees), (2) a conditional `Running → Failed` under its concurrency token (exactly one of several overlapping
  Workers wins), (3) the WorkItem retry budget applied. Step 1 before 3 means a retry never runs next to the old
  runtime. The Api's startup recovery is heartbeat-bounded too (it used to mark every Running WorkItem orphaned).
- **Cancellation** — terminal statuses are never overwritten: `AgentRunRepository.UpdateAsync` no longer merges a
  concurrency conflict when the other writer changed `Status`, so a late completion cannot turn `Cancelled` (or an
  orphan-recovered `Failed`) into `Succeeded`. Repeated cancel requests change nothing (the API keeps answering 409).
- **Agent-reported failure** (Phase P) — a run that exits successfully but whose own valid structured result says
  `Failed`, `TimedOut` or `Cancelled` fails the WorkItem with the agent's reason (`ExecutionReportedFailure`); it never
  enters verification, acceptance or review. The AgentRun keeps its runtime-level status (by design the self-report never
  drives `AgentRunStatus`); the report stays in its structured result. A missing or malformed report is unchanged: the
  Factory's own verification decides.
- **Cleanup** — Hermes: container + anonymous home removed. CLI runtimes: `{StateRoot}/{runId}` deleted on every
  path; after a crash `LocalProcessLifecycleManager` kills the recorded process tree (pid + start time, so a
  recycled pid is never killed) and deletes the directory.
- **Docker cancellation / timeout reconciliation** (Phase O) — cancelling kills the `docker run` client; if that happens
  before the Docker daemon has created the container, the daemon can still create it afterwards (observed:
  `docker rm -f` of a missing container exits 0 with no output, so a single removal cannot tell "never existed" from
  "not created yet"; measured on this host with launches cancelled together: the container was found 1.8–8.5 s after
  cleanup began with four launches, 2.8–11.8 s with eight, and with eight a fixed 5 s window missed 2–3 of the 8; a single
  cancelled Hermes launch was cleaned up in 10 of 10 runs, latency not measured). After an **interrupted** launch the Hermes executor and the OpenHands
  adapter therefore keep removing *this run's* container (`DockerContainerCleanup`, by its deterministic name
  `ai-factory-agent-<run id>` — nothing else is ever listed, filtered or removed) for a bounded window: 10 s,
  polling 250 ms backing off to 1 s, ending early the moment a removal succeeds, with an explicit outcome
  (`Removed` / `NotFound` / `Failed`) logged as `ContainerCleanup`. The ordinary path (the container exists, or the
  launch finished normally) is one removal with no waiting. A container the daemon creates after the window belongs to a
  terminal AgentRun and is removed by the periodic orphan sweep, so a cancelled launch converges to zero within the window
  plus `Recovery:PeriodicSweepInterval` (1 min by default, configurable).

## Workspace integrity (Factory verification)

`VerificationGate` runs `WorkspaceIntegrityPolicy` over `git status` vs HEAD (untracked and git-ignored files
included, build output excluded) before build/test. Violations are a **repairable** verification failure whose
summary names each one, so the repair agent gets exact instructions:

- runtime state/sessions/caches (`.claude/`, `.codex/`, `.hermes/`, `artifacts/runs/`, `*.sqlite`, `.aif-*`), credentials
  (`.env*`, `auth.json`, keys), logs/temp files, installers (`dotnet-install.*`), stray reports (`*_SUMMARY.*`);
- modified/deleted build infrastructure (`*.sln[x]`, `global.json`, `Directory.Build.*`, `nuget.config`, `.gitignore`);
- changed or removed versions of existing dependencies (csproj/props/package.json) and changed project settings
  (`TargetFramework`, `ImplicitUsings`, `Nullable`, `LangVersion`) — adding a new package is allowed;
- anything that looks like a credential written into a file.

A workspace without a git baseline reports "integrity unavailable" and keeps the build/test semantics.

## Usage / cost telemetry

Token counts go to the AgentRun columns. Everything else the runtime reported goes to the AgentRun's structured
result metadata: `RuntimeSessionId`, `CostUsd` + `CostSource`, `UpstreamRequestId`, `RuntimeTurns`, `RuntimeVersion`,
`RuntimeExitCode`, `RuntimeStartedAtUtc`, `RuntimeCompletedAtUtc`, `RuntimeReportedModel`, plus the existing
`ExecutionAdapter`, `Gateway`, `ExitReason`. **Absent key = not exposed**; nothing is zero-filled or inferred.

## Test categories and live opt-in (never automatic)

The default `dotnet test apps/AI.Factory.slnx` makes no provider call, reads no `.env.live` and needs no credential.
Anything that needs more is **skipped by xunit with a reason** (`SKIPPED — LIVE OPT-IN NOT ENABLED (set …)`, or
`ENVIRONMENT BLOCKED — …` when you opted in but a credential, URL, executable path or Docker is missing). A skipped test
proves nothing about a runtime. The rules live in `apps/tests/Shared/TestGating.cs` (compiled into each test project);
`LiveTestGateTests` fails the test run for any new live-shaped test that uses a plain `[Fact]`.

| Cat. | Tests | Default run | Enable | Cost |
|---|---|---|---|---|
| A, B | unit and local integration (SQLite, git, fake CLIs with real processes, real `dotnet build`) | yes | — | none |
| C | `DockerLifecycleIntegrationTests`, `docker compose config` checks | yes, if Docker is reachable | — | none (no model, no credential) |
| D | `ClaudeCodeLiveContractTests` — the real CLI against a local scripted endpoint; **not** model generation | no | `AI_FACTORY_LIVE_CLAUDE_CONTRACT=1` + `ClaudeCode__ExecutablePath=<claude.exe>` | none |
| D | `HermesCredentialPrecedenceLiveTests` | no | `AI_FACTORY_LIVE_HERMES_CREDENTIALS=1` (Docker + Hermes image) | none — local recording provider |
| D | `OpenHandsStateIsolationLiveTests` | no | `AI_FACTORY_OPENHANDS_SMOKE_TEST=true` (Docker + pinned image) | none — fake key, closed loopback |
| E | `CliRuntimeLiveSmokeTests` | no | `AI_FACTORY_LIVE_SMOKE=ClaudeCode` or `Codex` + the credential | runtime/provider usage |
| E | `RealHermes*`, `RealOpenHands*`, `Live*E2E`, `HermesSmokeTest`, `ReviewExecutionLiveTests.LiveReview…` | no | `AI_FACTORY_HERMES_SMOKE_TEST=true` (OpenHands: or `AI_FACTORY_OPENHANDS_SMOKE_TEST`) + the key | OpenRouter usage |
| E | `ModelBackedCertificationTests` (Phase P, one runtime per run) | no | `AI_FACTORY_LIVE_CERT_HERMES=1` or `AI_FACTORY_LIVE_CERT_OPENHANDS=1` + `AI_FACTORY_CERT_MODEL` + `OPENROUTER_API_KEY` (Docker) | OpenRouter usage |
| E | `GatewayLiveAgentRunTests`, `RuntimeBenchmarkE2ETests.Live*` / connectivity | no | `AI_FACTORY_LIVE_GATEWAY_AGENTRUN=true` / `AI_FACTORY_LIVE_BENCHMARK=true` + `OPENROUTER_API_KEY` | OpenRouter usage |

Commands and credential handling: [local-setup.md](local-setup.md#running-the-tests-phase-o). Run the Docker opt-in suites
one at a time; the Docker and real-build tests hold a machine-wide lock so they never overlap each other, whichever test
assembly they are in.

## Limitations

- **Independent review is not effective** (Phase P, observed live — see `docs/phase-p-report.md`): the reviewer is given
  only the changed-file *paths* (no content, no diff) in an empty scratch workspace, and `AgentCodeReviewer` parses the
  *whole* runtime output as JSON. Hermes prints its container start-up log first, so a real findings array (the Phase P
  reviewer returned one in a fenced block) is discarded and stored as a single non-blocking Info finding. A completed
  WorkItem therefore proves verification + acceptance criteria, not a code review.
- The workspace-integrity policy is a deny-list: an unrequested file with an arbitrary name (Phase P: an untracked file
  named U+F00D left by an OpenHands agent) passes verification, and acceptance criteria only check required content.
- Automatic model ids (`auto/…`) are not rejected by the Factory itself (the shipped catalog still lists
  `auto/coding:free`); the Phase P certification harness rejects them. Hermes/OpenHands fall back to their configured
  `DefaultModel` when a run carries no model; only the CLI adapters reject an unmapped model.
- Hermes' own usage figures are persisted verbatim; its `total_tokens` need not equal input + output (Phase P:
  99,662 + 4,776 vs. 389,622).
- Claude Code / Codex generation is **not verified live** (Phases M, N, O and P: no credential available; Codex CLI
  not installed on this machine). Isolation, credential routing, the 401 abort, timeout and cancellation were verified
  against the real Claude Code binary (2.1.284 in Phase N; 2.1.284 and 2.1.286 in Phase O) with synthetic credentials and a
  scripted local endpoint — never a model.
- Docker cancellation (fixed in Phase O, see "Lifecycle guarantees"): the immediate reconciliation window is 10 s, not
  unbounded. A daemon slower than that leaves a Created container for the periodic orphan sweep to remove (bounded by
  `Recovery:PeriodicSweepInterval`, 1 min by default); `DockerContainerCleanup` reports `NotFound` in that case, never `Removed`.
- `OpenHandsStateIsolationLiveTests` is timing-sensitive on a loaded Docker host: the Agent Server must become healthy
  within `OpenHands:StartupTimeoutSeconds` (45 s); with two Hermes containers starting in parallel it failed 2 of 4
  times (TimedOut), alone / on an idle host it passed. Run the Docker live suites one at a time.
- Codex runs an OpenAI model — a benchmark confounder against Claude-model runtimes.
- Process-tree kill walks the tree at kill time; a grandchild already re-parented (its parent exited first) is not
  found. Container removal and the per-run reaper cover the crash cases.
- Private NuGet feeds configured in the user's `%APPDATA%\NuGet\NuGet.Config` are not visible to CLI runs (the home
  is isolated); the package cache (`NUGET_PACKAGES`) is shared.
- The integrity policy is deliberately conservative: a WorkItem that genuinely needs to edit a solution file or bump a
  dependency fails verification and needs a human.
