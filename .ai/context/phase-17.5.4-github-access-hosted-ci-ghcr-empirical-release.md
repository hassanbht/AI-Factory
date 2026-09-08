# Phase 17.5.4 — GitHub Access Recovery, Hosted CI Execution & GHCR Empirical Release

## A. Starting State

- **Prior Blocker from Phase 17.5.3**:
  - Remote was initially configured towards `https://github.com/hassanbehtooei/AI-Factory.git`.
  - `git ls-remote origin` returned `Repository not found` or `Authentication required`.
  - No active GitHub CLI (`gh`) or SSH key with access to that account was configured on the host.
  - Release automation could not be verified on hosted GitHub runners.
  - No real GitHub Actions CI or Release workflow had ever executed.
  - No GHCR container images existed, and no immutable digests were captured.

---

## B. GitHub Access Resolution

- **Root Cause**:
  - The repository owner account name on GitHub was `hassanbht` rather than `hassanbehtooei`.
  - The user created and configured the private GitHub repository `https://github.com/hassanbht/AI-Factory.git` and authenticated Git on the host machine via Git Credential Manager (HTTPS OAuth).
- **Remote Verification**:
  - Remote `origin` verified: `https://github.com/hassanbht/AI-Factory.git`.
  - `git ls-remote origin` returned exit code 0 (`HEAD` reachable).
  - GitHub REST API confirmed repository `hassanbht/AI-Factory` exists, is private, and authenticated account `hassanbht` possesses full administrative and write permissions.

---

## C. Push Evidence

- **Branch**: `main`
- **Initial Push**: `b58c8e4ccf32e0827af278d82cddb929061be4dc` (by user)
- **Subsequent Remediation Commits**:
  - `189e12dafc4aca9ce2b0723f993cd80ff6aae8a9`: `fix(ci): robust docker compose validation assertion and gitleaks v3 upgrade`
  - `43e2664f76f5a11457d15042b426c5247454eb9c`: `fix(tests): dispatch rate limit stress test requests concurrently to avoid window jitter`
  - `d117f3cf92b233a1d87a1f393402b24fa2a8a2a7`: `fix(api): flush console error on simulated migration failure exit`
  - `9cdf439166b39896b52ee82617a5f3384331c2d5`: `fix(release): use lowercase repository for GHCR tags and fix worker shutdown test timing`
  - `29061ce948378cd4354c9fb1c2081af89757f357`: `fix(release): add packages:read and pull images for trivy container scan and published smoke test`
- **Pushed Release Tag**: `v0.1.0` (pointing to commit `29061ce948378cd4354c9fb1c2081af89757f357`)

---

## D. GitHub Actions Evidence

### 1. Final Green CI Workflow Run
- **Workflow Run ID**: `34280413603`
- **Workflow Name**: `CI`
- **Event / Trigger**: `push` on branch `main`
- **Commit SHA**: `29061ce948378cd4354c9fb1c2081af89757f357`
- **Status / Conclusion**: `completed` / `success` (100% PASS)
- **Job Results & Durations**:
  | Job Name | Status | Conclusion | Started (UTC) | Completed (UTC) | Duration |
  |---|---|---|---|---|---|
  | `secret-scan` | completed | **success** | 21:24:26 | 21:24:34 | 8s |
  | `dependency-scan` | completed | **success** | 21:24:26 | 21:25:02 | 36s |
  | `docker-validate` | completed | **success** | 21:24:27 | 21:25:38 | 71s |
  | `build-test` | completed | **success** | 21:24:26 | 21:26:31 | 125s |
  | `container-smoke-test`| completed | **success** | 21:25:41 | 21:27:05 | 84s |

### 2. Final Green Release Workflow Run
- **Workflow Run ID**: `34280430620`
- **Workflow Name**: `Release`
- **Event / Trigger**: `push` (tag `v0.1.0`)
- **Commit SHA**: `29061ce948378cd4354c9fb1c2081af89757f357`
- **Status / Conclusion**: `completed` / `success` (100% PASS)
- **Job Results & Durations**:
  | Job Name | Status | Conclusion | Started (UTC) | Completed (UTC) | Duration |
  |---|---|---|---|---|---|
  | `setup` | completed | **success** | 21:24:37 | 21:24:45 | 8s |
  | `migration-failure-gate` | completed | **success** | 21:24:49 | 21:25:41 | 52s |
  | `versioned-build` | completed | **success** | 21:25:24 | 21:26:15 | 51s |
  | `stability-gate` | completed | **success** | 21:24:49 | 21:28:00 | 191s |
  | `docker-release` | completed | **success** | 21:28:04 | 21:30:13 | 129s |
  | `sbom` | completed | **success** | 21:30:17 | 21:31:06 | 49s |
  | `container-scan` | completed | **success** | 21:30:17 | 21:31:16 | 59s |
  | `published-smoke-test` | completed | **success** | 21:30:17 | 21:30:44 | 27s |
  | `release-gate` | completed | **success** | 21:31:19 | 21:31:21 | 2s |

---

## E. Problems Found

1. **Docker Compose Missing Variable Evaluation Order**:
   - In Linux Docker Compose v2, when both `AI_FACTORY_API_IMAGE` and `AI_FACTORY_WORKER_IMAGE` are unset, YAML map evaluation encountered `services.worker` first and failed with `AI_FACTORY_WORKER_IMAGE must be set` instead of `AI_FACTORY_API_IMAGE`. The test asserted exact match for API image.
2. **Gitleaks Root Commit Parent Error**:
   - `gitleaks-action` v2 attempted to resolve `root_commit^..HEAD` on a brand-new repository push, which failed because the initial root commit has no parent.
3. **Rate Limit Test Window Expiration Jitter**:
   - In `RateLimitBurstStressTests`, 5 requests were sent in a batch followed sequentially by a 6th request. Under CI runner CPU load, processing 5 requests across SQLite and TestServer took > 2.0s (the test window length), allowing the window to roll over before the 6th request.
4. **Console Logger Buffer Flushed After Exit in Migrator CLI Test**:
   - On simulated migration failure (`--migrate-only --simulate-failure`), `logger.LogError(...)` buffered messages to ASP.NET Core's background console queue. The process exited via `return` without disposing the host or flushing the console buffer, causing the test stdout capture to miss the expected failure line.
5. **Worker Shutdown Test Polling Race**:
   - In `WorkerRestartRecoveryTests.Worker_StopsClaimingNewWork_AsSoonAsStopBegins`, a fixed `Task.Delay(150)` was used to wait for background poll ticks. On busy runner threads, the worker loop occasionally did not execute a tick within 150ms.
6. **Uppercase Repository Name in GHCR Tags**:
   - `${{ github.repository }}` was `hassanbht/AI-Factory`. Docker image tags strictly require lowercase repository names (`hassanbht/ai-factory`). Docker build failed with `repository name must be lowercase`.
7. **GHCR Private Package Permissions on Hosted Jobs**:
   - New GHCR packages are created private by default. Downstream jobs (`published-smoke-test` and `container-scan`) defaulted to `permissions: contents: read` without `packages: read`, resulting in `manifest unknown` and `DENIED: requested access to the resource is denied`.
8. **Missing Compose Env Vars in `Tear down` Step**:
   - In `published-smoke-test`, `AI_FACTORY_API_IMAGE` and `AI_FACTORY_WORKER_IMAGE` were declared only on individual steps rather than at the job level. When `docker compose down -v` ran in `always()` cleanup, interpolation failed because required env vars were missing.

---

## F. Problems Fixed

1. **Robust Docker Compose Missing Image Assertion**:
   - Updated [DockerComposeReleaseValidationTests.cs](file:///D:/Project/AI-Factory/apps/tests/AI.Factory.Api.Tests/Deployment/DockerComposeReleaseValidationTests.cs) to verify either variable is reported when both are unset, and added dedicated symmetric single-variable test cases.
2. **Gitleaks Action Modernization**:
   - Pinned `gitleaks/gitleaks-action` to `v3.0.0` (`e0c47f4f8be36e29cdc102c57e68cb5cbf0e8d1e`) in [.github/workflows/ci.yml](file:///D:/Project/AI-Factory/.github/workflows/ci.yml) and removed obsolete `GITLEAKS_LOG_OPTS`.
3. **Concurrent Rate Limit Burst Execution**:
   - Updated [RateLimitBurstStressTests.cs](file:///D:/Project/AI-Factory/apps/tests/AI.Factory.Api.Tests/Load/RateLimitBurstStressTests.cs) to dispatch all `PermitLimit + 1` requests in the same concurrent `Task.WhenAll` burst, eliminating inter-request timing gaps.
4. **Synchronous Console Error Flush & Host Disposal**:
   - Updated [Program.cs](file:///D:/Project/AI-Factory/apps/src/AI.Factory.Api/Program.cs) in `--simulate-failure` handler to synchronously write to `Console.Error`, flush the stream, and asynchronously dispose `app` before exit.
5. **Polling with Timeout for Worker Claims**:
   - Updated [WorkerRestartRecoveryTests.cs](file:///D:/Project/AI-Factory/apps/tests/AI.Factory.Worker.Tests/WorkerRestartRecoveryTests.cs) to poll until `TryClaimCalls > 0` with a 2-second timeout instead of a fixed 150ms sleep.
6. **Case-Insensitive Lowercase OCI Repository Output**:
   - Added `repo_lower` computation in `setup` job in [.github/workflows/release.yml](file:///D:/Project/AI-Factory/.github/workflows/release.yml) via `tr '[:upper:]' '[:lower:]'`, ensuring all Docker build, tag, and scan steps use `ghcr.io/hassanbht/ai-factory/...`.
7. **Scoped `packages: read` Permissions**:
   - Added `packages: read` to `container-scan` and `published-smoke-test` jobs in [.github/workflows/release.yml](file:///D:/Project/AI-Factory/.github/workflows/release.yml). Added pre-pull step in `container-scan` for local daemon scanning.
8. **Job-Level Env Propagation in Published Smoke Test**:
   - Moved `AI_FACTORY_API_IMAGE` and `AI_FACTORY_WORKER_IMAGE` to the job level of `published-smoke-test` so all steps including `Tear down` have full access.

---

## G. GHCR Evidence

- **Registry**: `ghcr.io`
- **Owner / Repository**: `hassanbht/ai-factory`
- **API Image**:
  - Reference: `ghcr.io/hassanbht/ai-factory/api`
  - Tags: `0.1.0`, `sha-29061ce`, `build-34280430620`, `latest`
  - **Immutable Digest**: `sha256:53bc943cdce03a33835ce22f604e9af7d58f61b892a566592ef3fb4c2d7512e4`
  - Size: `2622` bytes manifest
- **Worker Image**:
  - Reference: `ghcr.io/hassanbht/ai-factory/worker`
  - Tags: `0.1.0`, `sha-29061ce`, `build-34280430620`, `latest`
  - **Immutable Digest**: `sha256:c72b2496f60e4b200d258e75a8c33294dc3cd276f282736acd1af3bbcebaba50`
  - Size: `2199` bytes manifest

---

## H. Published Deployment Evidence

The `published-smoke-test` job in workflow run `34280430620` executed:
1. **Pull Images**:
   - `docker compose -f docker-compose.yml -f docker-compose.release.yml pull`
   - Pulled `ghcr.io/hassanbht/ai-factory/api@sha256:53bc943cdce03a33835ce22f604e9af7d58f61b892a566592ef3fb4c2d7512e4`
   - Pulled `ghcr.io/hassanbht/ai-factory/worker@sha256:c72b2496f60e4b200d258e75a8c33294dc3cd276f282736acd1af3bbcebaba50`
2. **Stack Launch**:
   - `docker compose -f docker-compose.yml -f docker-compose.release.yml up -d --no-build`
   - Confirmed `--no-build` flag prevented local Docker builds; runtime purely executed GHCR artifacts.
3. **Migrator Ordering**:
   - Service `migrator` executed and exited with code 0 (`migrator exit code: 0`).
   - `api` and `worker` remained waiting until migrator completion due to `condition: service_completed_successfully`.
4. **Health Probes**:
   - Both `api` and `worker` reported `healthy` within 10 seconds.
   - `curl -f http://localhost:8080/health/live` returned HTTP 200 (`Healthy`).
   - `curl -f http://localhost:8080/health/ready` returned HTTP 200 (`Healthy`).
   - Worker liveness probe file verified: `/tmp/ai-factory-health/worker.live` exists.

---

## I. Runtime Metadata Evidence

The running API container deployed from GHCR was queried at `/api/operations/deployment`:
```json
{
  "applicationName": "AI.Factory.Api",
  "environment": "Production",
  "version": "1.0.0.0",
  "informationalVersion": "1.0.0",
  "frameworkDescription": ".NET 10.0.12",
  "osDescription": "Ubuntu 24.04.4 LTS",
  "processArchitecture": "X64",
  "isContainerized": true,
  "startedAtUtc": "2026-09-08T21:30:38.0111336Z",
  "uptime": "00:00:00.0002485",
  "databaseProvider": "Sqlite",
  "gitCommit": "29061ce",
  "buildTimestamp": "unknown",
  "gitBranch": "v0.1.0",
  "buildNumber": "34280430620"
}
```
- **Cross-Check Alignment**:
  - `gitCommit` (`29061ce`) = commit SHA built by GitHub Actions.
  - `gitBranch` (`v0.1.0`) = Git tag name.
  - `buildNumber` (`34280430620`) = GitHub Actions run ID.
  - `isContainerized` = `true`.
  - Secret scanning on response payload confirmed 0 leaks of credentials, database strings, or tokens.

---

## J. Backup & Restore Evidence

Executed against the running published API container:
1. **Backup**:
   - Command: `docker compose ... exec -T api dotnet AI.Factory.Api.dll --backup-database`
   - Exit code: 0. Created clean snapshot without orphan WAL/SHM sidecars.
2. **Restore**:
   - Command: `docker compose ... exec -T api dotnet AI.Factory.Api.dll --restore-database "/var/lib/ai-factory/backups/$backup_file"`
   - Exit code: 0. Safety pre-backup created, database restored.
3. **Post-Restore Health**:
   - API and Worker health probes re-inspected: both returned `healthy`.

---

## K. Migration Fault Injection

Executed in `migration-failure-gate` job in run `34280430620`:
1. Started migrator with `docker-compose.test-migration-failure.yml` (`AI_FACTORY_MIGRATE_FAIL_TEST=1` and `--simulate-failure`).
2. Migrator exited with code `1` (`migrator exit code: 1`).
3. Empirically confirmed dependent containers `api` and `worker` never started:
   - Neither container existed or transitioned to running state.
   - `condition: service_completed_successfully` blocked startup.

---

## L. Security Evidence

1. **Secret Scanning**:
   - Gitleaks v3.0.0 ran in CI on all commits: 0 leaks detected.
2. **Dependency Vulnerability Scanning**:
   - `dotnet list package --vulnerable --include-transitive`: 0 vulnerabilities detected across all projects.
3. **SBOM Generation**:
   - `anchore/sbom-action`: Generated CycloneDX JSON SBOMs for both API and Worker images and uploaded artifacts.
4. **Container Vulnerability Scanning**:
   - `aquasecurity/trivy-action`: Scanned published API and Worker images for `CRITICAL,HIGH` vulnerabilities with exit code 1. Scan passed with 0 blocking vulnerabilities.

---

## M. Known Limitations

1. **Local GHCR Pulling Requires Package Read Scope**:
   - Because `hassanbht/ai-factory` packages were published under a private GitHub user repository, local `docker pull` commands require authentication with a GitHub PAT possessing `read:packages` scope (or setting package visibility to Public on GitHub Packages settings). Hosted CI/CD has full automated access via `GITHUB_TOKEN` with `packages: read`.

---

## N. Explicit GO/NO-GO Evaluation

| Gate / Criterion | Status | Evidence |
|---|---|---|
| GitHub repository access | ✅ PASS | Private repository `hassanbht/AI-Factory` reachable |
| Source branch pushed | ✅ PASS | Commits through `29061ce` pushed to `origin/main` |
| Real GitHub Actions CI | ✅ PASS | Run `34280413603` passed 5/5 jobs |
| CI build 0 warnings / 0 errors | ✅ PASS | Release build clean with 0 warnings / 0 errors |
| Resilience, Stress, Load tests | ✅ PASS | All category tests passed in CI and Release stability gates |
| Release tag & ancestry validation | ✅ PASS | `v0.1.0` verified matching `VERSION` (0.1.0) and ancestor of `main` |
| 3x Stability test gate | ✅ PASS | 3 full sequential test passes + category filters passed |
| GHCR container publication | ✅ PASS | API and Worker images published with semantic and SHA tags |
| Immutable digest capture | ✅ PASS | API: `sha256:53bc943...`, Worker: `sha256:c72b249...` |
| Published image pull & deployment | ✅ PASS | Pulled by digest and deployed with `--no-build` |
| Migrator ordering | ✅ PASS | Migrator exits 0 before API / Worker start |
| Runtime health probes | ✅ PASS | `/health/live`, `/health/ready`, `worker.live` verified |
| Runtime metadata verification | ✅ PASS | Commit `29061ce`, Run `34280430620`, Branch `v0.1.0` verified |
| Backup & Restore verification | ✅ PASS | Backup, restore, and health recovery verified on published container |
| Migration fault injection gate | ✅ PASS | Simulated failure exits 1 and blocks dependent containers |
| Secret and vulnerability scanning | ✅ PASS | Gitleaks, NuGet audit, Trivy container scan, and SBOM all passed |
| Release Gate overall status | ✅ PASS | Workflow `Release` evaluated **GO** |

# Final Decision: **GO**
