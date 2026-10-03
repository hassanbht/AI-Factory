# Local setup (Phase M)

One command checks prerequisites, builds, starts Api + Worker in `Development` and registers the default
AgentDefinitions. It is idempotent.

```powershell
.\scripts\bootstrap-factory.ps1 -Start
```

Without `-Start` it expects an Api already running at `http://localhost:5223` (`-Api` to change).

## What the bootstrap covers (benchmark: nine manual fixes before the first correct run)

| Concern | How it is handled now |
|---|---|
| Database initialisation | `-Start` runs the Api with `Database__AutoMigrate=true`; a fresh SQLite DB is created on first start. |
| AgentDefinitions | Registers one enabled definition per dispatched AgentType (Planner, Implementation, Test, Repair, Review, Documentation) if missing. A Project started without a Planner definition now fails closed with an actionable message instead of looping silently. |
| Provider configuration / `Hermes:Provider` / network | `appsettings.Development.json` (Worker): `Hermes:Provider=openrouter`, key injected from secret `OPENROUTER_API_KEY`, `NetworkMode=bridge`. |
| Workspace location | Both Development overlays resolve project workspaces under the repository's `projects/` — never under `apps/src/...` (stray workspaces there used to break the Factory build; the Api/Worker csproj now also exclude `projects/**`). |
| Execution budget | Worker Development overlay: `AgentWorker:ExecutionTimeout = 15 min` (the 5-minute base budget timed out a test-writing task at 285 s). |
| Runtime registration | Hermes, OpenHands, ClaudeCode and Codex adapters are always registered; selecting one is `Execution:AgentTypeProfiles` (no default changed). |
| Runtime executable discovery | Bootstrap reports `docker`, `claude`, `codex` versions. Codex on Windows: set `Codex__ExecutablePath` to the native `codex.exe` (npm puts only a `codex.cmd` shim on PATH). |
| `/start` vs `plan/generate` | The 409 from `plan/generate` now names `POST /api/projects/{id}/start` as the autonomous path. |
| Development scope validation | The Worker starts under `Development` (the captive `IAuditLogger` crash is fixed). |

## Required external credentials (names only — set them in the environment, never in appsettings)

| Secret | Needed for |
|---|---|
| `OPENROUTER_API_KEY` | Hermes and OpenHands via OpenRouter (and the default Review route) |
| `ANTHROPIC_API_KEY` *or* `CLAUDE_CODE_OAUTH_TOKEN` | ClaudeCode adapter. For the token run `claude setup-token` (subscription) and set `ClaudeCode__CredentialSecretName`/`ClaudeCode__CredentialEnvironmentVariable` to `CLAUDE_CODE_OAUTH_TOKEN`. |
| `OPENAI_API_KEY` | Codex adapter |

The Factory never falls back to a CLI's own login (`~/.claude`, `~/.codex`, `~/.hermes`); a missing credential stops
the run before launch. See [execution-runtimes.md](execution-runtimes.md).

## Selecting a runtime and model

```powershell
$env:Execution__AgentTypeProfiles__Implementation = 'ClaudeCode'
$env:Execution__AgentTypeProfiles__Repair = 'ClaudeCode'
# Explicit mapping: the routed Factory model id -> the CLI model. Unmapped models fail closed.
${env:ClaudeCode__Models__anthropic/claude-haiku-4.5} = 'claude-haiku-4-5'
```

## Claude Code on this machine (Phase N, discovery updated in Phase O)

The Claude desktop app bundles the CLI at `%APPDATA%\Claude\claude-code\<version>\<hash>\claude.exe` (2.1.284 and
2.1.286 were present in Phase O); it is not on PATH. Never hard-code a version: discover the newest one and point the
adapter at it with `ClaudeCode__ExecutablePath`. Codex is not installed here.

```powershell
$root = "$env:APPDATA\Claude\claude-code"
$env:ClaudeCode__ExecutablePath = (Get-ChildItem $root -Recurse -Filter claude.exe |
    Sort-Object { [version](($_.FullName.Substring($root.Length + 1)) -split '\\')[0] } -Descending |
    Select-Object -First 1).FullName
& $env:ClaudeCode__ExecutablePath --version   # record it; the contract tests below drive the adapter's real argv through it
```

The commands that use it are in "Running the tests" below. The benchmark has a one-command equivalent per runtime:
`bench\archon-vs-factory\start-factory.ps1 -Runtime ClaudeCode`.

## Running the tests (Phase O)

### The normal run: no provider call, no credential, no `.env.live`

```powershell
dotnet test apps/AI.Factory.slnx
```

Makes **zero** requests to a model provider and spends nothing: it never reads `.env.live`, never starts a model-backed
runtime and needs no API key. A test that needs something real is **skipped by xunit with a reason**, never "passed by
returning early":

| Reason shown by xunit | Meaning |
|---|---|
| `SKIPPED — LIVE OPT-IN NOT ENABLED (set VAR or VAR)` | the explicit opt-in variable is not set; the test did not run |
| `ENVIRONMENT BLOCKED — opt-in enabled but required credential variable(s) not set: NAME` | you opted in but the credential / URL / executable path (names only, never values) is missing |
| `ENVIRONMENT BLOCKED — Docker daemon not reachable` | a Docker test on a machine without a running daemon |

A skipped live test is **not** evidence that a runtime works. Only a live test that actually ran and passed is — and
only for what that test proves (see the categories below).

| Cat. | What | In the normal run | To run it |
|---|---|---|---|
| A | pure unit tests | yes | — |
| B | local integration: SQLite, git, the real `SystemProcessRunner` with fake CLIs, real `dotnet build`/`test` verification, `WebApplicationFactory` hosts | yes | — |
| C | Docker integration, no model, no credential: `DockerLifecycleIntegrationTests`, `docker compose config` checks | yes **if** Docker is reachable (else skipped, `ENVIRONMENT BLOCKED`) | — |
| D | the real CLI/container against a local fake endpoint — **not model generation**: `ClaudeCodeLiveContractTests`, `HermesCredentialPrecedenceLiveTests`, `OpenHandsStateIsolationLiveTests` | no | opt-in variable, below |
| E | real provider / model-backed: the `RealHermes*`, `RealOpenHands*`, `Live*`, `GatewayLive*`, `HermesSmokeTest`, `ReviewExecutionLiveTests.LiveReview…`, `RuntimeBenchmarkE2ETests.Live*`, `CliRuntimeLiveSmokeTests` | no | opt-in variable **and** the credential, below |

Test isolation (so heavy tests cannot starve unrelated ones): process-spawning tests run one at a time inside their
assembly; Docker and real-build tests additionally hold a machine-wide lock (a file in `%TEMP%`), because
`dotnet test AI.Factory.slnx` runs the three test assemblies concurrently and they share one Docker daemon and one CPU.
Everything else runs in parallel as before. The Api tests write their project workspaces to a per-run temp directory,
not into the repository.

### Non-billed contract tests (category D) — explicit, run them one at a time

```powershell
# Real claude.exe against a scripted local endpoint: credential precedence, 401 abort, deadline, cancellation, tree kill
# (needs ClaudeCode__ExecutablePath from the discovery snippet above)
$env:AI_FACTORY_LIVE_CLAUDE_CONTRACT = '1'
dotnet test apps/tests/AI.Factory.Application.Tests --filter ClaudeCodeLiveContractTests

# Hermes: the old mount style leaks the ambient key, the Phase M executor sends only the Factory key (needs Docker + the Hermes image)
$env:AI_FACTORY_LIVE_HERMES_CREDENTIALS = '1'
dotnet test apps/tests/AI.Factory.Worker.Tests --filter HermesCredentialPrecedenceLiveTests

# OpenHands: no runtime state in the workspace, no container left (needs Docker + the pinned image; fake key, no provider contacted)
$env:AI_FACTORY_OPENHANDS_SMOKE_TEST = 'true'
dotnet test apps/tests/AI.Factory.Worker.Tests --filter OpenHandsStateIsolationLiveTests
```

Unset the variable afterwards (`Remove-Item Env:AI_FACTORY_...`) — `AI_FACTORY_OPENHANDS_SMOKE_TEST` and
`AI_FACTORY_HERMES_SMOKE_TEST` also switch on the billed category-E suites for any later, unfiltered `dotnet test`.

### Model-backed certification (category E) — explicit, billed or quota-consuming

Each needs its opt-in variable **and** its credential, set in the environment of that one process (never in a file you
commit, never in appsettings, never on a command line):

| Suite | Opt-in | Credential |
|---|---|---|
| Claude Code through the Factory path | `AI_FACTORY_LIVE_SMOKE=ClaudeCode` | `ANTHROPIC_API_KEY` or `CLAUDE_CODE_OAUTH_TOKEN` (from `claude setup-token`) |
| Codex through the Factory path | `AI_FACTORY_LIVE_SMOKE=Codex` + `Codex__ExecutablePath` | `OPENAI_API_KEY` |
| Hermes / OpenHands live suites | `AI_FACTORY_HERMES_SMOKE_TEST=true` (OpenHands: also `AI_FACTORY_OPENHANDS_SMOKE_TEST`) | `OPENROUTER_API_KEY`, or the key named by `AI_FACTORY_LIVE_API_KEY_SECRET` |
| Gateway agent runs | `AI_FACTORY_LIVE_GATEWAY_AGENTRUN=true` | `OPENROUTER_API_KEY` (OmniRoute case: also `AI_FACTORY_LIVE_OMNIROUTE_URL`) |
| Benchmark live runs / connectivity check | `AI_FACTORY_LIVE_BENCHMARK=true` or `AI_FACTORY_RUN_BENCHMARK=true` | `OPENROUTER_API_KEY` |
| Phase P certification — Hermes | `AI_FACTORY_LIVE_CERT_HERMES=1` + `AI_FACTORY_CERT_MODEL=<explicit id>` | `OPENROUTER_API_KEY` |
| Phase P certification — OpenHands | `AI_FACTORY_LIVE_CERT_OPENHANDS=1` + `AI_FACTORY_CERT_MODEL=<explicit id>` | `OPENROUTER_API_KEY` |

```powershell
$env:AI_FACTORY_LIVE_SMOKE = 'ClaudeCode'; $env:CLAUDE_CODE_OAUTH_TOKEN = '<token>'   # in this shell only
dotnet test apps/tests/AI.Factory.Worker.Tests --filter CliRuntimeLiveSmokeTests --logger "console;verbosity=detailed"
```

Claude Code without an API key: run `claude setup-token` once (browser login, subscription) with the discovered
`claude.exe`, and put the token in `CLAUDE_CODE_OAUTH_TOKEN` of the shell that starts the test. The adapter accepts it
(`ClaudeCode:CredentialEnvironmentVariable=CLAUDE_CODE_OAUTH_TOKEN`) and drops `--bare` for it. The Factory never reads your
`~/.claude` login: every run gets its own HOME/config directory.

Phase P certification (`ModelBackedCertificationTests`): one runtime per process, one explicit model (any `auto` id is
rejected), no fallback, the shipped OpenRouter gateway asserted (not overridden), a fresh disposable git repository
(`Calculator.Multiply` + `Multiply_ReturnsProduct`). It runs the real Worker path — scheduler → AgentRunWorker → adapter →
verification gate → acceptance criteria → review — then checks the result independently (changed files, `dotnet test`,
runtime state, the credential in workspace and logs, Factory-owned containers immediately and after 15 s). Billed; stop the
compose Worker first (`docker compose stop worker`): its orphan sweep watches the same container labels.

```powershell
$env:AI_FACTORY_LIVE_CERT_OPENHANDS = '1'; $env:AI_FACTORY_CERT_MODEL = 'qwen/qwen3-coder'   # in this shell only
dotnet test apps/tests/AI.Factory.Worker.Tests --filter "FullyQualifiedName~ModelBackedCertificationTests.OpenHands" --logger "console;verbosity=detailed"
Remove-Item Env:AI_FACTORY_LIVE_CERT_OPENHANDS, Env:AI_FACTORY_CERT_MODEL
```

The gitignored `.env.live` at the repository root can supply credentials, but only for an opted-in test that declares which
variables it needs, only for variables not already set, and it can never switch an opt-in on (any `*SMOKE_TEST` or opt-in
key in the file is ignored). Without an opt-in it is not read at all. The gate's skip reasons and the loader name variables, never values.

### Docker requirements

A reachable daemon (`docker info`). Category C needs the `nousresearch/hermes-agent:0.20.6` and `alpine` images locally;
the OpenHands opt-in test needs the pinned `ghcr.io/openhands/agent-server` image (`OpenHands:DockerImage`). The Docker
tests were developed on a 16 GB host with about 3 GB free; they are sensitive to host load, which is why they never overlap.
