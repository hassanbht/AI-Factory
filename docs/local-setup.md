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

## Claude Code on this machine (Phase N)

The Claude desktop app bundles the CLI (`%APPDATA%\Claude\claude-code\<version>\claude.exe`, 2.1.284 at the time of
Phase N); it is not on PATH. Point the adapter at it with `ClaudeCode__ExecutablePath`. Codex is not installed here.

```powershell
# Free, offline: the real claude.exe against a local scripted endpoint (not model generation)
$env:AI_FACTORY_LIVE_CLAUDE_CONTRACT = '1'
$env:ClaudeCode__ExecutablePath = "$env:APPDATA\Claude\claude-code\2.1.284\claude.exe"
dotnet test apps/tests/AI.Factory.Application.Tests --filter ClaudeCodeLiveContractTests

# Real model-backed smoke (subscription quota): token from `claude setup-token`, kept in the environment only
$env:CLAUDE_CODE_OAUTH_TOKEN = '<token>'; $env:AI_FACTORY_LIVE_SMOKE = 'ClaudeCode'
dotnet test apps/tests/AI.Factory.Worker.Tests --filter CliRuntimeLiveSmokeTests --logger "console;verbosity=detailed"
```

The benchmark has a one-command equivalent per runtime: `bench\archon-vs-factory\start-factory.ps1 -Runtime ClaudeCode`.
