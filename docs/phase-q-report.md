# Phase Q Result — End-to-End Benchmark & Autonomous Software Delivery

## Phase Q.3 implementation and readiness (2026-10-09)

**Gate: LIMITED — COST EVIDENCE NOT CERTIFIED.** Q.3 added candidate SQLite generation/session ledger records, OpenRouter response/SSE parsing and usage validation, an injected-HTTP generation metadata client, a decimal $2.00 budget calculator, and non-billed ledger/parser/budget tests. This is implementation/mock evidence only: `providerCostCaptureVerified` remains `false`, and the Epoch-2 manifest/summary remain untouched with all twelve identities `NOT_RUN` / `NOT_CERTIFIED`. Final validation passed: build (0 warnings/errors), focused accounting tests (25/25), three consecutive full-solution TRX regressions (each 1,993 total / 1,954 passed / 0 failed / 39 explicitly gated skips / 0 ungated), post-interlock PowerShell AST checks, and read-only exact Epoch-2 invariants. No live provider generation or matrix sample ran.

### Actual-path architecture and blocker

The production path remains `Epoch identity → WorkItem → AgentRun → HermesExecutionAdapter → HermesAgentExecutor → Hermes inside Docker → OpenRouter`. The prototype capture proxy binds only to host loopback. Hermes executes in a Docker bridge network, and no supported, verified route from its container namespace to that listener was demonstrated. The coordinator is deliberately not activated or injected into the real route; a separate, unrelated lookup would be misattributed. Hermes `--usage-file` reports estimated cost but no authoritative generation ID. Thus the generation ledger/client are not connected to real Hermes calls, production accounting is not ready, and provider end-to-end verification remains false.

OpenRouter’s official [generation metadata endpoint](https://openrouter.ai/docs/api/api-reference/generations/get-request-&-usage-metadata-for-a-generation) documents `GET /api/v1/generation?id=gen-…`, a `data` envelope, token fields and `total_cost`. The requested overview URL returned 404 in this check. The client/validator prototypes are not evidence of a request from the real Hermes execution path.

### Budget and security

The pure decimal guard rejects missing request bounds and unresolved records, but no transactional concurrent reservation is connected before billed execution. **The USD 2.00 ceiling is not production-enforced.**

Secret scanners (gitleaks, trufflehog and detect-secrets) were unavailable. Local regex scanning found credential-shaped strings in the ignored, untracked `.env.live`, historical reachable Git blobs, current test fixtures and historical diagnostic artifacts. Synthetic test values and potentially real historical values cannot be exhaustively classified by regex; no values or hashes are repeated. The history audit is **partial**, not complete. Provider credential revocation/rotation is **pending owner confirmation**; no remediation was confirmed. No live canary is authorized until this security gate is independently cleared.

### Resource and validation evidence

Point-in-time inspection after non-billed test runs found 0 Factory-labeled Docker containers, 0 `aif-phaseq-*` temp roots, and no Hermes/Factory worker processes by name; Docker Desktop processes remained and were not treated as Factory-owned. This does not establish exhaustive process-descendant cleanup or interruption recovery.

Final validation after the code and live-egress guard edits: `dotnet build apps/AI.Factory.slnx --no-restore` passed (0 warnings, 0 errors); focused `PhaseQ3CostCaptureTests` passed 25/25. Three consecutive full-solution runs each produced three actual TRX files and passed: each was 1,993 total / 1,954 passed / 0 failed / 39 skipped, with all 39 skips explicitly carrying `LIVE OPT-IN NOT ENABLED` reasons and zero ungated skips. The raw logs and TRX files are under [PostSafetyFinalRegression](../TestResults/PhaseQCertificationReadiness/PhaseQ3/PostSafetyFinalRegression/). Earlier intermittent failures remain historical failures and are not counted as passes.

Both Phase Q PowerShell scripts were parsed after the final runner interlock with zero AST syntax errors. The Phase Q runner has an unconditional Q.3 safety interlock and refuses matrix execution. A read-only exact Epoch-2 check verified both manifest and summary have ordered identities Q-A-1 through Q-D-3, all 12 `NOT_RUN` / `NOT_CERTIFIED`, null generation/cost/usage evidence, no historical carry-forward, totals 12 required / 12 not run / 0 certified, and `providerCostCaptureVerified=false`. No Phase Q.3 model generation, live canary, or Epoch-2 sample was executed.

**Current disposition:** implementation remains LIMITED, not implementation-ready. Owner confirmation of credential revocation/rotation remains a prerequisite before any separately authorized Q.4 canary; do not begin Phase R.

Detailed findings and limitations: [Phase Q.3 implementation summary](../TestResults/PhaseQCertificationReadiness/PhaseQ3/implementation-summary.md); machine-readable disposition: [readiness.json](../TestResults/PhaseQCertificationReadiness/PhaseQ3/readiness.json). Final decision remains **LIMITED**; this does not certify Phase Q and does not authorize Phase R or any Epoch-2 work.

---

> Historical Q.2 update (2026-10-08): Epoch-2 remains exactly 0/12, with no historical samples carried forward. OpenRouter's supported accounting exposes response usage/cost and a generation lookup keyed by `X-Generation-Id`; Hermes 0.20.6's `--usage-file` surfaces only Hermes-estimated cost and no provider generation ID. The runner guard and a new three-run regression gate passed, but a focused validation attempt enabled the benchmark opt-in and reached the live Hermes/OpenRouter path, which returned HTTP 402. This was not the approved canary or a matrix sample; without authoritative usage evidence, its cost is UNKNOWN and no more provider calls are authorized. The prior non-generation readiness probe reported a paid key, $10 credits and the exact model reachable, but that does not certify the later attempt. Three current actual-TRX solution regressions passed (1,968 total / 1,928 passed / 0 failed / 40 explicitly gated skips / 0 ungated per run). Security scans surfaced placeholder/test patterns and historic credential-shaped values; validity is unverified and no candidate value is repeated. Full process/cleanup ownership remains unproven. Historical Q-A-2 cost remains UNKNOWN. Final gate: **LIMITED — COST EVIDENCE NOT CERTIFIED**.

> **Phase Q.2 update (2026-10-08):** Epoch-2 remains exactly 0/12, with no historical samples carried forward. OpenRouter's supported accounting exposes response usage/cost and a generation lookup keyed by `X-Generation-Id`; Hermes 0.20.6's `--usage-file` surfaces only Hermes-estimated cost and no provider generation ID. The runner guard and a new three-run regression gate passed, but a focused validation attempt enabled the benchmark opt-in and reached the live Hermes/OpenRouter path, which returned HTTP 402. This was not the approved canary or a matrix sample; without authoritative usage evidence, its cost is UNKNOWN and no more provider calls are authorized. The prior non-generation readiness probe reported a paid key, $10 credits and the exact model reachable, but that does not certify the later attempt. Three current actual-TRX solution regressions passed (1,968 total / 1,928 passed / 0 failed / 40 explicitly gated skips / 0 ungated per run). Security scans surfaced placeholder/test patterns and historic credential-shaped values; validity is unverified and no candidate value is repeated. Full process/cleanup ownership remains unproven. Historical Q-A-2 cost remains UNKNOWN. Final gate: **LIMITED — COST EVIDENCE NOT CERTIFIED**.

Date: 2026-10-08  
Status: **LIMITED — COST EVIDENCE NOT CERTIFIED** (current Q.2 gate; Epoch-2 remains 0/12 and the Q.2 opt-in harness attempt has UNKNOWN cost after HTTP 402)  
Reference: Phase P.3 Report (`docs/phase-p3-report.md`), Phase Q Remediation Specification (`prompts/Phase Q Remediation & Completion — Stabilize, Resume, Complete, and Close the Benchmark.md`), and Phase Q specification (`prompts/Phase Q — End-to-End Benchmark & Autonomous Software Delivery.md`).

> This report records the evidence currently available and does not certify the incomplete matrix. Historical Phase-Q evidence was retained. The pre-existing HTTP 402 smoke under `TestResults/PhaseQHarnessSmoke/` remains separate from the matrix. During Q.2, a focused validation attempt with the benchmark opt-in enabled created a disposable WorkItem and AgentRun, then reached Hermes/OpenRouter and was rejected with HTTP 402. It produced no authoritative generation-linked cost record; its cost is UNKNOWN. It is not a canary or matrix sample, and no retry followed. The historical Q-A-2 unknown-cost attempt remains unresolved. The Phase Q.1 epoch and current Q.2 evidence are documented below.

---

## Historical Phase R entry-gate snapshot (pre-Q.2)

Date: 2026-10-08  
Status: **LIMITED — EXTERNAL RUNTIME CONSTRAINT** (historical snapshot; superseded by the Q.2 cost-evidence gate)  
Reference: Phase P.3 Report (`docs/phase-p3-report.md`), Phase Q Remediation Specification (`prompts/Phase Q Remediation & Completion — Stabilize, Resume, Complete, and Close the Benchmark.md`), and Phase Q specification (`prompts/Phase Q — End-to-End Benchmark & Autonomous Software Delivery.md`).

> This report records the evidence currently available and does not certify the incomplete matrix. The historical Phase-Q evidence was retained. One later harness smoke did start the Factory production path, but OpenRouter rejected it with HTTP 402 (insufficient credits); it is separate from the matrix and its usage/cost remains UNKNOWN. Q.2 also had a distinct opt-in validation attempt; its HTTP-402 outcome and UNKNOWN cost are recorded in the Q.2 addendum. The original Phase-Q unknown-cost attempt still prevents cost certification.

---

## Historical remediation background

The previous Phase R preflight (`docs/phase-r-report.md`) blocked entry because Phase Q had no final gate, its matrix stopped after Q-A × 3, and defects existed in report deserialization and regression stability. This remediation fixed the mapper, repaired the two regression test diagnostics/race, added partial-state manifest parsing, and preserved three consecutive clean default regressions. An attempted isolated harness smoke reached the real Factory path, then OpenRouter returned HTTP 402 insufficient credits. That attempt failed before generation and is recorded as `BLOCKED / BILLING`, with usage/cost UNKNOWN; it is not a matrix sample and no retry was performed. The original Q-A-2 timeout remains a separate unknown-cost historical attempt.

## Historical executive summary (pre-Q.2)

**Historical Q.1 executive summary (superseded by Q.2).** At that time the matrix was incomplete and historical Q-A-2 cost remained unknown. The current disposition and provider evidence are in the Q.2 addendum below; do not use historical readiness figures for current certification.

Historical note: Q.1 non-billed build and regression gate passed. Q.2 repeated the build and three full regressions; the current gate is limited by absent authoritative cost capture, not by regression status.

## 2. Phase P.3 prerequisite confirmation

- Hermes **CERTIFIED** (`nousresearch/hermes-agent:0.20.6`), 2/2 consecutive real model-backed runs.
- Explicit model `deepseek/deepseek-v4-flash-0731` via OpenRouter; no fallback.
- Regression suite green at P.3 close: 1,960 total / 1,921 passed / 0 failed / 39 gated-skipped.
- Phase P.3 gate: **GO FOR PHASE Q**.

## 3. Runtime/model configuration

```text
Runtime: Hermes (nousresearch/hermes-agent:0.20.6)
Gateway: OpenRouter (https://openrouter.ai/api/v1)
Model: deepseek/deepseek-v4-flash-0731 (explicit, paid)
```

No auto-routing, no fallback models/runtimes, no OpenHands/Claude/Codex in the execution path.

## Phase Q objective

Measure whether the Factory can complete autonomous software-delivery tasks reliably through its production workflow, with independently verifiable artifacts, acceptance, security, cleanup, and auditable cost under a fixed $2.00 certification ceiling. Phase Q is not certified solely by a green build.

## Governed rebaseline decision

Historical Q-A-2 attempt 1 cost could not be authoritatively reconciled from retained records. The user explicitly approved a separate evidence epoch; the record and `PHASE_Q_REBASELINE_APPROVED=true` marker are in [phase-q-rebaseline-decision.md](phase-q-rebaseline-decision.md). Historical runs and their unknown costs were not rewritten, converted to zero, or carried forward.

## Certification epoch

`PhaseQ-Certification-Epoch-2`, separate from `HistoricalEpoch`, contains exactly twelve matrix identities. Its machine-readable [manifest](../TestResults/PhaseQ/certification-epoch-2/manifest.json), [summary](../TestResults/PhaseQ/certification-epoch-2/summary.json), and dual epoch governance records (`epoch.json`, `epoch-record.json`) agree. All twelve are `NOT_RUN` / `NOT_CERTIFIED`; no historic evidence contributes to epoch counts or cost.

## Historical Q.1 entry-gate evidence

**Historical Q.1 entry-gate snapshot:** the build and three actual-TRX regressions then passed; at that time the key was free-tier/$0 credits. That readiness result is superseded by the Q.2 non-generation readiness probe below, which later reported a paid key/$10 credits and model reachability. The Q.2 probe did not establish cost capture, and a later Q.2 opt-in harness attempt received HTTP 402 with UNKNOWN usage/cost. Current epoch readiness is separate from certification; neither result permits matrix execution without authoritative accounting.

## Exact runtime/model configuration

```text
Runtime: Hermes
Hermes image: nousresearch/hermes-agent:0.20.6
Gateway: OpenRouter
Model: deepseek/deepseek-v4-flash-0731
Auto/fallback routing: prohibited
```

## Benchmark definitions

Q-A is the single-file Subtract feature; Q-B is the multi-file Multiply feature plus NOTES update; Q-C corrects Divide from addition to division without weakening its test; Q-D implements domain Multiply and application `MultiplyAndAdd` with expected `3*4+5 == 17`. The exact contract remains in the historical section below and task fixtures; no task was run in this epoch.

## Failure classification

Historical Q.1 stop classification: `PROVIDER/BILLING` (the probe then reported a free-tier key and zero credits). Q.2 later observed paid readiness in a non-generation probe, but the separate opt-in harness attempt received HTTP 402 with UNKNOWN usage/cost. Authoritative generation-linked usage capture remains absent and blocks certification. Historical Q-A-2 cost remains UNKNOWN; the Q.2 failure is not an Epoch-2 sample.

## Verification, review, acceptance, and workspace integrity

The prior Q-A samples remain historical, uncertified evidence as summarized below. For each epoch run these states are `NOT_RUN`; there are no new WorkItems, AgentRuns, generations, workspaces, verification, reviews, acceptance results, or cleanup outcomes to claim.

## Docker, process, and temporary-workspace audit

Docker was reachable and `docker ps -a --filter label=factory=ai-factory` reported zero Factory-owned containers; the pinned Hermes image is present. No certification run/workspace was launched. The previous complete child-process/temp-workspace/foreign-resource audit remains incomplete and is not upgraded by the container count.

## Token, latency, cost, and provider billing reconciliation

No certification-epoch token or latency metrics exist. Historical Hermes successful-attempt estimates total $0.05229189 but are not OpenRouter bills. The Q-A-2 timed-out attempt remains UNKNOWN COST; no generation ID or provider request correlation was retained, and account-side usage reconciliation was unavailable. The separate HTTP 402 smoke remains separate and its cost remains unknown. The $2.00 ceiling is therefore uncertifiable. Hermes estimates are never treated as authoritative provider cost.

## Regression evidence

Raw TRX, logs, and per-run parsers are preserved under [Phase Q certification-readiness evidence](../TestResults/PhaseQCertificationReadiness/). All three current regressions passed their TRX gates; the details are reiterated in the Phase Q.1 addendum and the pre-Q.1 snapshot is explicitly labeled historical below.

## Historical Q.1 credential/security snapshot (superseded by Q.2)

The Phase Q.1 working-tree/artifact scan examined 1,339 candidate files and classified four matches as placeholders/test patterns; it did not scan repository history. The broader Q.2 checkout/history scan and its limitations are recorded in the Q.2 addendum below and supersede any implication here that credential audit is complete.

## Remaining limitations

Provider credits must be replenished to a paid tier, and the Factory harness needs verified generation-ID/AgentRun-linked authoritative cost capture before any billed retry. Historical unknown cost remains outside the new epoch; security-history and exhaustive child-process/temp-workspace audits are still incomplete. Do not proceed to Phase R.

## Historical Q.1 phase gate (superseded by Q.2)

`LIMITED — EXTERNAL RUNTIME CONSTRAINT` (historical; superseded by the Q.2 gate below)

## Historical benchmark design (pre-Q.2)

12 independent live runs: 4 task classes × 3 repetitions (Q-A-1..Q-D-3),
each from a fresh disposable workspace/baseline through the full Factory path
(WorkItem → AgentRun → Hermes → verification → review → acceptance → completion
→ workspace integrity → audit → cleanup). Harness: `scripts/run-phase-q.ps1`,
`scripts/parse-phase-q-results.ps1`, runner `PhaseQBenchmarkTests`, fixtures
`PhaseQTaskDefinitions` (+ Phase F `BenchmarkTaskDefinition` for Q-A/B/C).

## 5. Exact task definitions

### Q-A — Small Feature Addition (SingleFile)

Baseline `Calculator` with `Add` only. Add `Subtract(a,b) => a-b` to
`src/Calculator.cs` + `Subtract_ReturnsDifference` test (`10-4==6`).
Acceptance: `Subtract` in src, `Add` preserved, test present, `TestsPass`.

### Q-B — Multi-File Feature (MultiFileFeature)

Add `Multiply(a,b) => a*b` + `Multiply_ReturnsProduct` test (`3*4==12`) +
append `- Multiply` to `NOTES.md` Features. Acceptance: text in all three
files + `TestsPass`.

### Q-C — Bug Fix / Regression (BugFix)

Seeded defect: `Divide(a,b) => a+b`. Fix to `a/b`, preserve
`Divide_ReturnsQuotient` (`10/2==5`) and all existing behavior; no test
weakening. Acceptance: `Divide` in src, test present, `TestsPass`.

### Q-D — Cross-Layer Engineering (domain/application)

Baseline: `src/Domain/Calculator.cs` (`Add` only) +
`src/Application/CalculatorService.cs` (`Add` wrapper + stub
`MultiplyAndAdd => 0`) + `tests/CalculatorServiceTests.cs` (1 passing, 1
failing `MultiplyAndAdd_ReturnsExpected`: `3*4+5==17`). Add `Multiply` to the
domain Calculator and implement `MultiplyAndAdd` as `a*b+c` through the domain
Calculator. Acceptance: `Multiply` in domain, `MultiplyAndAdd` in service,
test present, `TestsPass`.

## Historical baseline evidence

- Repo base commit `9b02037` on `main`; remediation source/report files are modified or untracked in the shared checkout.
- Final `dotnet build apps/AI.Factory.slnx --no-restore --no-incremental`: 0 errors, 3 duplicate-using warnings.
- Current Docker audit: server reachable; 0 Factory-owned containers. The smoke created no Factory-owned container; no foreign resource was altered.
- Historical readiness evidence only. Q.2's non-generation probe reported paid readiness; the subsequent opt-in harness attempt reached the provider path and got HTTP 402. It is recorded in the Q.2 addendum as UNKNOWN-cost, not as an epoch run.
- Spending ceiling: `PHASE_Q_MAX_SPEND_USD = 2.00`.

## Historical per-run results

Run manifest: `TestResults/PhaseQ/manifest.json`; resumable machine-readable summary: `TestResults/PhaseQ/summary.json`; parser: `scripts/parse-phase-q-results.ps1`.

| Run | Outcome | WorkItem / AgentRun | Duration | Tokens | Cost | Verification / review / acceptance | Workspace / cleanup |
|---|---|---|---:|---|---|---|---|
| pilot-Q-A-1 | Completed pilot (not a matrix sample) | W=`deb1fed8-ddbf-4109-ad90-19ac8f98a1b0` / AgentRun=`c6659eb9-0625-4e49-9702-ec90032978bc` | 699 s wall; 549 s runtime | 60,260 prompt / 2,563 completion / 262,503 total (pilot reference in original report) | $0.00795956 Hermes estimate | verification pass; review non-blocking; WorkItem Completed | integrity fields clean; Factory containers 0 after 15 s |
| Q-A-1 | Completed historically; not recertified | W=`a018e5da-52f8-47b2-b7f4-0b222312d7de` / AgentRun=`d8d4fd84-41b1-4642-a865-9f9cc396d562` | 822 s wall; 721 s runtime | 39,729 prompt / 14,535 completion / 1,053,944 total | $0.03731416 Hermes estimate | verification pass; review non-blocking; Completed | historical evidence reports no missing/forbidden state, credential scan 0, cleanup 0 |
| Q-A-2 | Completed after historical bounded retry; not recertified | W=`8e411163-fb07-433a-a61b-61e51ffa8902`; AgentRun attempt 1=`08aaf881-0576-4616-a608-943aed3be171`; attempt 2=`7aa78314-a5eb-4176-ad37-37afb161dd69` | 1725 s wall; attempt 1 1188 s / attempt 2 474 s | attempt 1 not reported; attempt 2 37,034 prompt / 2,156 completion / 253,974 total | attempt 1 UNKNOWN; attempt 2 $0.007292404 Hermes estimate | final historical attempt verification pass, review non-blocking, Completed | historical integrity clean; Factory containers 0 after 15 s |
| Q-A-3 | Completed historically; console-only evidence; not recertified | W=`2de0dbc1-f4fe-4b02-8124-b5f079b1bb6b` / AgentRun=`e1da82d2-a31d-430d-aa5f-4b2525b05093` | 605 s wall; 517 s runtime | 98,927 prompt / 2,651 completion / 241,098 total | $0.007685326 Hermes estimate | verification pass; review non-blocking; Completed | console reports missing/forbidden state empty and cleanup 0; `Q-A-3.txt` missing |
| Remediation smoke Q-A-1 (separate; not a matrix run) | BLOCKED — BILLING | WorkItem=`8fe1fb45-84ac-4f8c-bcc8-b0205d448a31` / AgentRun=`b0fb268f-76f4-4385-bc84-7390a2903cfb` | 14.648 s AgentRun / 22 s wall | prompt/completion/total NOT REPORTED | UNKNOWN; HTTP 402 insufficient OpenRouter credits | WorkItem Failed; no generation/verification/review/acceptance PASS | zero Factory-owned containers after smoke; evidence under `TestResults/PhaseQHarnessSmoke/` |
| Q-B-1 | NOT RUN | — | — | — | — | no evidence | no workspace created |
| Q-B-2 | NOT RUN | — | — | — | — | no evidence | no workspace created |
| Q-B-3 | NOT RUN | — | — | — | — | no evidence | no workspace created |
| Q-C-1 | NOT RUN | — | — | — | — | no evidence | no workspace created |
| Q-C-2 | NOT RUN | — | — | — | — | no evidence | no workspace created |
| Q-C-3 | NOT RUN | — | — | — | — | no evidence | no workspace created |
| Q-D-1 | NOT RUN | — | — | — | — | no evidence | no workspace created |
| Q-D-2 | NOT RUN | — | — | — | — | no evidence | no workspace created |
| Q-D-3 | NOT RUN | — | — | — | — | no evidence | no workspace created |

Historical outcomes above are not upgraded to certified PASS. The separate 402 smoke is not part of the formal 12-run matrix. Q-A-3's original separate text evidence remains absent and was not reconstructed.

## 8. Failure attribution

| Finding | Classification | Evidence / disposition |
|---|---|---|
| B1: execution tests emitted as objects but mapper accepted only strings | FACTORY | Fixed in `WorkItemExecutionResultMapper`; normalized provider-neutral test records and raw JSON preserved; focused tests pass. Historical live warnings remain in the retained logs. |
| B2a: Windows fake CLI PID probe write/read sharing race | TEST | Root cause was concurrent exclusive `WriteAllText` vs polling `ReadAllLines`; PID publication now uses a temp file plus same-directory rename. Persistent read errors still fail with diagnostics. Focused cancellation theory passes; all three full regressions also pass. |
| B2b: API test helper lost response body | TEST / root trigger UNKNOWN (not reproduced) | Helper now records status, response body, project name, and isolated workspace root before parsing. Failed case passes focused and in all three full-solution runs; no evidence identifies whether original response was an API error, infra fault, or other. Do not relabel as a proven product fix. |
| B3: no partial/resume state | HARNESS | Runner now writes manifest + summary before readiness checks; partial parser emits all 12 identities; interrupted RUNNING state is marked INTERRUPTED and stops resume. Existing historical evidence stays in place. |
| B4: unknown usage counted as zero | HARNESS / EVIDENCE | Cost model distinguishes reported, estimated, and unknown attempts. Historical Q-A-2 attempt 1 is UNKNOWN; the runner guard blocks when capture proof is absent. Q.2's separate opt-in harness attempt received HTTP 402 and has UNKNOWN usage/cost; it was not put into the certification ledger and was not retried. |
| Runtime checkout path relocation | ENVIRONMENT | Runner resolves root from `$PSScriptRoot`; manifest/evidence paths derive from repository-relative configured ResultsDir. No D: or C: checkout literal is required in code. |
| OpenRouter credit availability | ENVIRONMENT / EVIDENCE | The historical smoke and the separate Q.2 opt-in harness attempt returned HTTP 402. Q.2 attempt cost is UNKNOWN because no authoritative usage record was captured; it is not a matrix sample and was not retried. |
| Missing Q-A-3 text evidence | EVIDENCE | Console evidence retained; no `.txt` reconstructed or backfilled. Historical entry remains uncertified. |

## 9. Token/latency/cost metrics

For historical Q-A-1..3, successful-attempt Hermes estimates total **$0.05229189** (not actual provider billing). The pilot estimate is an additional **$0.00795956**, outside the matrix. Q-A-2 attempt 1 timed out without trustworthy usage/cost. A separate pre-existing remediation smoke was denied with HTTP 402 and has no provider usage/cost response. The additional Q.2 opt-in harness attempt also lacks authoritative usage evidence and remains UNKNOWN; neither is part of the matrix. Thus the actual attempted spend is UNKNOWN; the $2.00 ceiling is **not certifiable**. Provider-side billing reconciliation was not available/performed.

## 10. Verification results

The retained Q-A evidence records `validationRun success=True build=True tests=True` for Q-A-1, Q-A-2's final successful attempt, and Q-A-3. The independent Factory gate, not agent self-report alone, produced these results. The pilot also records a successful independent focused test and full demo suite. These results cover Q-A only; Q-B/Q-C/Q-D remain unexecuted.

## 11. Review results

Each available Q-A run records `reviewRun blocksCompletion=False`. Findings were informational/non-blocking. Q-A-3's review record exists only in its console log. No other task class has review evidence.

## 12. Acceptance results

Q-A-1/2/3 each record `workItem.final=Completed`, and the explicit artifact checks for Subtract and its test passed. Q-A-2 used one bounded timed-out attempt followed by successful attempt 2. These are partial historical outcomes, not certification that all planned tasks pass.

## 13. Workspace integrity results

Retained Q-A evidence states `workspace.missingRequired=[]`, `workspace.forbiddenRuntimeState=[]`, and zero workspace files containing credentials. Build output was classified as incidental by the existing policy. Q-A-3's workspace details are available only in the console log. No audit has been performed for the unrun Q-B/Q-C/Q-D workspaces.

## Historical Q.1 credential security audit (superseded by Q.2)

Available historical Phase Q evidence reports zero credential-containing workspace files and zero credential-bearing log lines; the harness prints only the variable name and never the value. Phase Q.1 additionally scanned the working tree plus retained Phase-Q, smoke, remediation, and regression artifacts (1,339 candidate files) for common credential patterns and reviewed four matches as placeholders/test data; it did not scan Git history. The broader Q.2 audit and historical credential-shaped matches are detailed in the Q.2 addendum and supersede this earlier partial snapshot.

## 15. Docker/container audit

Historical Q-A evidence records `factoryOwned=0` immediately and after 15 seconds; one foreign `hermes` container was left untouched. Final audit reached Docker Engine 29.7.2 and the `factory=ai-factory` label query returned zero containers after the smoke. This verifies Factory-labeled containers only; child-process and temporary-workspace state was not exhaustively inventoried. No foreign resource was modified.

## Historical remediation validation (pre-Q.2)

- A final rebuild after remediation: `dotnet build apps/AI.Factory.slnx --no-restore --no-incremental` succeeded, 0 errors and 3 CS0105 duplicate-using warnings.
- Focused application regression: `WorkItemExecutionResultMapperTests` plus `CliProcessTreeLifecycleTests`, 47/47 passed.
- Focused API regression: `ProjectsAndTasksApiTests`, 23/23 passed.
- PowerShell parser syntax checks for both Phase-Q scripts passed; parser smoke outputs preserve BLOCKED/BILLING/UNKNOWN for the 402 attempt and the original Phase-Q summary preserves three historical records plus one unknown-cost attempt.
- Historical Q.1 runner invocation only; Q.2 did not run the runner or parser against the current certification ledger. Both scripts were AST-parsed, and the existing ledger was inspected read-only.

Historical snapshot: three consecutive solution-wide `dotnet test apps/AI.Factory.slnx --no-build` runs passed before the separate 402 smoke (pre-Q.1 evidence). The Phase Q.1 final three were independently rerun and parsed from actual TRX under `TestResults/PhaseQCertificationReadiness/`: each totals 1,968 / 1,928 passed / 0 failed / 40 skipped / 0 skips without gate reasons. Q.2 then reran the solution build and three full solution regression gates with the same parsed totals; raw actual TRX, per-run logs, [regression summary](../TestResults/PhaseQCertificationReadiness/PhaseQ2/regression-summary.txt), and the separate [UNKNOWN-cost attempt record](../TestResults/PhaseQCertificationReadiness/PhaseQ2/unplanned-live-attempt.json) are under [Phase Q.2 readiness evidence](../TestResults/PhaseQCertificationReadiness/PhaseQ2/). Do not conflate the regression and provider-attempt evidence.

## 17. Historical comparison with Phase F

Historical comparison not directly comparable. (Phase F measured Hermes vs
OpenHands empirical comparison on different tasks/contracts; Phase Q measures
Hermes-only autonomous delivery on Q-A..Q-D under the full acceptance contract.)

## 18. Limitations

- Historical snapshot: only Q-A × 3 completed; no Q-B, Q-C, Q-D benchmark executions exist. Epoch-2 itself remains 0/12.
- Q-A-3 has no separate `.txt` artifact; its console log is preserved. Nothing was reconstructed.
- At least one historical timed-out Q-A attempt has unknown cost; the pre-existing isolated smoke and the distinct Q.2 opt-in harness attempt were both HTTP-402/billing failures without authoritative usage. Provider billing reconciliation is unavailable, so the $2.00 ceiling is not certifiable.
- Docker was available for final ownership-label check (zero Factory-owned containers). A complete audit of child processes, temp workspaces, and foreign resources was not performed.
- This controlled workload does not support general AI-agent performance claims.
- Token `total` may differ from prompt + completion as reported by Hermes.
- B2b's original response is not recoverable: its old test helper had discarded status/body. The test passes with improved diagnostics, but its original underlying trigger remains unknown.

## Historical Phase R recommendation (pre-Q.2)

Historical recommendation: do not enter Phase R before Phase Q is certified. The evidence-only rebaseline established `PhaseQ-Certification-Epoch-2` but did not certify its matrix. Current Q.2 stop findings and the exact gate are documented in the addendum below; historical evidence remains unchanged and is not carried forward.

## 20. Historical final phase gate (pre-Q.1)

```text
LIMITED — EXTERNAL RUNTIME CONSTRAINT
```

The Q.1 decision and Epoch-2 ledger remain in force, but the Q.2 non-generation probe supersedes the earlier readiness values: the configured key was paid and $10 credits were reported at probe time. The matrix remains 0/12 because authoritative generation-linked capture is not implemented through the current Hermes report/harness. A separate Q.2 harness validation attempted the provider path and received HTTP 402; cost evidence is UNKNOWN, and the attempt is excluded from the epoch. Historical Q-A-2 unknown cost remains UNKNOWN and excluded, not zero. The current disposition is `LIMITED — COST EVIDENCE NOT CERTIFIED`; do not proceed to Phase R.

## Historical remediation certification snapshot (pre-Q.1)

This table is retained to preserve the prior remediation snapshot; it is not the current certification matrix. The current epoch-specific 12-run table above is authoritative.

| Area | Result | Evidence |
|---|---|---|
| Factory Build | PASS | `dotnet build apps/AI.Factory.slnx --no-incremental`, 0 errors, 3 pre-existing warnings |
| Regression Run 1 | PASS | `TestResults/PhaseQRemediation/Run1/*.trx`; parser: 1968 / 1923 / 0 / 45; no ungated skips |
| Regression Run 2 | PASS | `TestResults/PhaseQRemediation/Run2/*.trx`; parser: 1968 / 1923 / 0 / 45; no ungated skips |
| Regression Run 3 | PASS | `TestResults/PhaseQRemediation/Run3/*.trx`; parser: 1968 / 1923 / 0 / 45; no ungated skips |
| B1 Execution Metadata | PASS (focused) | mapper tests: 20/20; live rerun deferred due unknown historical cost |
| B2a CLI Lifecycle | PASS (focused + regressions) | focused cancellation test 2/2; three complete regressions green |
| B2b Projects API Test | PASS (not reproduced) | diagnostic status/body helper; focused ProjectsAndTasks API suite 23/23; three full regressions green; original transient cause undetermined |
| B3 Resume/Relocation Safety | PASS (partial behavior verified) | relative paths and 12-entry partial summary; historical and smoke manifests preserved; unknown-cost runner guard prevents another call |
| B4 Cost Accounting | PASS (fail-closed behavior; ceiling uncertifiable) | Historical timeout and HTTP 402 smoke remain UNKNOWN; parser reports LIMITED, not zero; runner refuses further billed calls |
| Historical Q-A | INCOMPLETE / NOT CERTIFIED | Three retained historical runs; Q-A-3 text artifact missing and Q-A-2 has an unknown-cost attempt; separate remediation smoke returned HTTP 402 and is not a matrix sample |
| Historical Q-B | NOT RUN | no historical artifacts |
| Historical Q-C | NOT RUN | no historical artifacts |
| Historical Q-D | NOT RUN | no historical artifacts |
| Verification | PARTIAL | Q-A evidence has verification success for available runs |
| Review | PARTIAL | Q-A evidence has non-blocking review for available runs |
| Acceptance | PARTIAL | Q-A work items reached Completed; not a 12-run certification |
| Workspace Integrity | PARTIAL | available Q-A evidence lists no missing/forbidden state; no full matrix audit |
| Credential Security | PARTIAL | Historical workspace checks only; Q.2 redacted scans found credential-shaped historic values requiring review/rotation; no candidate values included in report |
| Cleanup | PARTIAL (container ownership-label check passed) | Historical Q-A and Q.2 queries show zero Factory containers and no aif-phaseq temp roots at inspection; exhaustive process-tree/foreign-resource cleanup proof remains incomplete |
| Cost Ceiling | LIMITED / UNKNOWN | Successful historical Hermes estimates $0.05229189; historical Q-A-2, pre-existing HTTP 402 smoke, and Q.2 opt-in harness attempt do not provide authoritative provider usage/cost; actual billed total not certifiable |
| Phase Q Gate | LIMITED — COST EVIDENCE NOT CERTIFIED | This legacy snapshot is superseded by the Q.2 addendum: authoritative generation-linked cost is absent, Epoch-2 remains 0/12, and the Q.2 HTTP-402 harness attempt is UNKNOWN and outside the matrix |

---

## Phase Q.2 provider accounting and stop decision (2026-10-08)

### Authoritative provider contract and current implementation gap

OpenRouter's published Usage Accounting guide states that completed API responses include native token usage and the total amount charged, and its Generation lookup returns usage/cost by the response generation ID. Streaming responses carry usage in the terminal usage chunk; `X-Generation-Id` is returned for endpoints. The official docs also state `upstream_inference_cost` may be 0/null except for BYOK, so the auditable account cost field is `usage.cost` / generation metadata `total_cost`, not upstream cost or a Hermes estimate. These facts are documented at [OpenRouter Usage Accounting](https://openrouter.ai/docs/cookbook/administration/usage-accounting), [Generation lookup](https://openrouter.ai/docs/api/api-reference/generations/get-request-%26-usage-metadata-for-a-generation), and [streaming headers/usage](https://openrouter.ai/docs/api_reference/streaming).

Current Factory code delegates model HTTP transport to Hermes. [HermesAgentExecutor](../apps/src/AI.Factory.Infrastructure/Agents/HermesAgentExecutor.cs) reads a Hermes `--usage-file`; [execution-runtimes.md](execution-runtimes.md) documents its cost as Hermes' own estimate. That report has no OpenRouter `X-Generation-Id` or OpenRouter-authoritative response usage. The runner's manifest assertion `providerCostCaptureVerified` therefore remains false. No production capture or requested non-billed cost-accounting test suite was implemented in Q.2: Hermes does not expose the provider evidence needed to test/certify a successful generation path, and no generation may be made merely to fabricate a test fixture. We did **not** relabel Hermes telemetry as billed cost and did not use the OpenRouter generation lookup without a generation ID.

Implementing a safe capture adapter needs a verified way to intercept each OpenRouter response (including streamed/tool-loop responses) and durably persist each generation ID plus response/generation cost tied to the current AgentRun, before a run can end UNKNOWN. A post-run query alone cannot reconcile process termination before the ID is saved. The provider/library stack does not currently expose this through the Hermes report, so the evidence requirements are not met and no billed canary is permitted.

### Non-billed readiness, audits, and stop

- OpenRouter account probe used only GET `/key`, `/credits`, and the explicit model `/endpoints`; it made no generation request. Results: key HTTP 200, `is_free_tier=false`, credits HTTP 200 with $10, model HTTP 200/reachable. The secret was not printed.
- **Credential/security audit:** redacted pattern scans covered 5,166 checkout files (69 over the 25 MB bound skipped), 1,266 currently tracked files, the ignored `.env.live`, and targeted reachable-history diffs for OpenRouter-shaped values in `prompts/Before prompt.txt` and `apps/src/AI.Factory.Api/appsettings.json`. `.env.live` exists, is ignored/untracked, and has a nonempty configured key; its value was never printed. The checkout scans surfaced numerous test/placeholder and broad assignment-pattern false positives. The targeted history scan found credential-shaped strings in the prompt (including a separate `sk-...`-shaped value not covered by the OpenRouter detector) and an old provider-setting value. Whether those are valid secrets is unverified; treat them as potentially exposed and rotate/revoke relevant provider keys. Current prompt file is absent; history entries remain unchanged. This is not a claim that every secret format across all history was exhaustively classified; no candidate value is repeated here.
- **Resource/cleanup audit:** Docker query succeeded and showed zero Factory-labeled containers; the temp root had zero `aif-phaseq-*` directories after checks. A Windows process snapshot captured three matching audit-shell candidates and their ancestry; it did not prove or disprove all Factory/Hermes descendants. No process was killed and no foreign container was touched. The focused harness attempt created an isolated temp workspace and Docker/Hermes execution, then the test cleanup removed the workspace and executor cleanup ran; however, exhaustive process-tree/container/foreign-resource invariants for that failed attempt were not independently certified. Its console log was retained and checked with credential-shaped key/bearer detectors; no such match was found. Do not classify this as a successful cleanup audit.
- **Build/regression gate:** current `dotnet build apps/AI.Factory.slnx --no-restore --no-incremental` succeeded (0 errors, 3 duplicate-using warnings). Three current full-solution `dotnet test ... --no-build --results-directory ... --logger trx` runs each produced three actual TRX files and parsed as 1,968 total / 1,928 passed / 0 failed / 40 skips with explicit `LIVE OPT-IN NOT ENABLED` reasons / 0 ungated skips. Raw console and TRX are preserved under [Phase Q.2 readiness evidence](../TestResults/PhaseQCertificationReadiness/PhaseQ2/).
- **Accounting behavior:** Factory metadata identifies each `AgentRun` by its GUID and persists Hermes-reported tokens plus a `hermes:estimated_cost_usd(...)` source inside the structured result. `AgentRun` has no provider-generation relation/table, and the Hermes result has no OpenRouter generation ID or provider-authoritative usage. The Phase Q runner/parser know how to validate `providerUsage` evidence if supplied, but no production capture implementation or non-billed accounting unit-test suite exists. The existing fail-closed guard remains `providerCostCaptureVerified=false`; no implementation can safely manufacture that evidence. The last observed Epoch-2 manifest/summary had the 12 exact identities, all `NOT_RUN` / `NOT_CERTIFIED`, null costs, and readiness `true`; the separate Q.2 HTTP-402 attempt is outside that ledger and has UNKNOWN cost.
- **Script/harness safety note:** the focused harness attempt was intended only as validation but, because its live opt-in was enabled, it followed the provider execution path and received HTTP 402. Its redacted classification is in [the separate attempt record](../TestResults/PhaseQCertificationReadiness/PhaseQ2/unplanned-live-attempt.json); its console log is retained at [the Q.2 HTTP-402 attempt log](../TestResults/PhaseQCertificationReadiness/PhaseQ2/phaseq-live-attempt-http402.log). The isolated temp workspace was confirmed removed. This invocation did not write to or mutate `TestResults/PhaseQHarnessSmoke/`; that separate pre-existing smoke remains BLOCKED/UNKNOWN. Neither is an approved canary or matrix sample. The runner/parser were not executed at the end because the parser writes certification state and reprocessing existing UNKNOWN evidence would risk mutating the ledger.

**Disposition:** The Q.2 HTTP-402 attempt is recorded separately at [unplanned-live-attempt.json](../TestResults/PhaseQCertificationReadiness/PhaseQ2/unplanned-live-attempt.json) and is not a canary or matrix run. Stop before any further provider call, cost canary, or matrix run. The provider readiness probe and three consecutive non-billed regressions passed, but generation-linked authoritative cost capture/tests are absent; historical credential-shaped values need provider review/rotation, and exhaustive process/resource cleanup is unproven. The Q.2 opt-in harness attempt reached the provider path, returned HTTP 402, and has UNKNOWN cost; no further live calls are authorized. Epoch-2 remains 0/12; the last observed manifest/summary were preserved without parser writes. Gate: **LIMITED — COST EVIDENCE NOT CERTIFIED**. No Phase R work is authorized.

---

## Phase Q.1 certification epoch and governance addendum

### Phase Q.1 runner/parser hardening verification

- PowerShell AST parsing succeeded for both Phase Q scripts after final edits.
- Historical Q.1 parser check: at that time JSON validation confirmed exact ordered identities Q-A-1 through Q-D-3, 0 certified PASS, 12 NOT_RUN, null reported costs, and `BLOCKED_PROVIDER_BILLING`; Q.2 later updated readiness. Q.2 did not rerun the parser.
- Historical Q.1 runner invocation only; it stopped before further live work at that time. Q.2 later had a separate opt-in harness attempt that reached the provider path and returned HTTP 402; it is documented in the Q.2 attempt record and was not a canary or matrix sample.
- Historical Q.1 resume/parser logic preserves durable identity/attempt state and rejects invalid provider-evidence correlation; Q.2 did not rerun those scripts against the certification ledger.
- Epoch-2 remains exactly 0/12, with all twelve entries `NOT_RUN` / `NOT_CERTIFIED`; see the Q.2 addendum and machine-readable ledger.


**Historical Q.1 response snapshot (not the current Q.2 result):**

```text
Phase Q.1 Result

Governance decision: Rebaseline explicitly approved; decision and marker recorded.
Historical cost reconciliation: Not possible from retained evidence; Q-A-2 attempt 1 remains UNKNOWN COST.
Rebaseline required: Yes.
Rebaseline approved: Yes, for evidence-ledger separation only.
Certification epoch: PhaseQ-Certification-Epoch-2; remains 0/12, not started.
Matrix completion: 0/12; every identity is NOT_RUN / NOT_CERTIFIED.
Regression gate: PASS; three consecutive full-solution runs, each 1968 total / 1928 passed / 0 failed / 40 gated skips / 0 ungated.
Provider readiness: PASS for the latest non-generation probe (paid key, $10 credits, model endpoint reachable); earlier free-tier/$0 reading is historical.
Cost ceiling: Not certifiable; provider-reported generation-cost capture is not proven.
Security audit: Partial only; historical workspace/log checks exist, no repository-wide secret/history scan.
Workspace audit: Partial historical Q-A evidence only; no new epoch workspaces created.
Cleanup audit: Docker available, 0 Factory containers; exhaustive child-process/temp-workspace audit not done.
Factory defects remaining: None demonstrated by current checks; harness does not yet capture authoritative OpenRouter usage per generation.
Final Phase Q Gate: LIMITED — COST EVIDENCE NOT CERTIFIED. Q.2 non-generation readiness probe: PASS at probe time; opt-in harness validation was HTTP 402 / cost UNKNOWN and is not an authorized canary or matrix identity.
```

The report’s current certification matrix is the exact epoch matrix below; the legacy tables farther down are explicitly historical snapshots. The Phase Q.1 parser/runner verification above reflects its historical validation date. Q.2 AST-parsed both scripts and read the existing manifest/summary without modification; it did not run the parser or invoke the certification runner against the current ledger. The separate Q.2 HTTP-402 attempt is outside Epoch-2; current stop findings are recorded in the Q.2 addendum.

---

An explicit approval decision established the new certification epoch after the retained historical Q-A-2 attempt could not be reconciled from preserved OpenRouter evidence. The auditable decision and literal approval marker are recorded in [phase-q-rebaseline-decision.md](phase-q-rebaseline-decision.md). Historical artifacts in `TestResults/PhaseQ/` remain untouched; their UNKNOWN cost is outside the new certification ledger, never zero. No historical Q-A sample is carried forward.

Epoch `PhaseQ-Certification-Epoch-2` is represented separately in the machine-readable [epoch manifest](../TestResults/PhaseQ/certification-epoch-2/manifest.json) and [epoch summary](../TestResults/PhaseQ/certification-epoch-2/summary.json). They contain exactly Q-A-1..Q-D-3; all twelve have `NOT_RUN`, `NOT_CERTIFIED`, null actual cost, and their verification/review/acceptance/integrity/security/cleanup fields are `NOT_RUN`. Summary totals agree (12 required, 0 pass, 12 not run, 0 certified).

### Non-billed entry gate and provider readiness

- Build: `dotnet build apps/AI.Factory.slnx --no-restore --no-incremental` succeeded, 0 errors and 3 duplicate-using warnings.
- Regressions: `dotnet test apps/AI.Factory.slnx --no-build` passed three consecutive times. Each result was parsed from all 3 actual TRX files: **1,968 total; 1,928 passed; 0 failed; 40 skipped; 0 skips without explicit gate reason**. Raw TRX, per-run console output, and JSON summaries are preserved under [Phase Q readiness evidence](../TestResults/PhaseQCertificationReadiness/).
- Provider probes were non-generation HTTP GET requests. The key endpoint returned HTTP 200 and identified `is_free_tier=true`; credits endpoint returned HTTP 200 with `total_credits=0`; exact model endpoint returned HTTP 200 with endpoints present. Thus explicit model reachability passed, but paid-tier/sufficient-credit readiness failed. The API key value was not printed.
- Docker was reachable (29.7.2); the exact Hermes image `nousresearch/hermes-agent:0.20.6` was present; the Factory-owned container query returned zero. These do not override failed billing readiness.
- The Hermes report currently exposes Hermes-estimated cost, not authoritative OpenRouter billed cost; new-epoch generation-ID/provider-usage correlation and per-run authoritative billing capture remain unproven. Do not launch a model run until the provider account has paid credits and authoritative usage can be captured and associated with each AgentRun.
- Historical Q.1 state: no Phase R work or provider generation had been started at that checkpoint. Q.2 later attempted an opt-in harness validation; it reached the provider path and returned HTTP 402 with UNKNOWN cost, outside the matrix.

### Phase Q.1 final disposition

Historical Q.1 close state: Q-A-2 could not be reconciled and remains UNKNOWN; the separate epoch was created but not started (0/12). At that historical checkpoint the regression gate passed, the provider probe reported a free-tier key with zero credits, and repository-history scanning had not been done. Q.2 subsequently reported paid readiness in a non-generation probe, then an opt-in harness attempt returned HTTP 402 with UNKNOWN cost. The current status and security audit are recorded in the Q.2 addendum. Q.1 final gate at that checkpoint: **LIMITED — EXTERNAL RUNTIME CONSTRAINT**. Do not enter Phase R.

The sections below preserve historical report text and are not current certification evidence. Interpret legacy final-gate, readiness, and matrix statements as historical only; the Q.2 addendum and Epoch-2 manifest/summary govern current status.

### Historical Phase Q.1 certification matrix

The table below describes the **new certification epoch only**. Historical evidence, the pre-existing HTTP 402 smoke, and the separate Q.2 validation attempt remain outside these rows. All 12 identities remain NOT_RUN; do not infer successful generation, billing, or certification from the separate attempt.

| Run | Task | Outcome | Runtime | Model | Generation | Verification | Review | Acceptance | Workspace | Cost | Cleanup | Certification |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| Q-A-1 | Small Feature Addition | NOT_RUN | Hermes 0.20.6 | deepseek/deepseek-v4-flash-0731 | NOT_RUN | NOT_RUN | NOT_RUN | NOT_RUN | NOT_RUN | NOT_APPLICABLE | NOT_RUN | NOT_CERTIFIED |
| Q-A-2 | Small Feature Addition | NOT_RUN | Hermes 0.20.6 | deepseek/deepseek-v4-flash-0731 | NOT_RUN | NOT_RUN | NOT_RUN | NOT_RUN | NOT_RUN | NOT_APPLICABLE | NOT_RUN | NOT_CERTIFIED |
| Q-A-3 | Small Feature Addition | NOT_RUN | Hermes 0.20.6 | deepseek/deepseek-v4-flash-0731 | NOT_RUN | NOT_RUN | NOT_RUN | NOT_RUN | NOT_RUN | NOT_APPLICABLE | NOT_RUN | NOT_CERTIFIED |
| Q-B-1 | Multi-File Feature | NOT_RUN | Hermes 0.20.6 | deepseek/deepseek-v4-flash-0731 | NOT_RUN | NOT_RUN | NOT_RUN | NOT_RUN | NOT_RUN | NOT_APPLICABLE | NOT_RUN | NOT_CERTIFIED |
| Q-B-2 | Multi-File Feature | NOT_RUN | Hermes 0.20.6 | deepseek/deepseek-v4-flash-0731 | NOT_RUN | NOT_RUN | NOT_RUN | NOT_RUN | NOT_RUN | NOT_APPLICABLE | NOT_RUN | NOT_CERTIFIED |
| Q-B-3 | Multi-File Feature | NOT_RUN | Hermes 0.20.6 | deepseek/deepseek-v4-flash-0731 | NOT_RUN | NOT_RUN | NOT_RUN | NOT_RUN | NOT_RUN | NOT_APPLICABLE | NOT_RUN | NOT_CERTIFIED |
| Q-C-1 | Bug Fix / Regression | NOT_RUN | Hermes 0.20.6 | deepseek/deepseek-v4-flash-0731 | NOT_RUN | NOT_RUN | NOT_RUN | NOT_RUN | NOT_RUN | NOT_APPLICABLE | NOT_RUN | NOT_CERTIFIED |
| Q-C-2 | Bug Fix / Regression | NOT_RUN | Hermes 0.20.6 | deepseek/deepseek-v4-flash-0731 | NOT_RUN | NOT_RUN | NOT_RUN | NOT_RUN | NOT_RUN | NOT_APPLICABLE | NOT_RUN | NOT_CERTIFIED |
| Q-C-3 | Bug Fix / Regression | NOT_RUN | Hermes 0.20.6 | deepseek/deepseek-v4-flash-0731 | NOT_RUN | NOT_RUN | NOT_RUN | NOT_RUN | NOT_RUN | NOT_APPLICABLE | NOT_RUN | NOT_CERTIFIED |
| Q-D-1 | Cross-Layer Engineering | NOT_RUN | Hermes 0.20.6 | deepseek/deepseek-v4-flash-0731 | NOT_RUN | NOT_RUN | NOT_RUN | NOT_RUN | NOT_RUN | NOT_APPLICABLE | NOT_RUN | NOT_CERTIFIED |
| Q-D-2 | Cross-Layer Engineering | NOT_RUN | Hermes 0.20.6 | deepseek/deepseek-v4-flash-0731 | NOT_RUN | NOT_RUN | NOT_RUN | NOT_RUN | NOT_RUN | NOT_APPLICABLE | NOT_RUN | NOT_CERTIFIED |
| Q-D-3 | Cross-Layer Engineering | NOT_RUN | Hermes 0.20.6 | deepseek/deepseek-v4-flash-0731 | NOT_RUN | NOT_RUN | NOT_RUN | NOT_RUN | NOT_RUN | NOT_APPLICABLE | NOT_RUN | NOT_CERTIFIED |
