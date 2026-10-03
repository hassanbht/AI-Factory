# Benchmark: Archon vs AI Factory

Decision this benchmark feeds: keep building AI Factory's orchestration core, or adopt
[coleam00/Archon](https://github.com/coleam00/Archon) as the base and contribute the missing pieces.

## Layout

| Path | Purpose |
|---|---|
| `fixture/` | Baseline target repo: .NET 10 `Inventory` library + 5 visible tests. |
| `tasks/T*.md` | Prompts. Give the file content verbatim to both tools. Nothing else. |
| `hidden/T*.cs` | Acceptance tests. Never shown to either tool. Copied in only at scoring time. |
| `bench.ps1` | `prepare` creates an isolated git repo per run, `score` runs hidden tests and diff stats. |
| `results/*.json` | Automatic scores, one file per run. |

Hidden test totals (a build failure scores 0 of these): **T1 = 16, T2 = 9, T3 = 10**.

Validated on 2026-10-02: on the untouched baseline T1/T3 hidden tests do not compile and T2 passes 5/9;
with a reference implementation all three pass 100%.

## Tasks

| Task | Kind | What it tests |
|---|---|---|
| T1 | Feature, 1 file | Following a precise API spec. |
| T2 | Bug fix | Root-cause fix plus regression test. |
| T3 | Feature, multi-file | Planning, cross-cutting change (SKU normalisation) plus a new component. |

## Fairness rules

1. **Same model** on both sides (decide before the first run, record it below). Archon runs through
   Claude Code; AI Factory runs Hermes through OpenRouter, so pick the same model id on OpenRouter.
   The agent harness differs (Claude Code vs Hermes). That is part of what is being compared, so note it in the conclusion.
2. Each tool uses its **own native pipeline**: Archon uses a stock workflow (plan, implement, validate, review); AI Factory
   uses project create, source, plan/generate, approve, start.
3. Same prompt text, same fixture, fresh run directory per run.
4. **Human interventions:** anything typed or clicked after the initial submission, other than one planned approval
   gate, counts as an intervention. Log each one.
5. 2 runs per tool per task (12 runs). LLM output varies; a single run proves nothing.
6. Archon telemetry: turn it off before the first run (`archon telemetry status` to check).

## Per-run procedure

```powershell
# 1. Prepare an isolated repo (prints the prompt file to use)
.\bench.ps1 -Action prepare -Tool archon -Task T1 -Run 1     # creates C:\bench-runs\archon-T1-r1

# 2. Start a stopwatch, run the tool on that directory with the prompt, log interventions.

# 3. Score. Archon works in place (or a worktree: merge it back into main first).
.\bench.ps1 -Action score -Tool archon -Task T1 -Run 1

#    AI Factory clones the source into its Project.RootPath; score that clone:
.\bench.ps1 -Action score -Tool factory -Task T1 -Run 1 -RunDir '<Project.RootPath>'
```

Archon invocation (stock `archon-implement`; `archon-ship` / `archon-idea-to-pr` need a GitHub PR, run dirs are local only):

```powershell
$env:ARCHON_TELEMETRY_DISABLED = '1'
archon workflow run archon-implement --cwd C:\bench-runs\archon-T1-r1 --no-worktree `
  --config .\archon-config.yaml ([string](Get-Content -Raw -Encoding UTF8 .\tasks\T1-reservations.md))
```

AI Factory: start it with `.\start-factory.ps1` (isolated DB/workspace, model pinned), then per run:

1. `POST /api/projects` with `{ "name": "factory-T1-r1", "description": "<task file text>" }`
2. `POST /api/projects/{id}/source` with `{ "sourceType": "GitRepository", "location": "C:\\bench-runs\\factory-T1-r1", "branch": "main" }`
3. `POST /api/projects/{id}/start` with `{ "actor": "bench" }`. The Worker then drives the lifecycle itself
   (Analyzing → Specification → Architecture → Planning → Implementation → ... → HumanReview).
4. `.\monitor-factory.ps1 -ProjectId <id> -RunName factory-T1-r1` logs status changes until a gate or a terminal state.
   `POST /api/projects/{id}/approve` at `HumanReview` is the one allowed approval.

PowerShell 5.1: always pass the prompt as `[string](Get-Content -Raw -Encoding UTF8 ...)`. Without `[string]`,
`ConvertTo-Json` serialises the provider metadata (the first attempt sent a 95 MB body).

## Metrics

Automatic (from `results/*.json`): build, hidden pass rate, agent's own tests passing, files/lines changed,
files touched outside `src/` and `tests/`.

Manual (fill in the table):

- **Wall time** from submit to terminal state.
- **Cost (USD)**: Archon reports per-run usage; for AI Factory use `/api/agents/metrics` or the OpenRouter activity page.
- **Interventions**: count, see fairness rule 4.
- **Resume** (T3 run 1 only): kill the process (Archon server / AI Factory Worker) once implementation has started, restart it.
  2 = continues from where it stopped, 1 = restarts the task cleanly, 0 = stuck, corrupt or lost.
- **Audit** (0-2): from stored records alone, can you answer what was planned, what each attempt did, why it failed or passed, and who approved it?

## Results

Model used: Claude Haiku 4.5 (`claude-haiku-4-5` in Claude Code, `anthropic/claude-haiku-4.5` on OpenRouter).
Budget: at most 6 USD of OpenRouter spend in total; `run-factory.ps1` refuses to start a run once key usage reaches 5.5 USD
and logs `COST_USD` per run. Archon runs on the Claude Code subscription (no OpenRouter spend).
Supplementary (Sonnet 5.5, before the switch to Haiku): `archon-T2-sonnet-r1` 9/9 in 263 s, `archon-T2-sonnet-nobun` 9/9 in 157 s.
Archon version: v0.11.1 (binary, aa095446)   AI Factory version: 1.0.082 + uncommitted Phase I working tree

| Run | Hidden pass | Own tests | Wall time | Cost USD | Interventions | Resume | Audit | Notes |
|---|---|---|---|---|---|---|---|---|
| archon-T1-r1 | 16/16 | 26 pass | 333 s | 0 (subscription) | 0 | – | | completed, 2 files |
| archon-T1-r2 | 16/16 | 26 pass | 395 s | 0 (subscription) | 0 | – | | completed, 2 files |
| archon-T2-r1 | 9/9 | 11 pass | 275 s | 0 (subscription) | 0 | – | | completed, 2 files |
| archon-T2-r2 | 9/9 | 10 pass | 223 s | 0 (subscription) | 0 | – | | completed; left `artifacts/runs/.../implementation.md` in repo |
| archon-T3-r1 | 10/10 | 36 pass | 598 s | 0 (subscription) | 0 | – | | completed, 4 files |
| archon-T3-r2 | 10/10 | 28 pass | 416 s | 0 (subscription) | 0 | – | | completed; left `artifacts/runs/.../implementation.md` in repo |
| archon-T3-r3 (resume test) | 10/10 | 29 pass | 100 s + 524 s | 0 (subscription) | 2 (abandon, fresh run) | 1 | | Killed mid-`implement`. Run stays `running`; `resume` refuses (`running`), `--adopt` refuses (no isolation record with `--no-worktree`). Recovery = `abandon` + fresh run. |
| factory-T1-r1 | 16/16 | 26 pass | 752 s | 0.43 | 0 | – | | HumanReview reached; 2 planned tasks |
| factory-T1-r2 | 16/16 | 24 pass | stuck (~2200 s) | 0.49 | 1 (cancel) | – | | Tests task `TimedOut` after 285 s, `attemptCount 0`, never retried; project idle in `Implementation` with empty `currentError` for 25 min. Left `dotnet-install.sh` in repo. |
| factory-T2-r1 | 7/9 | 8 pass | 946 s | 0.56 | 0 | – | | Regression: non-positive quantity now `ArgumentException` (was `ArgumentOutOfRangeException`). Scope creep: rewrote test csproj (removed implicit/global usings, downgraded xunit 2.9.2→2.6.4), `Inventory.slnx`, dropped `sealed`; stray `BUILD_SUMMARY.txt`. |
| factory-T2-r2 | 9/9 | 8 pass | 676 s | 0.31 | 0 | – | | HumanReview reached; clean, 2 files +43/-1 |
| factory-T3-r1 (resume test) | 10/10 | 34 pass | 1218 s | 0.64 | 0 | 2 | | Worker killed 2 min into Implementation. After restart the lifecycle continued by itself. The orphaned AgentRun stayed `Running` and its container kept running for ~15 min (concurrent with the new run) until marked `Failed`. |
| factory-T3-r2 | 10/10 | 49 pass | 1745 s (incl. ~10 min host outage) | 0.80 | 0 | (2) | | Api+Worker were killed by the benchmark host at 12:05 (harness ended the parent job); after restart the project continued by itself to HumanReview. |

Hidden tests were made self-contained (explicit `using Xunit;` etc.) after factory-T2-r1 removed the project's global usings;
all runs were scored with the same files. Archon results are unchanged by it.

## Setup findings (count towards "operability")

Archon:

- Binary install is one command with checksum verification. `archon doctor` explains every missing piece.
- Compiled binary needs `claudeBinaryPath` (or `CLAUDE_BIN_PATH`) and `bun` on PATH for script nodes. The installer does not mention either.

AI Factory:

- Worker crashes at startup under `Development`: `OrchestrationRunWorker` (singleton hosted service) takes the scoped
  `IAuditLogger` (captive dependency, committed since Phase 17.4.13). Production skips scope validation, so it runs there
  with a long-lived scoped service shared across iterations.
- Running without a launch profile defaults to `Production`; `appsettings.Production.json` routes to
  `deepseek/deepseek-v4-flash-0731:free`, which the model preflight rejects (good: fails fast instead of substituting).
- Base `appsettings.json` has provider `openrouter` disabled and `omniroute` (not running locally) enabled.
- `plan/generate` returns 409 unless the project is in `Planning`; the autonomous path is `/start`. The API does not
  point the caller to it.
- Fresh DB has no AgentDefinitions (documented as G-1). The scheduler then fails every cycle while the project sits in
  `Planning` with an empty `currentError`: not bounded, not visible to the caller. Registered 5 definitions from
  `prompts/system/*.md` via `POST /api/agents/definitions` (setup, not counted as an intervention).
- Base `appsettings.json` leaves `Hermes:Provider` unset and `Hermes:NetworkMode` = `none`; the Planner run fails with
  "Hermes:Provider must be set explicitly". `start-factory.ps1` now mirrors the Production Hermes block.
- **Blocker (2026-10-02 09:57):** the OpenRouter key's account has no credits. Hermes reported it clearly and the
  project went to `Failed` within 45 s (good failure handling). No paid-model AI Factory run is possible until credits are added.

- **Credential precedence (security-relevant):** with the Factory's default Hermes data volume, Hermes used a different
  `OPENROUTER_API_KEY` (the one in the user's `~/.hermes/.env`, an account without credits) instead of the key the Factory
  resolves through `ISecretProvider` and injects with `-e`. The Factory believes it controls which credential (and
  therefore which account and budget) a run uses; it does not. Fixed for the benchmark with an isolated
  `Hermes__DataVolume`. Two runs were lost to it (`factory-T1-oldkey`, `factory-T2-oldkey`, 0 USD).

Archon (additional):

- `title.generate_failed` warning on every run: title generation ignores `claudeBinaryPath`. Cosmetic, the run continues.

## Totals (Haiku 4.5, 6 runs per tool)

| | Archon | AI Factory |
|---|---|---|
| Hidden tests passed | **70/70 (100%)** | 68/70 (97%) |
| Runs fully correct | 6/6 | 5/6 (T2-r1 regressed existing validation) |
| Median wall time | ~6 min | ~16 min |
| OpenRouter spend | 0 (Claude Code subscription) | **3.23 USD** |
| Human interventions | 0 | 1 (T1-r2 stuck, cancelled) |
| Scope creep | 2 runs left a run artifact in the repo | T2-r1 rewrote project files; T1-r2 left `dotnet-install.sh` |
| Resume after crash | 1 (manual abandon + fresh run) | **2** (continued by itself, twice) |
| Audit | 1 (rich transcript; no plan/approval in `archon-implement`) | **2** (plan hash, per-attempt records, actor, approval gate; no token/cost data) |
| Setup to first correct result | 1 install + `bun` + binary path | 9 config fixes (see findings) |

Confounder: the agent harness differs (Claude Code inside Archon; Hermes + OpenHands inside AI Factory). Most of the
quality and speed gap is the harness, not the orchestrator; most of the resume/audit gap is the orchestrator.

## Decision rule (agreed before running)

Per tool, total: hidden pass rate over all runs, interventions, cost, resume score, audit score.

- Archon reaches at least 80% of AI Factory's hidden pass rate **and** is not worse on resume and audit: adopt Archon,
  contribute durable state, retry and audit features there.
- Otherwise keep AI Factory as the control plane and borrow Archon's workflow-definition ideas.

**Outcome:** Archon passes the pass-rate bar (100% vs 97%) but is worse on resume (1 vs 2) and audit (1 vs 2), so by the
pre-agreed rule: **keep AI Factory as the control plane.** The data also says where to spend effort: the orchestration
core is the part that held up; the execution layer and the operability gaps are what lost. Next steps, in order:

1. Fix the orchestration defects this run found: no retry after a `TimedOut` task (`attemptCount 0`, project stuck),
   orphaned AgentRun/container after a Worker crash, captive `IAuditLogger`, Hermes credential precedence.
2. Make the base configuration runnable end to end (provider flags, `Hermes:Provider`, network, agent definitions).
3. Add a Claude Code (or Archon) execution adapter next to Hermes/OpenHands and rerun this benchmark: that isolates the
   harness effect and is the cheapest path to Archon-level output quality.

## Phase M rerun (prepared, NOT run)

Phase M fixed items 1–3 (see `docs/phase-m-report.md`). The protocol, fixture, prompts, hidden tests, run count and
scoring are unchanged. One command per execution runtime; the orchestrator is identical in all four:

```powershell
.\start-factory.ps1 -Runtime Hermes        # baseline
.\start-factory.ps1 -Runtime OpenHands
.\start-factory.ps1 -Runtime ClaudeCode    # ANTHROPIC_API_KEY or CLAUDE_CODE_OAUTH_TOKEN in the shell
.\start-factory.ps1 -Runtime Codex         # OPENAI_API_KEY; runs an OpenAI model (-CodexModel): confounder
.\run-factory.ps1 -Task T1 -Run 1          # then T1..T3, runs 1..2, exactly as before; score with bench.ps1
.\start-factory.ps1 -Stop
```

`-Runtime` routes Planner/Implementation/Test/Repair to that runtime; Review keeps its explicit Hermes route. The
`Hermes__DataVolume` workaround is gone: every Hermes run now gets its own empty home.

**Status 2026-10-02: not executed.** The Phase M budget was zero spend (no OpenRouter/OpenAI/Anthropic API usage),
and no Anthropic or OpenAI API credential was available. The results tables above remain the only measured data.
