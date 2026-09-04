# Phase 17.5.1 — CI/CD Release Automation, Supply-Chain Security & Production Deployment Validation

## 1. Phase Objective

Phase 17.4.x established production readiness, deterministic resilience,
backup/restore, deployment health checks, fault injection, and a stable
automated test suite.

The objective of Phase 17.5.1 is to establish a production-grade automated
delivery pipeline and release process.

The platform must move from:

"Production-ready code that can be manually validated and deployed"

to:

"A reproducible, secure, automated, auditable release pipeline capable of
building, testing, scanning, packaging, validating, and promoting a release
without relying on undocumented manual steps."

This phase must implement:

1. CI pipeline automation.
2. Deterministic release validation.
3. Docker image versioning and traceability.
4. Software supply-chain security checks.
5. Dependency and vulnerability scanning.
6. Secret scanning.
7. Container image security scanning where practical.
8. Automated Docker deployment smoke testing.
9. Release artifact generation.
10. Version and build metadata propagation.
11. Release gating.
12. Rollback-safe deployment strategy.
13. Production deployment validation documentation.

Do not redesign existing application architecture unless required to support
delivery automation.

Preserve all existing Phase 17.4.x behavior.

---

# 2. Required Preparation Before Coding

Before modifying any files:

1. Inspect the complete current repository state.
2. Read AGENTS.md and all repository-level development instructions.
3. Review all Phase 17.4.8 through Phase 17.4.13 documentation.
4. Inspect:
   - docker-compose.yml
   - Dockerfile.api
   - Dockerfile.worker
   - Program.cs for API
   - Program.cs for Worker
   - application version metadata
   - existing test projects
   - existing configuration and secrets handling
5. Search the repository for:
   - GitHub Actions
   - Azure DevOps pipelines
   - CI configuration
   - version generation
   - Docker image tags
   - release metadata
   - vulnerability scanning
   - secret scanning
6. Reuse existing mechanisms where possible.
7. Do not introduce duplicate CI systems.

Before implementing each change, identify:

- Callers/importers.
- Configuration consumers.
- Runtime impact.
- Security impact.
- CI/CD impact.
- Rollback impact.

Do not silently replace existing working mechanisms.

---

# 3. CI Pipeline Architecture

Create a production-quality CI workflow.

Preferred platform:

GitHub Actions, unless the repository already uses another CI platform.

The CI pipeline must run on:

- Pull requests.
- Pushes to the main/release branch.

Separate the pipeline into logical jobs where practical.

Recommended jobs:

1. Restore
2. Build
3. Unit / Integration Tests
4. Resilience Tests
5. Stress Tests
6. Load Tests
7. Container Configuration Validation
8. Docker Build
9. Security / Dependency Scan
10. Secret Scan
11. Container Smoke Test
12. Release Gate

Avoid duplicating expensive work unnecessarily.

Use job dependencies and artifact reuse where appropriate.

---

# 4. Build & Test Gate

The pipeline must execute the same production validation commands that have
already proven stable locally.

Required validation:

```powershell
dotnet restore apps/AI.Factory.slnx

dotnet build apps/AI.Factory.slnx --configuration Release --no-restore

dotnet test apps/AI.Factory.slnx --configuration Release --no-build --blame-hang-timeout 90s

The release pipeline must fail if:

Build fails.
Any compiler warning exists if warnings-as-errors policy is enabled.
Any test fails.
Any test hangs.
A required validation category fails.

Also execute category gates:

dotnet test apps/AI.Factory.slnx --no-build --filter "Category=Resilience"

dotnet test apps/AI.Factory.slnx --no-build --filter "Category=Stress"

dotnet test apps/AI.Factory.slnx --no-build --filter "Category=Load"

dotnet test apps/AI.Factory.slnx --no-build --filter "Category=ContainerIntegration"

Do not weaken existing tests.

Do not skip tests to make CI green.

5. Multi-Run Stability Gate

Phase 17.4.13 demonstrated five consecutive successful runs locally.

Introduce a configurable CI stability gate.

The default CI policy should be reasonable for execution time.

Suggested policy:

Pull Requests:

One complete suite run.

Main branch:

One complete suite run.
Required targeted category validation.

Release workflow:

Three consecutive full-suite runs.

Do not blindly execute five full runs on every pull request unless runtime is
acceptable.

The release workflow must fail immediately if a run fails.

Document the difference between:

PR validation.
Main branch validation.
Release certification.
6. Versioning & Build Traceability

Implement deterministic version metadata.

Every production build should expose enough metadata to answer:

Which application version is running?
Which Git commit produced it?
When was it built?
Which CI run produced it?

Introduce or extend a versioning strategy.

Recommended metadata:

SemanticVersion
GitCommit
GitBranch
BuildTimestampUtc
BuildNumber / CI Run ID
InformationalVersion

Do not expose secrets.

The metadata should be available through the existing sanitized deployment
diagnostics mechanism where appropriate.

Do not create a second competing diagnostics endpoint.

Preferred approach:

Extend the existing deployment diagnostics model.

Requirements:

Local builds still work without CI variables.
CI builds inject real metadata.
Missing CI metadata must fall back safely.
Runtime deployment diagnostics must never expose credentials or environment
secrets.

Add automated tests.

7. Docker Image Tagging Strategy

Docker images must not rely exclusively on:

latest

Introduce deterministic tags.

Recommended tags:

ai-factory-api:<semantic-version>
ai-factory-api:<git-sha>
ai-factory-worker:<semantic-version>
ai-factory-worker:<git-sha>

Optional:

latest only for explicitly designated development or stable releases.

The release pipeline must build API and Worker images using immutable traceable
tags.

The deployment documentation must clearly explain:

Development tag.
Commit tag.
Release version tag.
Rollback tag.

Do not overwrite a release tag after publication.

8. Container Supply-Chain Hardening

Inspect current Dockerfiles.

Verify:

Multi-stage builds remain intact.
Runtime images are minimal.
Containers run as non-root.
No secrets are copied into images.
.dockerignore prevents unnecessary build context leakage.
Build output is reproducible.
Health checks remain correct.

Explicitly verify that:

Worker container health represents liveness, not readiness.

The worker health check must remain based on:

worker.live

and must not revert to:

worker.ready

because transient dependency degradation must not trigger a container restart
loop.

Add CI regression validation for this invariant.

9. Dependency & Vulnerability Scanning

Add dependency vulnerability scanning appropriate for the technology stack.

At minimum inspect:

.NET NuGet dependencies.
Docker base images where practical.

The pipeline should:

Detect known high-severity vulnerabilities.
Produce actionable output.
Fail according to a documented severity policy.

Do not silently ignore findings.

Recommended policy:

Critical vulnerabilities: fail release gate.
High vulnerabilities: fail release gate unless explicitly allowlisted.
Medium/Low: report, unless project policy requires failure.

If an allowlist mechanism is introduced:

It must be explicit.
It must contain justification.
It must have an expiry/review policy.
It must not silently suppress future findings.
10. Secret Scanning

Introduce automated repository secret scanning.

The scan must detect likely committed:

API keys.
Access tokens.
Passwords.
Private keys.
Connection strings.

Do not add actual secrets to tests.

Use safe synthetic values.

CI must fail when a new secret is detected unless explicitly classified as a
known safe test value.

Ensure:

.env is not committed.
Example configuration contains placeholders only.
Docker images do not contain secrets.
Logs and diagnostics remain sanitized.

Add regression checks where practical.

11. SBOM / Dependency Inventory

Generate a Software Bill of Materials for release builds.

The SBOM should provide at least:

Application package dependencies.
Package versions.
Release/build identity.

Use an industry-standard format where practical.

The SBOM must be:

Generated during release builds.
Stored as a CI artifact.
Associated with the release version.

Do not manually maintain dependency inventories.

12. Container Security Scanning

Where practical in the CI environment, scan built Docker images for known
vulnerabilities.

Scan:

API image.
Worker image.

Policy:

Critical findings fail release.
High findings fail release unless explicitly documented and temporarily
accepted.
Results must be retained as CI artifacts.

Do not fabricate vulnerability results.

If the selected scanning tool cannot run in the available CI environment,
document the limitation and provide a reproducible command for operators.

13. Automated Docker Deployment Smoke Test

After Docker images are built, execute a real container smoke test.

The CI smoke test must validate:

docker compose config succeeds.
Images build successfully.
Migrator starts.
Migrator completes successfully.
API does not start before migration succeeds.
Worker does not start before migration succeeds.
API becomes healthy.
Worker becomes healthy.
API /health/live returns 200.
API /health/ready returns 200.
Worker contains worker.live.
Worker readiness behavior is verified without confusing readiness and
liveness.
Operational deployment diagnostics respond successfully.
No secrets appear in diagnostics output.
Stack can shut down cleanly.

Where possible, automate the checks rather than relying on log inspection.

Use explicit timeouts.

Always clean up containers and volumes according to the test's isolation
requirements.

14. Migration Failure Regression Gate

Phase 17.4.13 introduced migration failure fault injection.

Preserve and integrate this into CI.

The release validation must verify:

Simulated migration failure exits non-zero.
API does not start.
Worker does not start.
Docker Compose dependency conditions prevent invalid startup.

Use:

AI_FACTORY_MIGRATE_FAIL_TEST=1

or the existing canonical mechanism.

This must remain a test-only capability.

Production deployment must not accidentally run with migration failure
simulation enabled.

Add regression protection preventing the test environment variable from being
included in production environment templates.

15. Backup/Restore Release Validation

Integrate existing backup/restore functionality into release validation.

The release smoke test should verify:

Backup can be created.
Backup passes integrity validation.
No unexpected WAL/SHM artifacts remain.
Restore validates the backup before touching active data.
Restore completes successfully.
API and Worker can start successfully after restore.

Use isolated CI volumes/databases only.

Never restore over real production data.

Preserve existing backup mutual exclusion behavior.

Add a regression test ensuring concurrent backups remain rejected.

16. Rollback Strategy

Document and validate a production rollback strategy.

The rollback strategy must distinguish:

A. Application rollback
B. Database migration rollback
C. Configuration rollback

Application rollback:

Previous immutable Docker image tag can be redeployed.

Database policy:

Preserve forward-only migration policy unless the existing architecture
explicitly supports safe rollback.
Do not introduce blind automatic migration downgrades.

Configuration rollback:

Restore the previous versioned configuration template.
Do not restore secrets through source control.

Document cases where application rollback is unsafe due to schema changes.

The release process should require migration compatibility consideration.

17. Release Gate Model

Create an explicit release gate.

Suggested GO criteria:

Gate	Required
Release build succeeds	Yes
Zero build errors	Yes
Required warning policy passes	Yes
Full test suite passes	Yes
Resilience tests pass	Yes
Stress tests pass	Yes
Load tests pass	Yes
Container integration tests pass	Yes
Migration failure test passes	Yes
Docker Compose validation passes	Yes
Production images build	Yes
Container smoke test passes	Yes
Backup succeeds	Yes
Restore succeeds	Yes
API healthy after restore	Yes
Worker healthy after restore	Yes
Secret scan passes	Yes
Dependency security policy passes	Yes
Container security policy passes where supported	Yes
SBOM generated	Yes
Version metadata available	Yes
Rollback instructions documented	Yes

The release workflow must produce:

GO

or:

NO-GO

based on actual validation evidence.

Do not produce GO merely because the workflow completed.

A failed mandatory gate must result in NO-GO.

18. Release Artifact Generation

The release workflow must publish artifacts where supported:

Test results.
Build metadata.
SBOM.
Security scan reports.
Docker image digests.
Release validation summary.

Do not publish:

.env files.
Secrets.
Private keys.
Raw connection strings.

Artifacts must be named using version/build identity.

19. Documentation

Create:

.ai/context/phase-17.5.1-cicd-release-automation-supply-chain.md

Document:

Phase objective.
CI architecture.
Job graph.
PR validation.
Main branch validation.
Release certification.
Versioning model.
Docker image tagging.
Supply-chain scanning.
Secret scanning.
SBOM generation.
Container scanning.
Migration failure gate.
Backup/restore validation.
Deployment smoke testing.
Rollback strategy.
Release artifacts.
GO/NO-GO criteria.
Known limitations.
Final validation results.

Also update the production operational runbook where required.

20. Testing Requirements

Add automated tests for all new application-level behavior.

For CI YAML and Docker configuration:

Add regression tests where practical.
Validate YAML/Compose configuration.
Avoid brittle string-only tests when structural validation is possible.

Test:

Version metadata fallback.
Version metadata CI injection.
Sanitized deployment diagnostics.
Migration failure trigger safety.
Backup concurrency remains protected.
Image tagging logic if implemented in application/repository scripts.

Do not weaken any existing tests.

21. Validation Procedure

Run:

dotnet build apps/AI.Factory.slnx

Expected:

0 warnings
0 errors

Run full tests:

dotnet test apps/AI.Factory.slnx --blame-hang-timeout 90s

Run targeted categories:

dotnet test apps/AI.Factory.slnx --filter "Category=Resilience"

dotnet test apps/AI.Factory.slnx --filter "Category=Stress"

dotnet test apps/AI.Factory.slnx --filter "Category=Load"

dotnet test apps/AI.Factory.slnx --filter "Category=ContainerIntegration"

Validate Docker:

docker compose config -q

docker compose build

Run production smoke test.

Run migration failure smoke test.

Run backup/restore smoke test.

Run security scans where CI tooling is available.

Do not claim a validation passed unless it was actually executed.

22. Required Final Report

At completion provide:

Files created.
Files modified.
CI workflow architecture.
Versioning implementation.
Image tagging strategy.
Security scanning implementation.
Secret scanning implementation.
SBOM implementation.
Container scanning implementation.
Docker smoke-test results.
Migration failure validation results.
Backup/restore validation results.
Rollback strategy.
Release artifacts.
Build results.
Full test results.
Category test results.
Known limitations.
Problems found and fixed.
Explicit:
GO

or:

NO-GO

The decision must be based on actual executed evidence.

Do not silently hide failures.

Do not mark optional or unsupported tooling as successfully implemented.

Do not commit changes unless explicitly requested.