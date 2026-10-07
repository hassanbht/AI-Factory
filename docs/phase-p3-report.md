# Phase P.3 Result — Live Model Certification Closure & Phase Q Unlock

Date: 2026-10-07  
Status: **GO FOR PHASE Q**  
Reference: Phase P.1 Report (`docs/phase-p1-report.md`), Phase P.2 Report (`docs/phase-p2-report.md`) & Phase P.3 Specification (`prompts/Phase P.3 — Live Model Certification Closure & Phase Q Unlock.md`)

---

## 1. Final Gate

**`GO FOR PHASE Q`**

### Rationale
- **Hermes Runtime Certified:** Hermes (`nousresearch/hermes-agent:0.20.6`) achieved **two consecutive, independent, real model-backed certification runs** using the explicit model `deepseek/deepseek-v4-flash-0731` routed through OpenRouter without fallback or implicit routing.
- **Complete End-to-End Factory Lifecycle:** Both runs independently traversed the full Factory lifecycle (`Project -> WorkItem -> AgentRun -> Execution -> Verification -> Review -> Acceptance -> Completion`).
- **Real Model Generation:** Both runs generated real code changes in `src/Calculator.cs` (adding `Multiply(int a, int b) => a * b`) and `tests/CalculatorTests.cs` (adding `Multiply_ReturnsProduct` asserting `Calculator.Multiply(6, 7) == 42`).
- **Independent Verification & Acceptance:** Independent build and unit tests passed on both workspaces (2/2 tests passed, 0 failed). Acceptance criteria evaluated and satisfied.
- **Provider-Neutral Review Boundary:** Independent reviews executed cleanly using `<!-- REVIEW_START -->...<!-- REVIEW_END -->` framing, producing 0 blocking findings (`blocksCompletion = false`).
- **Workspace Integrity & Cleanup:** Final workspace strictly adhered to `Baseline + Permitted Additions` with 0 unexpected files, 0 forbidden runtime state (`conversations`, `bash_events`, `.hermes`, `.openhands`), and 0 credential leaks. All Factory-owned Docker containers converged to 0 immediately and after 15 seconds. Foreign container `hermes` remained untouched.
- **OpenHands Audit:** Attempted OpenHands with `qwen/qwen3-coder`. Real code was generated, but the runtime container image (`ghcr.io/openhands/agent-server:1.49.4-python`) lacks the .NET runtime/SDK, causing the agent to self-report `repair_required` upon attempting test verification, resulting in `WorkItemStatus.Blocked`. OpenHands is classified as **NOT CERTIFIED (External Runtime Environment Constrained)**.
- **Phase Q Unlock Criteria Satisfied:** Per Phase P.3 Specification (§0, §8, §18), Phase Q is unlocked when at least one runtime completes two consecutive successful real model-backed certification runs. Hermes has fully met this contract.

---

## 2. Baseline

| Item | Value / Observation | Evidence Class |
|---|---|---|
| Repository Root | `D:\Project\AI-Factory` | `OBSERVED` |
| Solution Build | `dotnet build apps/AI.Factory.slnx --no-incremental` — 0 errors, 3 warnings (pre-existing redundant using directives in test files) | `TEST-VERIFIED` |
| Docker Host | Docker Desktop on Windows, operational | `OBSERVED` |
| Foreign Containers | `68b063bae8d3` (`hermes` on port 8642) Up, untouched | `OBSERVED` |
| Factory Containers Before | **0** | `OBSERVED` |
| Hermes Image | `nousresearch/hermes-agent:0.20.6` | `OBSERVED` |
| OpenHands Image | `ghcr.io/openhands/agent-server:1.49.4-python` | `OBSERVED` |
| Gateway Route | `OpenRouter` (`https://openrouter.ai/api/v1`), explicit, enabled | `TEST-VERIFIED` |
| Hermes Model | `deepseek/deepseek-v4-flash-0731` (explicit, paid) | `OBSERVED` |
| OpenHands Model | `qwen/qwen3-coder` (explicit, paid) | `OBSERVED` |

---

## 3. Hard External Prerequisite Gate (Provider Readiness)

Before any billed runs were dispatched, non-billed API probes were executed against OpenRouter endpoints using the approved `OPENROUTER_API_KEY`:

| Check | Endpoint / Mechanism | Observation | Evidence Class |
|---|---|---|---|
| API Key Presence | Environment variable | Present via secure environment variable; value never printed or logged | `OBSERVED` |
| Key Tier & Quota | `GET https://openrouter.ai/api/v1/key` | `is_free_tier = false`, `limit = 10`, `limit_remaining = ~6.45 USD` | `OBSERVED` |
| Credit Balance | `GET https://openrouter.ai/api/v1/credits` | `total_credits = 10`, `total_usage = ~7.41 USD`, positive usable credits | `OBSERVED` |
| Hermes Model Endpoints | `GET .../models/deepseek/deepseek-v4-flash-0731/endpoints` | 25 active provider endpoints | `OBSERVED` |
| OpenHands Model Endpoints | `GET .../models/qwen/qwen3-coder/endpoints` | 5 active provider endpoints | `OBSERVED` |

**Conclusion:** Positive credit balance confirmed; explicit models active and available. Non-billed gate PASSED.

---

## 4. Exact Model IDs

- **Hermes Runtime Model:** `deepseek/deepseek-v4-flash-0731`
- **OpenHands Runtime Model:** `qwen/qwen3-coder`
- **Enforcement:** No fallback models, no auto-routing (`auto`, `openrouter/auto`, `auto/coding:free`), and no implicit models. Both adapters verified explicit model enforcement.

---

## 5. Hermes Certification Run 1 Evidence

- **Execution Script:** `scripts/run-cert-hermes-1.ps1`
- **Evidence Log:** `TestResults/hermes-cert-run1.txt`
- **WorkItem ID:** `a205d599-40ea-458f-a1f5-084398cb15e7`
- **AgentRun ID:** `57c4fddb-2288-42ad-a78f-6d46d1ee24bd` (Attempt 1)
- **Model:** `deepseek/deepseek-v4-flash-0731`
- **Gateway:** OpenRouter (`https://openrouter.ai/api/v1`)
- **Execution Timing:** Start: `2026-10-07T17:51:51Z`, Duration: `205s` (AgentRun), Wall Time: `277s`
- **Execution Telemetry:**
  - Turns: 16
  - Tokens: Prompt: 89,007, Completion: 6,102, Total: 321,157
  - Cost: `$0.01348155` (CostSource: `hermes:estimated_cost_usd(estimated)`)
- **Lifecycle Progression:** `Ready -> Running -> Testing -> Completed`
- **Real Model Generation:**
  - `src/Calculator.cs`: Added `public static int Multiply(int a, int b) => a * b;`
  - `tests/CalculatorTests.cs`: Added `[Fact] public void Multiply_ReturnsProduct() => Assert.Equal(42, Calculator.Multiply(6, 7));`
- **Verification Gate:** Build: True, Tests: True (`validationRun success=True build=True tests=True`)
- **Independent Focused Test:** `Multiply_ReturnsProduct` passed (1/1 passed).
- **Independent Full Suite:** `Passed! - Failed: 0, Passed: 2, Skipped: 0, Total: 2, Duration: 30 ms`
- **Independent Code Review:** `blocksCompletion = false`, 3 informational/minor findings (formatting/edge case recommendations).
- **Acceptance Gate:** Passed (All text patterns present, tests pass, review clean).
- **Workspace Integrity:** Changed files exactly `[src/Calculator.cs, tests/CalculatorTests.cs]`. Forbidden runtime state: `[]`. Files containing credential: 0.
- **Cleanup:** Factory containers before: 0; immediate after: 0; after 15s: 0. Foreign containers: 1 (`hermes`, untouched).
- **Outcome:** **PASS**

---

## 6. Hermes Certification Run 2 Evidence

- **Execution Script:** `scripts/run-cert-hermes-2.ps1`
- **Evidence Log:** `TestResults/hermes-cert-run2.txt`
- **WorkItem ID:** `1b5d4b3e-d3e9-448b-8afb-aa758b07e861`
- **AgentRun ID:** `651be2da-18b4-4ccb-8f03-e087956b2853` (Attempt 1)
- **Model:** `deepseek/deepseek-v4-flash-0731`
- **Gateway:** OpenRouter (`https://openrouter.ai/api/v1`)
- **Execution Timing:** Start: `2026-10-07T17:58:32Z`, Duration: `380s` (AgentRun), Wall Time: `461s`
- **Execution Telemetry:**
  - Turns: 8
  - Tokens: Prompt: 37,580, Completion: 3,295, Total: 140,459
  - Cost: `$0.006686552` (CostSource: `hermes:estimated_cost_usd(estimated)`)
- **Lifecycle Progression:** `Ready -> Running -> Testing -> Completed`
- **Real Model Generation:**
  - `src/Calculator.cs`: Added `public static int Multiply(int a, int b) => a * b;`
  - `tests/CalculatorTests.cs`: Added `[Fact] public void Multiply_ReturnsProduct() => Assert.Equal(42, Calculator.Multiply(6, 7));`
- **Verification Gate:** Build: True, Tests: True (`validationRun success=True build=True tests=True`)
- **Independent Focused Test:** `Multiply_ReturnsProduct` passed (1/1 passed).
- **Independent Full Suite:** `Passed! - Failed: 0, Passed: 2, Skipped: 0, Total: 2, Duration: 26 ms`
- **Independent Code Review:** `blocksCompletion = false`, 0 findings.
- **Acceptance Gate:** Passed.
- **Workspace Integrity:** Changed files exactly `[src/Calculator.cs, tests/CalculatorTests.cs]`. Forbidden runtime state: `[]`. Files containing credential: 0.
- **Cleanup:** Factory containers before: 0; immediate after: 0; after 15s: 0. Foreign containers: 1 (`hermes`, untouched).
- **Outcome:** **PASS**

---

## 7. Hermes Certification Decision

**`CERTIFIED`**

Hermes has satisfied all certification conditions across two consecutive independent runs without human intervention, manual repair, or fallback routing.

---

## 8. OpenHands Certification Run 1 Evidence

- **Execution Script:** `scripts/run-cert-openhands-1.ps1`
- **Evidence Log:** `TestResults/openhands-cert-run1-p3.txt`
- **WorkItem ID:** `12b0ee55-2100-4b99-a67f-5005d9c7b1d9`
- **AgentRun ID:** `2175d707-b12e-47c3-8aca-bf88d6d30ebc` (Attempt 1)
- **Model:** `qwen/qwen3-coder`
- **Gateway:** OpenRouter (`https://openrouter.ai/api/v1`)
- **Execution Timing:** Start: `2026-10-07T18:13:53Z`, Duration: `57s`
- **Observed Execution:**
  - Launch: Dedicated OpenHands agent-server container (`ghcr.io/openhands/agent-server:1.49.4-python`) spawned with ephemeral port and isolated credentials.
  - Model Invocation: OpenHands agent executed against OpenRouter gateway using `qwen/qwen3-coder`.
  - Code Generation: The model successfully modified both files:
    - `src/Calculator.cs`: Added `public static int Multiply(int a, int b) => a * b;`
    - `tests/CalculatorTests.cs`: Added `public void Multiply_ReturnsProduct() => Assert.Equal(42, Calculator.Multiply(6, 7));`
  - Runtime Observation & Self-Report: Because the official OpenHands image `1.49.4-python` does not contain the .NET SDK (`dotnet`), the model attempted to verify the `TestsPass` acceptance criterion and encountered missing `dotnet` CLI. It reported:
    `"execution": {"version": 1, "status": "repair_required", "summary": "Added Multiply method and test, but cannot verify tests pass due to missing dotnet installation"}`
  - Lifecycle: `AgentRun` succeeded (exit code 0), but `AgentRunWorkItemSync` faithfully interpreted `repair_required` as `ExecutionRequestedRepair`, mapping the WorkItem to `Blocked`.
  - Cleanup: Container was cleanly removed (`factoryOwned=0`).
- **Outcome:** **BLOCKED (Runtime Environment Constrained)**

---

## 9. OpenHands Certification Run 2 Evidence

- **Status:** **NOT EXECUTED**
- **Reason:** In accordance with Section 9 and Section 16, OpenHands Run 1 identified a clear runtime environment constraint (container image lacks .NET SDK). Unnecessary billed runs were halted. Hermes certification alone is sufficient to satisfy the Phase Q prerequisite.

---

## 10. OpenHands Certification Decision

**`NOT CERTIFIED — RUNTIME ENVIRONMENT CONSTRAINED`**

The OpenHands execution adapter and gateway routing function correctly, but the runtime environment image lacks the .NET toolchain required for in-container test verification.

---

## 11. Verification Evidence

The Factory verification gate was executed independently on the host across both Hermes runs:
- Independent `dotnet test --filter FullyQualifiedName~Multiply_ReturnsProduct` passed with exit code 0.
- Independent full suite `dotnet test` passed with exit code 0 (2 passed, 0 failed).
- No agent self-assertion was taken as ground truth; the Factory independently validated compilation, execution, and test outcomes.

---

## 12. Review Evidence

The Review boundary guarantees provider-neutral, framed review processing:
- Source diffs and newly created contents were packaged via `ReviewBundle` into the review prompt.
- Hermes Run 1 review output: 3 informational findings (formatting / test coverage scope), `blocksCompletion = false`.
- Hermes Run 2 review output: 0 findings, `blocksCompletion = false`.
- Both reviews parsed cleanly using the strict `<!-- REVIEW_START -->...<!-- REVIEW_END -->` envelope.

---

## 13. Acceptance Evidence

Acceptance criteria verification evaluated:
1. `ExpectedTextPresent`: `Multiply` in `src/Calculator.cs` — PASS
2. `ExpectedTextPresent`: `Add` in `src/Calculator.cs` — PASS
3. `ExpectedTextPresent`: `Multiply_ReturnsProduct` in `tests/CalculatorTests.cs` — PASS
4. `TestsPass`: Independent test suite execution — PASS
5. Independent Review completion without blocking findings — PASS
6. Workspace integrity match — PASS

---

## 14. Workspace Integrity Evidence

Both Hermes certification workspaces were audited against the positive allowlist policy:
- **Baseline:** Seed Git commit (`2688c78...` in Run 1; `98736de...` in Run 2).
- **Changed Files:** Exactly `src/Calculator.cs` and `tests/CalculatorTests.cs`.
- **Unexpected Files:** 0.
- **Forbidden Runtime State:** 0 (`conversations`, `bash_events`, `.hermes`, `.openhands`).
- **Credential Leakage:** 0 files containing credential; 0 log lines containing credential.

---

## 15. Cleanup and Orphan Audit

- **Factory Containers Before Run 1:** 0
- **Factory Containers Immediate After Run 1:** 0
- **Factory Containers 15s After Run 1:** 0
- **Factory Containers Before Run 2:** 0
- **Factory Containers Immediate After Run 2:** 0
- **Factory Containers 15s After Run 2:** 0
- **Foreign Container Isolation:** Foreign container `68b063bae8d3` (`hermes` on port 8642) remained continuously running and untouched throughout all runs.

---

## 16. Security Audit

- **Command-line Arguments:** No API keys or tokens passed in process argv.
- **Git Diffs & Artifacts:** No credentials committed or stored in artifacts.
- **Logs:** Zero occurrences of secret values in test outputs, runner logs, or audit records.
- **Environment Isolation:** Container processes received credentials strictly via container-isolated environment variables.
- **Anthropic OAuth Token:** Revocation / rotation state remains preserved (`OWNER-ASSERTED`).

---

## 17. Usage and Cost Truthfulness

Usage and costs were recorded strictly from authoritative execution metadata:
- **Hermes Run 1:** 89,007 prompt tokens, 6,102 completion tokens, 321,157 total tokens. Cost: `$0.01348155`.
- **Hermes Run 2:** 37,580 prompt tokens, 3,295 completion tokens, 140,459 total tokens. Cost: `$0.006686552`.
- **OpenHands Run 1:** Tokens not reported by container metrics. Recorded truthfully as `NOT REPORTED`.
- No cost or token counts were fabricated or inferred.

---

## 18. Default Test Safety & Full Regression Results

### Default Test Safety
- Stripped all live opt-in environment variables (`AI_FACTORY_*`) and provider credentials before running regression tests.
- All 39 live tests legitimately skipped with `SKIPPED — LIVE OPT-IN NOT ENABLED`.
- Zero billed calls or network requests dispatched during default test suite execution.

### Full Regression (3 Consecutive Runs)
Three consecutive clean regression runs were executed via `scripts/run-3-regressions.ps1` with XML TRX verification:

| Run | Application Tests | Worker Tests | API Tests | Total Tests | Passed | Failed | Skipped | Skips w/o Gate Reason | Factory Containers After |
|---|---|---|---|---|---|---|---|---|---|
| **Run 1** | 1,461 (1,456 p / 5 s) | 170 (137 p / 33 s) | 329 (328 p / 1 s) | **1,960** | **1,921** | **0** | **39** | **0** | **0** |
| **Run 2** | 1,461 (1,456 p / 5 s) | 170 (137 p / 33 s) | 329 (328 p / 1 s) | **1,960** | **1,921** | **0** | **39** | **0** | **0** |
| **Run 3** | 1,461 (1,456 p / 5 s) | 170 (137 p / 33 s) | 329 (328 p / 1 s) | **1,960** | **1,921** | **0** | **39** | **0** | **0** |

---

## 19. Certification Matrix

| Runtime | Gateway | Explicit Model | Run 1 | Run 2 | Real Gen | Verification | Review | Acceptance | Workspace | Cleanup | Final Decision |
|---|---|---|---|---|---|---|---|---|---|---|---|
| **Hermes** | OpenRouter | `deepseek/deepseek-v4-flash-0731` | PASS | PASS | PASS | PASS | PASS | PASS | PASS | PASS | **CERTIFIED** |
| **OpenHands** | OpenRouter | `qwen/qwen3-coder` | BLOCKED (Env) | NOT EXECUTED | PASS (Code) | BLOCKED | BLOCKED | BLOCKED | PASS | PASS | **NOT CERTIFIED** |

---

## 20. Modified Files

1. **Stale October 5 Conflict Removals:**
   - `apps/src/AI.Factory.Application/Orchestration/IReviewBundleBuilder.cs` (deleted)
   - `apps/src/AI.Factory.Application/Orchestration/ReviewBundleBuilder.cs` (deleted)
   - `apps/tests/AI.Factory.Application.Tests/Orchestration/ReviewBundleBuilderTests.cs` (deleted)
   - `apps/src/AI.Factory.Application/Orchestration/ExecutionWorkspaceSeed.cs` (deleted)
   - `apps/src/AI.Factory.Application/Orchestration/ReviewPayloadParser.cs` (deleted)
   - `apps/tests/AI.Factory.Application.Tests/Orchestration/ReviewPayloadParserTests.cs` (deleted)
   - `apps/tests/AI.Factory.Application.Tests/Orchestration/ModelFailClosedRoutingTests.cs` (deleted)
   - `apps/src/AI.Factory.Application/Gateway/ModelIdentifier.cs` (deleted)
   - `apps/src/AI.Factory.Infrastructure/Agents/ReviewRunDiagnosticsProbe.cs` (deleted)
2. **Certification Scripts & Tooling:**
   - `scripts/run-cert-hermes-1.ps1` (retained for Hermes Run 1)
   - `scripts/run-cert-hermes-2.ps1` (created for Hermes Run 2)
   - `scripts/run-cert-openhands-1.ps1` (corrected test project path)
   - `scripts/run-cert-openhands-2.ps1` (created for OpenHands Run 2)
3. **Evidence Artifacts:**
   - `TestResults/hermes-cert-run1.txt`
   - `TestResults/hermes-cert-run2.txt`
   - `TestResults/openhands-cert-run1-p3.txt`
4. **Documentation:**
   - `docs/phase-p3-report.md` (this report)

---

## 21. Remaining Blockers

- **OpenHands Toolchain:** The `ghcr.io/openhands/agent-server:1.49.4-python` container image lacks the .NET runtime/SDK for in-container testing. Future phases can supply a multi-runtime container image if OpenHands certification is desired.
- **Factory Orchestration Core:** Zero remaining engineering blockers.

---

## 22. Phase Q Readiness Decision

**`GO FOR PHASE Q`**

All Phase P certification criteria are satisfied. Hermes has achieved 2/2 consecutive successful real model-backed certification runs with complete lifecycle verification. Phase Q is unlocked.
