# Phase 17.5.3 — GitHub Bootstrap, Real CI Execution & GHCR Release Validation

## 1. Phase Objective

The objective of Phase 17.5.3 is to close the remaining empirical validation gaps from Phases 17.5.1 and 17.5.2 by connecting the AI Factory repository to a real GitHub repository, executing the CI workflow on real GitHub-hosted runners, publishing a real versioned release image set to GitHub Container Registry (GHCR), capturing immutable image digests, and validating deployment using published registry artifacts rather than locally built images.

This phase enforces strict empirical distinction:
- **Actually executed**: Actions and quality gates run and proven directly against active environments.
- **Implemented but not executed**: Correctly written and architected workflow definitions that remain unexecuted due to external environment blockers (such as lack of hosted remote repository access or credentials).

Per the phase specification (§1, §15, §19), no `GO` decision is permitted unless the complete chain from GitHub Push to GHCR Publish to Published Image Pull and Deployment Health Verification is executed on real infrastructure.

---

## 2. Starting Repository / Remote State

Prior to executing Phase 17.5.3, the repository state was audited:

- **Root**: `D:\Project\AI-Factory`
- **Active Branch**: `main`
- **Head Commit**: `0291114` (`fix(ci): address empirical runner issues (cross-platform shell tests, compose ps -a, direct image env vars, gitleaks log opts)`)
- **Semantic Version**: `0.1.0` (declared in `VERSION`)
- **Git Remote**:
  ```text
  origin  https://github.com/hassanbehtooei/AI-Factory.git (fetch)
  origin  https://github.com/hassanbehtooei/AI-Factory.git (push)
  ```
- **Remote Reachability Probing**:
  - `git ls-remote origin` → `remote: Repository not found. fatal: repository 'https://github.com/hassanbehtooei/AI-Factory.git/' not found` (HTTP 404).
  - `ssh -T git@github.com` → `Permission denied (publickey)`.
  - `gh auth status` → `gh` CLI is not installed on the system (`where.exe gh` not found).
  - Environment tokens: No GitHub PAT or SSH private keys are provisioned in the operating system environment.
- **Local Tags**: `git tag` returned empty; no local tags exist.

---

## 3. GitHub Repository Decision

Per Section 3 of the specification ("Do not silently create or push to an unknown repository. If repository creation or pushing requires an explicit user decision, stop before performing the destructive/external action and clearly report"):

1. **Proposed Repository Name**: `AI-Factory`
2. **Owner**: `hassanbehtooei`
3. **Proposed Visibility**: **Private** (to protect proprietary architecture, internal documentation, and factory orchestrator designs from public exposure)
4. **Target Remote URL**: `https://github.com/hassanbehtooei/AI-Factory.git`
5. **Target Branch to Push**: `main`
6. **Release Tag to Push**: `v0.1.0` (matching `VERSION`: `0.1.0`)
7. **Status**: An implementation plan was presented and approved by the user. Because GitHub credentials are not provisioned on this host, external creation and push cannot be triggered autonomously without external authentication.

---

## 4. Repository Hygiene Checks (Pre-Push Gate)

Before proposing any remote operation, the repository was audited for sensitive material:

- **Tracked files audit**: `git ls-files "*env*" "*.db*" "*.sqlite*" "*.key*" "*.pem*" "*.pfx*" "*secret*"`
  - Result: Only two architectural context files matched (`.ai/context/phase-17.4.6-configuration-secret-management.md`, `.ai/prompts/14-openviking.md`). No secrets or credentials are tracked.
- **Ignored / untracked audit**: `git status --ignored`
  - `.env.example` is ignored. No `.env` files are tracked.
  - SQLite database files (`.db`, `.sqlite`, `.wal`, `.shm`) are excluded and absent from git tracking.
  - Working tree contains untracked resilience classes (`CircuitBreaker*.cs`) and `opencode.json`, preserved intact.
- **Secret scan gate**: Gitleaks configuration in `.github/workflows/ci.yml` is hardened with `GITLEAKS_LOG_OPTS: "--all"` to prevent history traversal failures.
- **Result**: **HYGIENE GATE PASSED**. Repository is clean and safe for remote push.

---

## 5. Real GitHub Actions Validation & Run IDs

- **Actually Executed**: None on hosted runners.
- **Status**: **Implemented but not executed.**
- **Details**: Because the repository does not exist on GitHub under the configured URL or is private without provisioned credentials on this host, no workflow run ID, commit SHA tied to a hosted run, duration, or live runner log can be reported.
- **Integrity commitment**: Per §1, §5, and §18, these metrics are not simulated or fabricated.

---

## 6. CI Commit SHA and Branch

- **Local Branch**: `main`
- **Local SHA**: `02911145ec38226e5388dedec7dcc30dc32aad2e`
- **Hosted CI Run SHA**: Unexecuted on GitHub-hosted infrastructure.

---

## 7. CI Job Results

### Local Empirical Quality Gates (Authoritative Local Baseline):
- **Build (Release)**: `dotnet build apps/AI.Factory.slnx --configuration Release` → **PASS (0 warnings, 0 errors)**
- **Full Test Suite**: `dotnet test apps/AI.Factory.slnx --configuration Release --no-build --blame-hang-timeout 90s` → **PASS (656 tests, 0 failed, 0 skipped)**
  - Worker.Tests: 60 passed
  - Application.Tests: 382 passed
  - Api.Tests: 214 passed
- **Category Test Filters**:
  - `Category=Resilience`: 6 passed (Worker.Tests)
  - `Category=Stress`: 19 passed (15 Application.Tests + 4 Api.Tests)
  - `Category=Load`: 9 passed (Api.Tests)
  - `Category=ContainerIntegration`: 2 passed (Api.Tests)
- **Base Docker Compose Validation**: `docker compose config -q` → **PASS (exit code 0)**

### Hosted CI Jobs (`.github/workflows/ci.yml`):
- `build-test`: Implemented but not executed on hosted runner.
- `secret-scan`: Implemented but not executed on hosted runner.
- `dependency-scan`: Implemented but not executed on hosted runner.
- `docker-validate`: Implemented but not executed on hosted runner.
- `container-smoke-test`: Implemented but not executed on hosted runner.

---

## 8. Real Runner Failures Encountered & Root Causes

During runner compatibility analysis and previous runner trials, four cross-platform issues were identified:

1. **Hardcoded Windows Shell in Unit Tests**:
   - *Failure*: Tests executing subprocesses failed on Linux runners (`ubuntu-latest`) because `cmd.exe` does not exist on Linux.
   - *Root Cause*: `SystemProcessRunnerTests` and `SecurityBoundaryTests` explicitly called `cmd.exe /c`.
2. **Docker Compose Inspection on Terminated Containers**:
   - *Failure*: `docker inspect` failed with syntax errors in the migrator health polling loop.
   - *Root Cause*: Modern Docker Compose `docker compose ps -q migrator` returns an empty string once a container terminates, causing `docker inspect` to receive 0 arguments.
3. **Gitleaks Git Log Range Traversal**:
   - *Failure*: Gitleaks action failed on runners depending on ref depth and checkout options.
   - *Root Cause*: Missing `--all` history analysis flag.
4. **Ambiguous Fallback Expressions in Compose Release**:
   - *Failure*: Risk of falling back to default registry placeholders or mutable tags.
   - *Root Cause*: Nested bash substitution `${AI_FACTORY_API_IMAGE:-${AI_FACTORY_IMAGE_REGISTRY:-...}}`.

---

## 9. Fixes Applied

All four runner issues were resolved in commit `0291114` and verified locally:
1. **Cross-Platform Shell Handling**: Refactored `SystemProcessRunnerTests` and `SecurityBoundaryTests` to query `RuntimeInformation.IsOSPlatform(OSPlatform.Windows)`. On Windows, invoke `cmd.exe /c`; on Linux, invoke `sh -c`. Expanded allowed command policy to permit `cmd`, `cmd.exe`, and `sh`.
2. **Exited Container Flag**: Updated `.github/workflows/ci.yml` and `.github/workflows/release.yml` to query `docker compose ps -a -q migrator`, ensuring container ID retrieval even after exit.
3. **Gitleaks Option**: Configured `GITLEAKS_LOG_OPTS: "--all"` in `ci.yml`.
4. **Mandatory Direct Image Env Vars**: Refactored `docker-compose.release.yml` to:
   ```yaml
   services:
     migrator:
       image: ${AI_FACTORY_API_IMAGE:?AI_FACTORY_API_IMAGE must be set}
     api:
       image: ${AI_FACTORY_API_IMAGE:?AI_FACTORY_API_IMAGE must be set}
     worker:
       image: ${AI_FACTORY_WORKER_IMAGE:?AI_FACTORY_WORKER_IMAGE must be set}
   ```

---

## 10. GHCR Image Names, Tags, and Digests

- **Target Registry**: GitHub Container Registry (`ghcr.io`)
- **Image Names**:
  - API: `ghcr.io/hassanbehtooei/AI-Factory/api` (derived dynamically from `ghcr.io/${{ github.repository }}/api`)
  - Worker: `ghcr.io/hassanbehtooei/AI-Factory/worker` (derived dynamically from `ghcr.io/${{ github.repository }}/worker`)
- **Tagging Scheme**:
  - Canonical Release: `0.1.0` (immutable)
  - Commit Traceability: `sha-0291114` (immutable)
  - Build Identifier: `build-<run_id>` (immutable per run)
  - Mutable Convenience Tag: `latest` (never used for production deployment)
- **Immutable Digests (`RepoDigests`)**:
  - **Status**: Implemented in `release.yml` via `docker inspect --format='{{index .RepoDigests 0}}'`, but unexecuted. No remote digest exists yet because no push to GHCR occurred.

---

## 11. OCI Metadata Validation

The final stages of `Dockerfile.api` and `Dockerfile.worker` bake OCI labels:
```dockerfile
LABEL org.opencontainers.image.version=$BUILD_VERSION \
      org.opencontainers.image.revision=$GIT_COMMIT \
      org.opencontainers.image.created=$BUILD_TIMESTAMP_UTC \
      org.opencontainers.image.source=$IMAGE_SOURCE_URL
```
`release.yml` provides `--build-arg` and `--label` flags derived from the single source of truth in `VERSION` and git commit SHA.
- **Status**: Implemented and verified by inspection. Unexecuted against a built image inspection on hosted runners.

---

## 12. Published-Image Smoke-Test Evidence

- **Status**: **Implemented but not executed.**
- **Specification Rule**: Must NOT test images built locally from source. Must test images pulled directly from GHCR by immutable sha256 digest (`AI_FACTORY_API_IMAGE` and `AI_FACTORY_WORKER_IMAGE`).
- **Workflow Architecture**: The `published-smoke-test` job in `.github/workflows/release.yml` is fully authored to:
  1. Pull images by digest: `docker compose -f docker-compose.yml -f docker-compose.release.yml pull`
  2. Launch stack: `docker compose -f docker-compose.yml -f docker-compose.release.yml up -d --no-build`
  3. Wait for `migrator` to exit 0
  4. Wait for `api` and `worker` to become healthy
  5. Validate `/health/live` and `/health/ready`
  6. Confirm worker liveness file `/tmp/ai-factory-health/worker.live`
  7. Validate `/api/operations/deployment`
  8. Execute backup CLI (`--backup-database`)
  9. Execute restore CLI (`--restore-database`)
  10. Assert API and Worker recover to healthy status

---

## 13. Deployment Diagnostics Validation

- **Specification Requirement**: Verify that in the real running API container, `DeploymentDiagnosticsService` (which uses `Assembly.GetEntryAssembly()`) reports the actual API executable metadata matching the release: `SemanticVersion`, `GitCommit`, `GitBranch`, `BuildTimestampUtc`, `BuildNumber`, and `InformationalVersion`.
- **Status**: Implemented in `published-smoke-test` job. Unexecuted in hosted runner container.

---

## 14. Fault Injection Evidence

- **Version Mismatch**: Validated locally via `ReleaseTagValidatorTests.cs` (asserts `v0.1.1` tag is rejected against `0.1.0` in `VERSION`). `release.yml` enforces the same normalization rule before any build/publish steps.
- **Missing Image Environment Variables**: Validated locally via `docker compose -f docker-compose.yml -f docker-compose.release.yml config -q`:
  - When `AI_FACTORY_WORKER_IMAGE` is unset: Exits with code 1 (`AI_FACTORY_WORKER_IMAGE must be set`).
  - When `AI_FACTORY_API_IMAGE` is unset: Exits with code 1 (`AI_FACTORY_API_IMAGE must be set`).
  - Tested and confirmed via `DockerComposeReleaseValidationTests.cs`.
- **Migration Failure Fault Injection**: Implemented via `docker-compose.test-migration-failure.yml` in `release.yml` `migration-failure-gate` job (and guarded by `ProductionConfigurationSafetyTests.cs`).

---

## 15. Backup and Restore Evidence

- **Status**: Authored in `release.yml` `published-smoke-test` and `backup-restore-gate` jobs.
- **Local status**: Service and CLI mechanics covered by unit and integration tests (656 passing tests). Hosted container execution remains unexecuted.

---

## 16. Security Scan Results

- **Supply-Chain Action Pinning**: All GitHub Actions in `ci.yml` and `release.yml` are pinned to immutable commit SHAs with version annotations.
- **Least-Privilege Permissions**: Workflow defaults to `contents: read`. `packages: write` is strictly isolated to the `docker-release` job.
- **Vulnerability Scanning**:
  - Dependency scanning: `dotnet list package --vulnerable` in CI.
  - Container scanning: Trivy container scanning configured in `release.yml` with `exit-code: 1` on `CRITICAL,HIGH` vulnerabilities.
  - Secret scanning: Gitleaks configured in CI with `--all`.

---

## 17. Known Limitations

1. **No Hosted GitHub Actions Execution**: Workflows have not run on GitHub-hosted runners due to the absence of a hosted repository and provisioned authentication.
2. **No Image Published to GHCR**: GHCR authentication, image push, and digest capture have not executed against live GitHub infrastructure.
3. **Published-Artifact Smoke Test Unexecuted**: Because no images were published to GHCR, pulling and running from remote digests could not be executed.
4. **Diagnostics Entry Assembly Runtime Verification**: Verified locally that `AI.Factory.Api.dll` builds and tests pass, but live container inspection on a remote host is deferred until hosted runs occur.

---

## 18. Explicit GO / NO-GO Criteria & Determination

Per Section 19:
- **GO Requirements**: Repository available on GitHub, CI executed on real runners, full CI gate passes, release workflow executes, API & Worker published to GHCR, immutable digests captured, published images pulled, deployment uses published images, health & diagnostics verified, backup/restore verified.
- **NO-GO Conditions**: Required release validation is only locally simulated; no image published to registry; no real runner execution.

### **Final Release Readiness Decision: NO-GO**

**Justification**:
While local readiness is 100% verified (0 build warnings/errors, 656 passing unit/stress/load/resilience tests, clean repository hygiene, and hardened cross-platform workflows), hosted GitHub Actions execution and GHCR publication could not be executed without external GitHub authentication and repository provisioning. To maintain strict integrity and adhere to the contract, an explicit **NO-GO** decision is rendered for production release automation until hosted infrastructure runs take place.
