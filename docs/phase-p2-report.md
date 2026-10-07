# Phase P.2 Result — Model-Backed Certification Closure & Phase Q Readiness

Date: 2026-10-07  
Status: **LIMITED — EXTERNAL RUNTIME CONSTRAINT**  
Reference: Phase P.1 Report (`docs/phase-p1-report.md`) & Phase P.2 Specification (`prompts/AI Factory — Phase P.2_ Model-Backed Certification Closure & Phase Q Readiness.md`)

---

## 1. Final Gate

**`LIMITED — EXTERNAL RUNTIME CONSTRAINT`**

### Rationale
- **Factory Engineering State:** All Factory review, acceptance, integrity, and routing defect fixes developed in Phase P.1 are fully stable, verified by clean builds and three consecutive 100% green solution-wide regression runs (1,960 tests each: 1,921 passed, 39 skipped, 0 failed).
- **External Provider Constraint:** A non-billed API probe against the configured OpenRouter gateway (`https://openrouter.ai/api/v1/key` and `/credits`) using the active `OPENROUTER_API_KEY` confirms that the account remains on the uncredited tier (`key.is_free_tier=True`, `credits.total_credits=0`). Both configured explicit models (`deepseek/deepseek-v4-flash-0731` and `qwen/qwen3-coder`) reject execution with HTTP 402 / credit exhaustion errors.
- **Contract Enforcement:** Under Phase P.2 instruction #17 ("If credits are still exhausted: STOP all billed runs. Final status: LIMITED — EXTERNAL RUNTIME CONSTRAINT. Do the non-billed verification only. Do NOT fabricate certification") and Phase Q readiness rules, the final gate is strictly **`LIMITED — EXTERNAL RUNTIME CONSTRAINT`**. Phase Q benchmark is prohibited until two consecutive live repeatability runs succeed for a certified runtime.

---

## 2. External Provider Readiness

Before executing billed runs, the external provider status was probed using non-billed endpoints:

| Check | Endpoint | Observation | Evidence Class |
|---|---|---|---|
| OpenRouter Key Presence | Process environment | Key present; value never printed | `VERIFIED LIVE` |
| Key Tier & Quota | `GET https://openrouter.ai/api/v1/key` | `is_free_tier=True`, `limit=null`, `limit_remaining=null`, `usage=0` | `VERIFIED LIVE` |
| Credit Balance | `GET https://openrouter.ai/api/v1/credits` | `total_credits=0`, `total_usage=0` | `VERIFIED LIVE` |
| Hermes Model Endpoints | `GET .../models/deepseek/deepseek-v4-flash-0731/endpoints` | 26 active provider endpoints | `VERIFIED LIVE` |
| OpenHands Model Endpoints | `GET .../models/qwen/qwen3-coder/endpoints` | 5 active provider endpoints | `VERIFIED LIVE` |
| Provider Live Billing Response | Live execution probe | `insufficient credits. This account never purchased credits.` | `VERIFIED LIVE` |

**Conclusion:** The external gateway is operational and the explicit models are routed, but the API key has zero purchased credits. All billed live runs were stopped in compliance with Phase P.2 instructions.

---

## 3. Baseline

| Item | Value / Observation | Evidence Class |
|---|---|---|
| Workspace | `c:\AI-Factory` (no `.git` directory in workspace root) | `VERIFIED LIVE` |
| Build Status | `dotnet build apps/AI.Factory.slnx --no-incremental` — 0 errors, 3 warnings (pre-existing redundant using directives in test files) | `VERIFIED BY TEST EXECUTION` |
| Docker Host | Docker Desktop operational | `VERIFIED LIVE` |
| Foreign Containers | `e9a87be01b8e` (`hermes` on port 8642) Up, untouched | `VERIFIED LIVE` |
| Factory Containers Before | **0** | `VERIFIED LIVE` |
| Hermes Image | `nousresearch/hermes-agent:0.20.6` (`sha256:bcced061c9e1...`) | `VERIFIED LIVE` |
| OpenHands Image | `ghcr.io/openhands/agent-server:1.49.4-python` (`sha256:9b215fb9ad53...`) | `VERIFIED LIVE` |
| Configured Gateway | `OpenRouter` (`https://openrouter.ai/api/v1`), explicit, enabled | `VERIFIED BY SOURCE` |
| Explicit Models | Hermes: `deepseek/deepseek-v4-flash-0731`; OpenHands: `qwen/qwen3-coder` | `VERIFIED BY SOURCE` |

---

## 4. Hermes Certification Run 1

- **Runtime:** `Hermes` (`nousresearch/hermes-agent:0.20.6`)
- **Gateway:** `OpenRouter` (`https://openrouter.ai/api/v1`)
- **Model:** `deepseek/deepseek-v4-flash-0731` (explicit, paid)
- **Task Contract:** Disposable Git workspace; `Calculator.Multiply(int a, int b)` with deterministic unit test `Multiply_ReturnsProduct`.
- **Observed Execution:**
  - Launch: Fresh container created with Factory label.
  - Gateway Interaction: Request dispatched to OpenRouter; provider returned HTTP billing rejection: `insufficient credits. This account never purchased credits. Make sure your key is on the correct account or org, and if so, purchase more at https://openrouter.ai/settings/credits`.
  - Attribution: Adapter extracted HTTP error tail; structured metadata populated:
    `{"ExecutionAdapter":"Hermes","ExitReason":"exit:0","Gateway":"OpenRouter","GatewayFailure":"Billing"}`.
  - Lifecycle: WorkItem transitioned `Ready -> Running -> Failed`. No verification or acceptance was attempted.
  - Cleanup: Container removed immediately (`factoryOwned=0`), confirmed after 15s (`factoryOwned=0`). Foreign containers untouched.
- **Classification:** `ENVIRONMENT BLOCKED — PROVIDER CREDITS EXHAUSTED`

---

## 5. Hermes Certification Run 2

- **Status:** **STOPPED — NOT EXECUTED**
- **Reason:** In accordance with Phase P.2 instructions, when Run 1 demonstrates that provider credits remain exhausted, billed live attempts must cease immediately rather than generating redundant billing failures.
- **Classification:** `ENVIRONMENT BLOCKED`

---

## 6. OpenHands Certification Run 1

- **Runtime:** `OpenHands` (`ghcr.io/openhands/agent-server:1.49.4-python`)
- **Gateway:** `OpenRouter` (`https://openrouter.ai/api/v1`)
- **Model:** `qwen/qwen3-coder` (explicit, paid)
- **Task Contract:** Disposable Git workspace; `Calculator.Multiply(int a, int b)` with deterministic unit test `Multiply_ReturnsProduct`.
- **Observed Execution:**
  - Launch: Dedicated agent-server container created.
  - Gateway Interaction: Agent-server initiated conversation with OpenRouter; provider rejected API calls due to credit exhaustion.
  - Lifecycle: OpenHands conversation transitioned to error state; WorkItem transitioned `Ready -> Running -> Failed`.
  - Cleanup: Container removed immediately (`factoryOwned=0`), confirmed after 15s (`factoryOwned=0`).
- **Classification:** `ENVIRONMENT BLOCKED — PROVIDER CREDITS EXHAUSTED`

---

## 7. OpenHands Certification Run 2

- **Status:** **STOPPED — NOT EXECUTED**
- **Reason:** Stopped due to external credit exhaustion on Run 1.
- **Classification:** `ENVIRONMENT BLOCKED`

---

## 8. Real Model Generation Evidence

- **Phase P.2 Status:** `ENVIRONMENT BLOCKED` (external provider credit exhaustion prevented model generation).
- **Historical Context:** Real model generation through the Factory path was previously proven in Phase P (Run H2 generated `Multiply` and `Multiply_ReturnsProduct` via Hermes; Runs O1–O3 generated implementations via OpenHands). Phase P.2 strictly records `ENVIRONMENT BLOCKED` for the current live certification attempts.

---

## 9. Verification Evidence

The Factory verification boundary enforces build, unit test execution, and workspace integrity prior to acceptance:

- **Build & Test Gate:** Tested deterministically across all application tests.
- **Integrity Integration:** Verification fails closed if the workspace contains unapproved files, scripts, or unexpected directories.
- **Failure Non-Bypass:** Verified that an `AgentRun` failure or verification failure prevents transition to `Review` or `Acceptance`.
- **Classification:** `VERIFIED BY TEST EXECUTION`

---

## 10. Review Evidence

The Review boundary hardening from Phase P.1 was verified across 15 dedicated deterministic test cases in [`ReviewBoundaryIntegrityTests`](file:///c:/AI-Factory/apps/tests/AI.Factory.Application.Tests/Review/ReviewBoundaryIntegrityTests.cs):

1. **Diff & Content Presence:**
   - Unified diffs and newly created file contents are extracted by [`GitReviewBundleBuilder`](file:///c:/AI-Factory/apps/src/AI.Factory.Infrastructure/Validation/GitReviewBundleBuilder.cs) and injected into the reviewer prompt.
   - Verified by `ReviewInput_UnifiedDiffAndNewlyCreatedContent_AreInjectedIntoPrompt`.
2. **Transport Framing & Bracket Isolation:**
   - Prompt mandates `<!-- REVIEW_START -->...<!-- REVIEW_END -->`.
   - `AgentCodeReviewer.ExtractJsonCandidate` skips operational log headers (e.g. `[s6-init]`) by matching `[` followed only by `{` or `]`.
   - Verified by `ReviewParsing_StartupLogsPrecedingValidJsonArray_ParsesFindingsCleanly` and `ReviewParsing_StartupLogsPrecedingFramedPayload_ExtractsFramedJson`.
3. **Fail-Closed Malformed/Missing Handling:**
   - Unparseable JSON, schema violations, missing payloads, reviewer timeouts, cancellations, or process failures produce `ReviewFindingSeverity.Critical` and set `BlocksCompletion = true`.
   - Verified by `ReviewParsing_MalformedPayload_NeverBecomesInformationalSuccess_BlocksCompletion` and `ReviewParsing_MissingResult_EmptyOutput_BlocksCompletion`.
4. **Lifecycle Enforcement:**
   - Reviewer failure or critical findings block completion and return the WorkItem to `Repairing`.
   - Verified by `ReviewLifecycle_ReviewerFailure_CanNeverBecomeSuccessfulReview` and `ReviewLifecycle_BlockingFinding_BlocksCompletion`.
- **Classification:** `VERIFIED BY TEST EXECUTION`

---

## 11. Acceptance Evidence

Acceptance criteria verification evaluates:
1. `ExpectedTextPresent` for requested symbols (`Multiply`, `Add`, `Multiply_ReturnsProduct`).
2. `TestsPass` for independent test suite execution.
3. Successful completion of independent Review without blocking findings.
4. Clean workspace integrity matching baseline + allowed additions.
- **Lifecycle Invariant:** Acceptance is never evaluated when implementation or verification has failed.
- **Classification:** `VERIFIED BY TEST EXECUTION`

---

## 12. Workspace Integrity

The positive allowlist model in [`WorkspaceIntegrityPolicy`](file:///c:/AI-Factory/apps/src/AI.Factory.Application/Orchestration/WorkspaceIntegrityPolicy.cs) was verified across 8 dedicated test cases in [`WorkspaceIntegrityPositiveAllowlistTests`](file:///c:/AI-Factory/apps/tests/AI.Factory.Application.Tests/Orchestration/WorkspaceIntegrityPositiveAllowlistTests.cs):

| Invariant | Test | Result |
|---|---|---|
| Clean Workspace | `CleanWorkspace_ReturnsZeroViolations` | Passed (0 violations) |
| Permitted Source/Test Additions | `PermittedIntendedArtifacts_PassIntegrityCheck` | Passed (0 violations) |
| Unexpected Ordinary File | `UnexpectedOrdinaryFile_IsFlaggedAsViolation` | Flagged as violation |
| Unicode Filename (U+F00D) | `UnexpectedUnicodeFilename_IsFlaggedWithLosslessCodePointFormatting` | Flagged with lossless `U+XXXX` format |
| Unexpected Hidden File | `UnexpectedHiddenFile_IsFlaggedAsViolation` | Flagged as violation |
| Credential-Like Artifact | `CredentialLikeArtifact_IsFlaggedAsViolation` | Flagged as violation |
| Unexpected Directory | `UnexpectedDirectory_IsFlaggedAsViolation` | Flagged as violation |
| Nested Git Repository / Submodule | `UnexpectedNestedRepositoryOrGitLink_IsFlaggedAsViolation` | Flagged as violation |

- **Classification:** `VERIFIED BY TEST EXECUTION`

---

## 13. Cleanup / Orphan Audit

- **Factory Containers Before:** **0**
- **Factory Containers After Regression:** **0**
- **Factory Containers After Live Hermes Probe:** **0** (immediately and after 15s)
- **Factory Containers After Live OpenHands Probe:** **0** (immediately and after 15s)
- **Foreign Container Isolation:** Standalone `hermes` container (`e9a87be01b8e`) remained continuously running and untouched across all operations.
- **Classification:** `VERIFIED LIVE`

---

## 14. Credential Security

- **Command Line Arguments:** 0 process arguments contained `OPENROUTER_API_KEY`.
- **Logs & Test Artifacts:** 0 log lines, diffs, or review bundles contained credential values.
- **Environment Isolation:** Container processes receive credentials strictly via isolated child environment variables; host `~/.hermes` and local configuration files are never mounted.
- **Phase N Credential Rotation:** Maintained as `OWNER-ASSERTED — TOKEN ROTATION/REVOCATION CONFIRMED`.
- **Classification:** `VERIFIED BY TEST EXECUTION` & `VERIFIED LIVE`

---

## 15. Usage / Cost Truthfulness

- **Policy:** Token usage and costs are recorded only when explicitly reported by the runtime/provider.
- **Phase P.2 Observation:** In the credit-blocked live attempts, token usage was reported as `NOT REPORTED` (null). No token usage or approximate cost was estimated or fabricated.
- **Classification:** `VERIFIED LIVE`

---

## 16. Default Test Safety

Default test execution was validated to ensure strict safety:
- All opt-in environment variables (`AI_FACTORY_*`) and provider keys were stripped prior to execution.
- Default `dotnet test apps/AI.Factory.slnx` contacted no external model providers.
- All 39 live tests legitimately skipped with `SKIPPED — LIVE OPT-IN NOT ENABLED`.
- No live test passed silently without execution.
- **Classification:** `VERIFIED BY TEST EXECUTION`

---

## 17. Full Regression

Three consecutive solution-wide regression runs were executed after a clean build (`--no-incremental`). Results were parsed directly from TRX files using the XML parser script [`scripts/parse-trx.ps1`](file:///c:/AI-Factory/scripts/parse-trx.ps1):

| Run | Application Tests | Worker Tests | API Tests | Total Tests | Passed | Failed | Skipped | Skips w/o Gate Reason | Factory Containers After |
|---|---|---|---|---|---|---|---|---|---|
| **Run 1** | 1,461 (1,456 p / 5 s) | 170 (137 p / 33 s) | 329 (328 p / 1 s) | **1,960** | **1,921** | **0** | **39** | **0** | **0** |
| **Run 2** | 1,461 (1,456 p / 5 s) | 170 (137 p / 33 s) | 329 (328 p / 1 s) | **1,960** | **1,921** | **0** | **39** | **0** | **0** |
| **Run 3** | 1,461 (1,456 p / 5 s) | 170 (137 p / 33 s) | 329 (328 p / 1 s) | **1,960** | **1,921** | **0** | **39** | **0** | **0** |

- **Comparison with Phase P.1:** Identical baseline (1,960 total, 1,921 passed, 39 skipped, 0 failed).
- **Classification:** `VERIFIED BY TEST EXECUTION`

---

## 18. Certification Matrix

| Runtime | Gateway | Explicit Model | Run 1 | Run 2 | Generation | Verification | Review | Acceptance | Cleanup | Final Status |
|---|---|---|---|---|---|---|---|---|---|---|
| **Hermes** | OpenRouter | `deepseek/deepseek-v4-flash-0731` | `BLOCKED` (402) | `BLOCKED` | `ENVIRONMENT BLOCKED` | `VERIFIED TEST` | `VERIFIED TEST` | `VERIFIED TEST` | `VERIFIED LIVE` | **ENVIRONMENT BLOCKED** |
| **OpenHands** | OpenRouter | `qwen/qwen3-coder` | `BLOCKED` (402) | `BLOCKED` | `ENVIRONMENT BLOCKED` | `VERIFIED TEST` | `VERIFIED TEST` | `VERIFIED TEST` | `VERIFIED LIVE` | **ENVIRONMENT BLOCKED** |
| **Claude Code** | None (CLI) | Unmapped | Not run | Not run | `ENVIRONMENT BLOCKED` | — | — | — | — | **ENVIRONMENT BLOCKED** |
| **Codex** | None (CLI) | Unmapped | Not run | Not run | `ENVIRONMENT BLOCKED` | — | — | — | — | **ENVIRONMENT BLOCKED** |

*Note: No runtime is marked CERTIFIED.*

---

## 19. Remaining External Constraints

1. **OpenRouter Account Balance:** The active API key lacks purchased credits, returning HTTP 402 billing errors on both `deepseek/deepseek-v4-flash-0731` and `qwen/qwen3-coder`.
2. **Claude Code / Codex Setup:** Local CLI installations and authentication tokens remain unconfigured in this environment.

---

## 20. Phase Q Readiness

**`NO`**

### Reason
Phase Q (Benchmark) requires that at least one runtime achieve `CERTIFIED` status via two consecutive successful live certification runs. Because external gateway credit exhaustion prevents live model generation, no runtime is certified. Phase Q benchmark is strictly prohibited until credit is added and certification re-entry is completed.

---

## 21. Worktree Status

- **Uncommitted Code Modifications:**
  - `apps/src/AI.Factory.Application/Abstractions/ICodeReviewer.cs`
  - `apps/src/AI.Factory.Application/Orchestration/ReviewContextBuilder.cs`
  - `apps/src/AI.Factory.Application/Orchestration/AgentCodeReviewer.cs`
  - `apps/src/AI.Factory.Application/Orchestration/WorkspaceIntegrityPolicy.cs`
  - `apps/src/AI.Factory.Application/Orchestration/ModelRoutingOptions.cs`
  - `apps/src/AI.Factory.Application/Orchestration/ModelRouter.cs`
  - `apps/src/AI.Factory.Application/Gateway/ModelGatewayResolver.cs`
  - `apps/src/AI.Factory.Infrastructure/Agents/HermesAgentExecutor.cs`
  - `apps/src/AI.Factory.Infrastructure/Agents/HermesExecutionAdapter.cs`
  - `apps/src/AI.Factory.Infrastructure/Agents/OpenHands/OpenHandsExecutionAdapter.cs`
  - `apps/src/AI.Factory.Worker/Program.cs`
  - `apps/src/AI.Factory.Api/Program.cs`
  - `apps/src/AI.Factory.Worker/appsettings.json`
  - `apps/src/AI.Factory.Api/appsettings.json`
  - `apps/tests/AI.Factory.Worker.Tests/ModelBackedCertificationTests.cs`
  - `docs/execution-runtimes.md`
  - `docs/local-setup.md`
- **Created Documentation & Scripts:**
  - `docs/phase-p1-report.md`
  - `docs/phase-p2-report.md`
  - `scripts/parse-trx.ps1`
  - `scripts/run-3-regressions.ps1`
- **Git Commits/Tags:** 0 commits, 0 tags, 0 remote pushes.

---

## 22. Final Evidence Classification

| Aspect | Classification | Basis |
|---|---|---|
| Review Bundle Contract & Source Diff Injection | `VERIFIED BY TEST EXECUTION` | 15 passing tests in `ReviewBoundaryIntegrityTests` |
| Transport Framing & Log Bracket Isolation | `VERIFIED BY TEST EXECUTION` | Tests simulating startup logs and framed JSON payloads |
| Review Schema Validation & Fail-Closed Behavior | `VERIFIED BY TEST EXECUTION` | Tests validating critical findings on parse/runtime failures |
| Workspace Integrity Positive Allowlist (G-P3 Fix) | `VERIFIED BY TEST EXECUTION` | 8 passing tests in `WorkspaceIntegrityPositiveAllowlistTests` |
| Lossless Non-ASCII / Unicode Path Formatting | `VERIFIED BY TEST EXECUTION` | `WorkspaceIntegrityPolicy.FormatLossless` test assertions |
| Fail-Closed Model Routing & Auto Rejection | `VERIFIED BY TEST EXECUTION` | 8 passing tests in `ModelRoutingSafetyTests` |
| Gateway Failure Attribution (Extraction from Output) | `VERIFIED BY TEST EXECUTION` & `VERIFIED LIVE` | Unit tests & live HTTP 402 attribution to `GatewayFailure = "Billing"` |
| Container Cleanup & Foreign Resource Isolation | `VERIFIED LIVE` | Docker ps audits showing 0 Factory containers before and after |
| Non-Live Default Regression (3 Runs) | `VERIFIED BY TEST EXECUTION` | 3 × 1,960 tests (1,921 passed, 39 skipped, 0 failed), TRX XML verified |
| Live Model Generation | `ENVIRONMENT BLOCKED` | External OpenRouter account has 0 credits |
| Final Gate | `LIMITED — EXTERNAL RUNTIME CONSTRAINT` | Factory ready; external provider credit unavailable |
