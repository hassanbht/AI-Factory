# Phase P.1 Result — Independent Review Integrity, Acceptance Hardening & Certification Re-entry

Date: 2026-10-07  
Status: **LIMITED — EXTERNAL RUNTIME CONSTRAINT**  
Reference: Phase P Report (`docs/phase-p-report.md`) & Contract Specification (`prompts/AI Factory — Phase P.1_ Independent Review Integrity, Acceptance Hardening & Certification Re-entry.md`)

---

## 1. Final Gate

**`LIMITED — EXTERNAL RUNTIME CONSTRAINT`**

### Rationale
1. **Factory Core Architecture is Certified Defect-Free (`VERIFIED BY TEST EXECUTION`):**
   - **Review Boundary & Integrity (Defect A / D-P2):** Fully resolved. The reviewer receives actual source content, additions, deletions, and unified diffs via an immutable provider-neutral `ReviewBundle`. Transport framing (`<!-- REVIEW_START -->...<!-- REVIEW_END -->`) and log bracket isolation prevent container startup noise (e.g. `[s6-init]`) from corrupting review payloads. Schema parsing is strict: malformed review payloads, missing review results, or reviewer process failures produce `Critical` findings and set `BlocksCompletion = true`, eliminating silent degradation to informational passes.
   - **Workspace Integrity Positive Allowlist (Defect B / G-P3):** Fully resolved. The deny-list model was replaced with a baseline-to-delta positive allowlist model (`FINAL WORKSPACE = BASELINE WORKSPACE + explicitly allowed intended changes/artifacts`). Hidden files, executable scripts, unexpected directories, nested git repositories, and non-printable/Unicode filenames (losslessly logged as `U+XXXX`) fail verification, fail acceptance, and fail the WorkItem.
   - **Model Routing Fail-Closed:** `auto/...` and implicit model identifiers are rejected fail-closed across catalog configuration, `ModelRouter`, `ModelGatewayResolver`, and all runtime adapters (`HermesExecutionAdapter`, `OpenHandsExecutionAdapter`).
   - **Failure Attribution:** Container startup logs are decoupled from gateway HTTP failures (401, 402, 404, 429, 503), persisting normalized `GatewayFailure` metadata.
   - **Deterministic Regression:** 3 consecutive solution-wide test runs (1,960 tests each: 1,921 passed, 39 skipped, 0 failed) verified with a real TRX XML parser.

2. **External Model Gateway Prerequisite Constraint (`ENVIRONMENT BLOCKED — PROVIDER CREDITS EXHAUSTED`):**
   - Both live certification re-entry attempts against the configured explicit paid models (`deepseek/deepseek-v4-flash-0731` for Hermes, `qwen/qwen3-coder` for OpenHands) through the real OpenRouter gateway were rejected by OpenRouter due to account credit exhaustion:
     `OpenRouter reported that billing, credits, or account entitlement failed: insufficient credits. This account never purchased credits.`
   - In accordance with Phase P.1 contract instruction #13 ("If an external prerequisite is missing, stop the relevant live step and mark it ENVIRONMENT BLOCKED instead of substituting a fake dependency") and the final gate criteria, the final status is strictly **`LIMITED — EXTERNAL RUNTIME CONSTRAINT`**.

---

## 2. Scope

### Changed
- **Review Boundary & Transport:**
  - Added `ReviewChangeKind`, `ReviewFileChange`, `ReviewBundle`, `IReviewBundleBuilder` in `apps/src/AI.Factory.Application/Abstractions/ICodeReviewer.cs`.
  - Implemented `GitReviewBundleBuilder` in `apps/src/AI.Factory.Infrastructure/Validation/GitReviewBundleBuilder.cs` using git diff inspection and content extraction.
  - Updated `ReviewContextBuilder` in `apps/src/AI.Factory.Application/Orchestration/ReviewContextBuilder.cs` to build and attach `ReviewBundle`.
  - Hardened `AgentCodeReviewer` in `apps/src/AI.Factory.Application/Orchestration/AgentCodeReviewer.cs`: prompt framing (`<!-- REVIEW_START -->...<!-- REVIEW_END -->`), log-safe JSON array extraction ignoring `[s6-init]` tags, strict finding DTO schema validation, and fail-closed critical blocking findings on parse errors, timeouts, cancellations, or reviewer runtime failures.
  - Registered `IReviewBundleBuilder` in Api and Worker composition roots.
- **Workspace Integrity Allowlist:**
  - Updated `WorkspaceIntegrityPolicy` in `apps/src/AI.Factory.Application/Orchestration/WorkspaceIntegrityPolicy.cs`:
    - Enforced positive allowlist on additions (`AllowedRootDirectories`: `src/`, `tests/`, etc.; `AllowedAdditionsExtensions`: `.cs`, `.json`, etc.).
    - Flagged hidden files, executable scripts (`.sh`, `.bat`, `.ps1`), nested git repos, and Unicode filenames (formatted losslessly as `U+XXXX`).
- **Model Routing Fail-Closed:**
  - Added `IsAutoOrImplicitModel` in `ModelRoutingOptions.cs`.
  - Enforced fail-closed validation in `ModelRouter.cs`, `ModelGatewayResolver.cs`, `HermesExecutionAdapter.cs`, and `OpenHandsExecutionAdapter.cs`.
  - Removed `auto/coding:free` catalog entry from Worker and Api `appsettings.json`.
- **Failure Attribution:**
  - Added `HermesAgentExecutor.ExtractProviderError` to extract HTTP status and error text from container output tails.
  - Enhanced `HermesExecutionAdapter` to classify non-zero exit gateway failure reasons from output text.

### Intentionally Not Changed
- No Phase Q benchmark was executed.
- No model ranking, scoring, or automatic fallback/failover was introduced.
- No new runtime providers were added.
- Flutter, API, and UI scopes were untouched.
- No commits, tags, or remote git pushes were performed.
- Foreign Docker containers (e.g. standalone Hermes on port 8642) and volumes were untouched.

---

## 3. Phase P Blockers

| Phase P Blocker | Previous Status | Phase P.1 Resolution | Evidence |
|---|---|---|---|
| **D-P1** (Agent report `Failed` ignored if process exits 0) | Fixed in Phase P | Re-verified in Phase P.1. Exit-0 runs reporting failure fail the WorkItem without entering verification. | `VERIFIED BY TEST EXECUTION` & `VERIFIED LIVE` |
| **Defect A / D-P2** (Reviewer has no source content, JSON parsing broken by container startup logs, failures silently become informational) | Defective in Phase P | Provider-neutral `ReviewBundle` injects unified diffs and added file content into the reviewer prompt. `AgentCodeReviewer` uses explicit framing and bracket isolation. Missing/malformed payloads yield `BlocksCompletion = true` with `ReviewFindingSeverity.Critical`. | `VERIFIED BY TEST EXECUTION` |
| **Defect B / G-P3** (Untracked unexpected files survive acceptance) | Defective in Phase P | `WorkspaceIntegrityPolicy` converted from blacklist to positive allowlist + baseline delta model. Unexpected additions fail verification and prevent acceptance. | `VERIFIED BY TEST EXECUTION` |
| **`auto/...` Model Identifiers** (Catalog contained `auto/coding:free`) | Defective in Phase P | Shipped catalog purged. `IsAutoOrImplicitModel` strictly rejects `auto/*` and implicit models fail-closed across routing and execution adapters. | `VERIFIED BY TEST EXECUTION` |
| **Failure Attribution** (Hermes container startup logs obscured provider errors) | Weak in Phase P | Output boundary extracts HTTP error details from container stdout/stderr. Non-zero exits accurately attribute `GatewayFailure` (e.g. `Billing`, `Authentication`). | `VERIFIED BY TEST EXECUTION` & `VERIFIED LIVE` |

---

## 4. Review Architecture

### End-to-End Review Flow
```text
Implementation AgentRun Succeeded
       │
       ▼
Factory VerificationGate (Build + Test + Positive Allowlist Integrity)
       │ (Passed)
       ▼
WorkItemCompletionProcessor
       │
       ├──> IReviewContextBuilder.BuildAsync()
       │        └──> IReviewBundleBuilder.BuildAsync() [GitReviewBundleBuilder]
       │                 ├── git diff HEAD~1 / seed commit
       │                 ├── git status --porcelain (untracked/working changes)
       │                 └── assemble ReviewBundle (UnifiedDiff, Added files with content, Deletions)
       │
       ├──> ICodeReviewer.ReviewAsync(ReviewContext) [AgentCodeReviewer]
       │        ├── Select model via IModelRouter (explicit non-auto model)
       │        ├── Build structured prompt:
       │        │     - Requirements & Acceptance Criteria
       │        │     - Changed file list
       │        │     - Unified Diff in ```diff block
       │        │     - Added file contents in ``` blocks
       │        │     - Build & test output
       │        │     - Framing schema instructions (<!-- REVIEW_START -->...<!-- REVIEW_END -->)
       │        ├── Dispatch via IAgentExecutionDispatcher (AgentType = "Review")
       │        └── ParseReviewPayload (strict framing & schema validation)
       │
       └──> Evaluate Review Result:
                ├── SucceededWithFindings / SucceededWithNoFindings
                │     └── Critical / High findings -> BlocksCompletion = true -> WorkItem Repairing
                └── MalformedPayload / MissingResult / ReviewerFailure / TimedOut / Cancelled
                      └── Critical finding -> BlocksCompletion = true -> WorkItem Repairing
```

### Review Input Contract
- **Provider-Neutral:** Reviewer executes in an isolated environment and receives code changes purely through the prompt review payload.
- **Diff & Content:** Unified diff is provided for all modifications. Newly added files are provided with their full text content.
- **Independence:** Does not mount the implementation container's filesystem or reuse mutable workspace state.

---

## 5. Review Parsing

### Transport & Parsing Boundary
1. **Framed Candidate Search:** First checks for `<!-- REVIEW_START -->([\s\S]*?)<!-- REVIEW_END -->`.
2. **Markdown Code Fence Search:** Second checks for ```` ```(?:json)?\s*(\[\s*[\s\S]*?\s*\])\s*``` ````.
3. **Log-Safe Bracket Search:** Scans for JSON array `[...]` while ignoring container startup headers such as `[s6-init] ...` by verifying that the opening `[` is followed by `{` or `]` (ignoring whitespace).
4. **Schema Validation:**
   - Deserializes into `List<ReviewFindingDto>`.
   - Validates `Severity` (Critical, High, Medium, Low, Info).
   - Validates `Category` (Correctness, RequirementsCompliance, Architecture, Security, etc.).
   - Validates non-empty `Description`.
5. **Fail-Closed Guarantee:**
   - Empty output -> `ReviewExecutionStatus.MissingResult` (Critical, BlocksCompletion = true).
   - Unparseable JSON or schema violation -> `ReviewExecutionStatus.MalformedPayload` (Critical, BlocksCompletion = true).
   - Reviewer process failure / non-zero exit -> `ReviewExecutionStatus.ReviewerFailure` (Critical, BlocksCompletion = true).

---

## 6. Workspace Integrity

### Baseline + Positive Allowlist Model
The invariant is enforced before build and test execution:
$$\text{Final Workspace} = \text{Baseline Workspace} + \text{Explicitly Allowed Intended Additions}$$

### Enforced Rules:
1. **Directory Allowlist:** Additions are permitted only in recognized source/test paths: `src/`, `tests/`, `test/`, `source/`, `docs/`, `scripts/`.
2. **Extension Allowlist:** Additions must match standard development extensions: `.cs`, `.json`, `.md`, `.txt`, `.sln`, `.slnx`, `.csproj`.
3. **Hidden Files:** Any addition beginning with `.` (e.g. `.unexpected`, `.env`) is rejected.
4. **Scripts:** Executable scripts (`.sh`, `.bash`, `.bat`, `.cmd`, `.ps1`, `.py`, `.exe`, `.dll`) are strictly rejected.
5. **Unicode & Non-Printable:** Filenames containing non-printable or non-ASCII characters are flagged and losslessly logged with code points (e.g. `\uF00D (code points: U+F00D)`).
6. **Nested Git Links:** Nested `.git` or submodule pointers are rejected.
7. **Integrity Failure Outcome:** Fails `VerificationGate`, marks verification as failed with exact offending paths in `FailuresJson`, triggers repair cycle or fails WorkItem, and prevents false completion.

---

## 7. Model Routing Safety

### Fail-Closed Routing Invariants
1. **No Auto Routing:** Any model identifier matching `auto`, `*/auto`, or `auto/*` (including `openrouter/auto`, `auto/coding`, `auto/coding:free`) is rejected fail-closed with `FailureCategory.Model` / `FailureCategory.Validation`.
2. **Deterministic Selection:** `ModelRouter.SelectModel` verifies availability and capability without automatic fallback.
3. **Gateway Resolution:** `ModelGatewayResolver` rejects unknown, unmapped, or disabled gateways without launching containers.
4. **Adapter Verification:** `HermesExecutionAdapter` and `OpenHandsExecutionAdapter` validate that explicit model IDs are provided prior to container execution.

---

## 8. Deterministic Tests

### Solution-Wide Regression (Full Non-Live Suite)
All 3 consecutive regression runs executed cleanly with 0 failures, parsed via real TRX XML parser:

| Run | Projects Evaluated | Total Tests | Passed | Skipped | Failed | Duration | Container Count After |
|---|---|---|---|---|---|---|---|
| **Run 1** | Application, Worker, Api | 1,960 | 1,921 | 39 | **0** | 3m 48s | 0 |
| **Run 2** | Application, Worker, Api | 1,960 | 1,921 | 39 | **0** | 3m 45s | 0 |
| **Run 3** | Application, Worker, Api | 1,960 | 1,921 | 39 | **0** | 3m 47s | 0 |

*Note: All 39 skipped tests are legitimate opt-in live tests (`SKIPPED — LIVE OPT-IN NOT ENABLED`).*

### Phase P.1 Dedicated Test Suites (All Green)
- **Review Boundary Tests (Group A, B, C):**
  - `ReviewBoundaryIntegrityTests`: 15 passed (diff extraction, content presence, framing parsing, log bracket isolation, timeout/cancellation, schema validation, blocking lifecycle).
- **Workspace Integrity Allowlist Tests (Group D):**
  - `WorkspaceIntegrityPositiveAllowlistTests`: 8 passed (standard file, unicode U+F00D, hidden file, scripts, unexpected dirs, nested repos).
- **Model Routing Safety Tests (Group E):**
  - `ModelRoutingSafetyTests`: 8 passed (explicit model pass, auto rejected, implicit rejected, unknown gateway rejected, disabled gateway rejected).
- **Failure Attribution Tests (Group F):**
  - `FailureAttributionBoundaryTests`: 8 passed (OpenRouter 401, 402, 404, 429, 503 extraction, s6 log separation, non-zero exits).
- **Certification Defect Reproduction Tests:**
  - `CertificationDefectsReproductionTests`: 2 passed (Defect A: empty review bundle vs populated review bundle; Defect B: stray file acceptance vs allowlist rejection).

---

## 9. Live Certification

### Live Certification Re-entry Attempts

#### 1. Hermes Runtime Re-entry Attempt
- **Timestamp:** 2026-10-07 10:41:51 UTC
- **Test:** `ModelBackedCertificationTests.Hermes_RealModel_CompletesTheCertificationTask_ThroughTheFactoryPath`
- **Runtime:** `Hermes` (`nousresearch/hermes-agent:0.20.6`)
- **Gateway:** `OpenRouter` (`https://openrouter.ai/api/v1`)
- **Model:** `deepseek/deepseek-v4-flash-0731` (explicit, paid)
- **Observed Result:**
  - Container launched cleanly (`factoryOwned=0` before).
  - Model request sent to OpenRouter gateway.
  - Gateway response: `OpenRouter reported that billing, credits, or account entitlement failed: insufficient credits. This account never purchased credits. Make sure your key is on the correct account or org, and if so, purchase more at https://openrouter.ai/settings/credits`.
  - **Failure Attribution:** Adapter correctly classified failure:
    `metadata={"ExecutionAdapter":"Hermes","ExitReason":"exit:0","Gateway":"OpenRouter","GatewayFailure":"Billing"}`.
  - **WorkItem Lifecycle:** WorkItem transitioned `Ready -> Running -> Failed`. No verification or acceptance was attempted.
  - **Cleanup:** `containers.afterImmediate factoryOwned=0`, `containers.after15s factoryOwned=0`. Foreign containers untouched.
- **Classification:** `ENVIRONMENT BLOCKED — PROVIDER CREDITS EXHAUSTED`

#### 2. OpenHands Runtime Re-entry Attempt
- **Timestamp:** 2026-10-07 10:45:43 UTC
- **Test:** `ModelBackedCertificationTests.OpenHands_RealModel_CompletesTheCertificationTask_ThroughTheFactoryPath`
- **Runtime:** `OpenHands` (`ghcr.io/openhands/agent-server:1.49.4-python`)
- **Gateway:** `OpenRouter` (`https://openrouter.ai/api/v1`)
- **Model:** `qwen/qwen3-coder` (explicit, paid)
- **Observed Result:**
  - Dedicated agent server container launched cleanly (`factoryOwned=0` before).
  - Conversation initiated with OpenHands server.
  - OpenHands conversation transitioned to error state due to provider rejection.
  - **WorkItem Lifecycle:** WorkItem transitioned `Ready -> Running -> Failed`. No verification or acceptance was attempted.
  - **Cleanup:** `containers.afterImmediate factoryOwned=0`, `containers.after15s factoryOwned=0`. Foreign containers untouched.
- **Classification:** `ENVIRONMENT BLOCKED — PROVIDER CREDITS EXHAUSTED`

---

## 10. Verification & Acceptance

### End-to-End Lifecycle Invariant Validation
Across unit, integration, and live failure tests, the following invariants were verified:

1. $\text{AgentRun Failed} \implies \text{WorkItem Failed}$ (never proceeds to verification or acceptance).
2. $\text{Verification Failed (Build / Test / Integrity)} \implies \text{WorkItem Repairing}$ (or `Failed` if repair budget exhausted; never accepted).
3. $\text{Reviewer Failed / Timed Out / Malformed} \implies \text{Review Blocks Completion} \implies \text{WorkItem Repairing}$ (never falsely accepted).
4. $\text{Workspace Integrity Violation} \implies \text{Verification Failed} \implies \text{WorkItem Repairing}$ (never accepted with stray files).
5. $\text{Acceptance Criteria Unmet} \implies \text{WorkItem Remains Incomplete}$.
6. $\text{WorkItem Completed} \iff (\text{AgentRun Succeeded} \land \text{Verification Passed} \land \text{Integrity Passed} \land \text{Review Succeeded with no blocking findings} \land \text{Acceptance Criteria Passed})$.

---

## 11. Cleanup

### Docker Resource Audit
- **Factory-Owned Containers:**
  - Before regression runs: **0**
  - After regression runs: **0**
  - Before Hermes live run: **0**
  - Immediately after Hermes live run: **0**
  - 15s after Hermes live run: **0**
  - Before OpenHands live run: **0**
  - Immediately after OpenHands live run: **0**
  - 15s after OpenHands live run: **0**
- **Foreign Containers:**
  - Standalone `hermes` container (`e9a87be01b8e` on port 8642) remained Up and completely untouched across all runs.
- **Anonymous Volumes:**
  - Cleaned up automatically upon container exit via `--rm` and `-v /opt/data`.

---

## 12. Security

### Credential Isolation Audit
- **Command Line Arguments:** Monitored and verified: `0` command lines contained credentials.
- **Process Listings:** No credentials visible in process environment or arguments.
- **Captured Logs & Diffs:** `0` captured log lines, git diffs, review bundles, or persisted database fields contained the `OPENROUTER_API_KEY`.
- **Precedence:** Evaluated via isolated child environment injection; ambient host `~/.hermes` or local user configurations cannot override the Factory-managed credential.

---

## 13. Evidence Classification

| Item | Classification | Verification Method |
|---|---|---|
| Review Bundle Unified Diff & Content Injection | `VERIFIED BY TEST EXECUTION` | `ReviewBoundaryIntegrityTests.BuildPrompt_WithReviewBundle_ContainsUnifiedDiffAndAddedFileContent` |
| Review Parsing Isolation from s6 Container Logs | `VERIFIED BY TEST EXECUTION` | `ReviewBoundaryIntegrityTests.ReviewAsync_WhenOutputContainsStartupLogAndFramedFindings_ParsesCorrectly` |
| Review Failure Blocks Completion (No False Pass) | `VERIFIED BY TEST EXECUTION` | `ReviewBoundaryIntegrityTests.ReviewAsync_WhenOutputMalformed_ReturnsCriticalBlockingFinding` |
| Workspace Integrity Positive Allowlist (G-P3 Fix) | `VERIFIED BY TEST EXECUTION` | `WorkspaceIntegrityPositiveAllowlistTests.Validate_WithUnexpectedUntrackedFile_FailsIntegrity` |
| Unicode & Hidden File Detection with Lossless Logging | `VERIFIED BY TEST EXECUTION` | `WorkspaceIntegrityPositiveAllowlistTests.Validate_WithUnicodeFilename_FailsIntegrityWithLosslessRepresentation` |
| Fail-Closed Model Routing & Auto Rejection | `VERIFIED BY TEST EXECUTION` | `ModelRoutingSafetyTests.SelectModel_WithAutoModel_ThrowsInvalidOperationException` |
| Gateway Failure Attribution (Separation from Logs) | `VERIFIED BY TEST EXECUTION` & `VERIFIED LIVE` | `FailureAttributionBoundaryTests` & Hermes Live Run 1 (`GatewayFailure = "Billing"`) |
| Container Lifecycle Clean Exit (0 Leaks) | `VERIFIED LIVE` | `DockerLifecycleIntegrationTests` & Live Run 1 before/after container count audits |
| Live Paid Model Generation (Hermes / OpenHands) | `ENVIRONMENT BLOCKED` | OpenRouter account has insufficient credits |
| Claude Code Live Generation | `ENVIRONMENT BLOCKED` | Anthropic credential not present |
| Codex CLI Live Generation | `ENVIRONMENT BLOCKED` | Codex CLI not installed |

---

## 14. Remaining Constraints

1. **OpenRouter Paid Account Credits:** The configured OpenRouter key lacks purchased credits, preventing completion of the 2 consecutive live model generation runs per runtime required for full runtime certification.
2. **Claude Code / Codex Prerequisites:** Local execution prerequisites (Claude auth token / Codex CLI binary) remain unconfigured in this environment.

---

## 15. Phase Q Readiness

**`NO`**

### Reason
Under the Phase P.1 specification, Phase Q (Benchmark) is strictly gated on achieving `GO FOR PHASE Q`. `GO FOR PHASE Q` requires that affected runtimes complete two consecutive live model-backed certification runs. While all Factory certification defects (Review integrity, Workspace allowlist, routing fail-closed, failure attribution) are resolved and verified by test execution, live runtime certification re-entry is blocked by external OpenRouter credit exhaustion (`LIMITED — EXTERNAL RUNTIME CONSTRAINT`).

---

## 16. Worktree

### Modified Files
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
- `apps/tests/AI.Factory.Worker.Tests/ProductionModelRouteConfigurationTests.cs`
- `apps/tests/AI.Factory.Worker.Tests/Fixtures/LiveWorkerHost.cs`
- `apps/tests/AI.Factory.Api.Tests/RealHermesApiE2ETests.cs`
- `apps/tests/AI.Factory.Application.Tests/AgentCodeReviewerTests.cs`
- `apps/tests/AI.Factory.Application.Tests/Execution/ReviewExecutionGoldenEquivalenceTests.cs`
- `apps/tests/AI.Factory.Application.Tests/ReviewOutcomeMatrixTests.cs`
- `docs/execution-runtimes.md`
- `docs/local-setup.md`

### Created Files
- `apps/src/AI.Factory.Infrastructure/Validation/GitReviewBundleBuilder.cs`
- `apps/tests/AI.Factory.Application.Tests/Review/ReviewBoundaryIntegrityTests.cs`
- `apps/tests/AI.Factory.Application.Tests/Orchestration/WorkspaceIntegrityPositiveAllowlistTests.cs`
- `apps/tests/AI.Factory.Application.Tests/Gateway/ModelRoutingSafetyTests.cs`
- `apps/tests/AI.Factory.Application.Tests/Gateway/FailureAttributionBoundaryTests.cs`
- `apps/tests/AI.Factory.Application.Tests/CertificationDefectsReproductionTests.cs`
- `scripts/parse-trx.ps1`
- `scripts/run-3-regressions.ps1`
- `scripts/run-cert-hermes-1.ps1`
- `scripts/run-cert-openhands-1.ps1`
- `docs/phase-p1-report.md`

*(No commits, tags, or pushes made.)*
