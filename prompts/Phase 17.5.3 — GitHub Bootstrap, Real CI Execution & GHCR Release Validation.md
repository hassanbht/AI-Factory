# Phase 17.5.3 — GitHub Bootstrap, Real CI Execution & GHCR Release Validation

## 1. Phase Objective

The objective of Phase 17.5.3 is to close the remaining empirical validation gaps from Phases 17.5.1 and 17.5.2 by connecting the AI Factory repository to a real GitHub repository, executing the CI workflow on real GitHub-hosted runners, publishing a real versioned release image set to GitHub Container Registry (GHCR), capturing immutable image digests, and validating deployment using the published registry artifacts rather than locally-built images.

This phase is intentionally an empirical validation and release-bootstrap phase.

Do not treat locally passing builds, locally validated YAML, or unexecuted workflow definitions as evidence that CI/CD works.

The primary success criterion is:

Source → GitHub Push → Real GitHub Actions CI → Version Validation → Release Workflow → GHCR Publish → Immutable Digest Capture → Published Image Pull → Docker Compose Release Deployment → Runtime Health Validation.

No GO decision is allowed for the real CI/CD/registry scope unless this chain has been executed successfully.

---

# 2. Mandatory First Step — Read Project Instructions and Prior Phase Context

Before making any changes:

1. Read `AGENTS.md` and all repository-level project instructions.
2. Inspect the repository structure.
3. Read these phase documents:

   - `.ai/context/phase-17.4.12-final-production-hardening-release-gate.md`
   - `.ai/context/phase-17.4.13-*.md` if present
   - `.ai/context/phase-17.5.1-cicd-release-automation-supply-chain.md`
   - `.ai/context/phase-17.5.2-cicd-runtime-validation-registry-artifact-integrity.md`

4. Inspect:

   - `.github/workflows/ci.yml`
   - `.github/workflows/release.yml`
   - `VERSION`
   - `apps/Directory.Build.props`
   - `docker-compose.yml`
   - `docker-compose.release.yml`
   - `Dockerfile.api`
   - `Dockerfile.worker`

5. Inspect the current git status, branches, tags and remotes.

Do not assume the prior workflow implementation is correct merely because it passed local YAML/config validation.

The purpose of this phase is to prove it on real infrastructure.

---

# 3. GitHub Repository Bootstrap

The repository currently has no configured Git remote and has not yet been pushed to GitHub.

Before changing CI/CD implementation, determine the exact current state:

```bash
git status
git branch --show-current
git remote -v
git log --oneline -20
git tag --list
gh auth status

Do not silently create or push to an unknown repository.

If no GitHub repository exists:

Determine whether the authenticated GitHub account can create repositories.
Propose the exact repository name and visibility based on existing project conventions.
Do not create a public repository if secrets, proprietary code, private infrastructure details, or internal documentation could be exposed.
Prefer a private repository unless project instructions explicitly require public visibility.

If repository creation or pushing requires an explicit user decision, stop before performing the destructive/external action and clearly report:

the proposed repository name
proposed visibility
remote URL
branch that will be pushed
whether tags will be pushed

Do not fabricate a successful GitHub operation.

Once explicit authorization exists, configure the remote and push the current implementation.

4. Repository Hygiene Gate Before First Push

Before the first remote push, inspect the repository for accidental sensitive material.

Run appropriate checks for:

.env
credentials
API keys
tokens
private certificates
database dumps
backup files
generated test artifacts
Docker volumes or database files
GitHub tokens
OpenRouter/API credentials
.db, .sqlite, .wal, .shm
local logs
test results containing secrets

Inspect both:

git status --ignored
git ls-files

and perform a secret scan over tracked content.

The first push must not accidentally publish:

secrets
credentials
production database files
SQLite backups
.env files
private tokens
local container artifacts

If any sensitive material is tracked, stop and fix repository hygiene before pushing.

Do not merely add a .gitignore entry while leaving an already tracked secret file committed.

5. Real GitHub Actions CI Validation

After the repository is available on GitHub, trigger the existing CI workflow through a real push or pull request.

The CI workflow must execute on GitHub-hosted infrastructure.

Do not consider:

local dotnet test
local Docker builds
local YAML parsing

as substitutes for this validation.

Capture the actual GitHub Actions run result.

Validate at minimum:

Checkout succeeds.
Required .NET SDK is installed.
Restore succeeds.
Release build succeeds.
Build has 0 warnings and 0 errors.
Full test suite succeeds.
Resilience category succeeds.
Stress category succeeds.
Load category succeeds.
ContainerIntegration category succeeds.
Secret scanning succeeds.
Dependency/security scanning succeeds.
Docker Compose validation succeeds.
Container smoke-test jobs succeed if Docker is available on the runner.
Workflow artifact uploads/downloads succeed where configured.
VERSION metadata is correctly propagated into the build.

Record:

workflow run ID
commit SHA
branch
GitHub Actions result
duration
failed/retried jobs if any

If the workflow fails, investigate the real runner failure and fix only the root cause.

Do not weaken tests or remove gates merely to make CI green.

6. GitHub Actions Supply-Chain Hardening Review

While validating the workflow, inspect all third-party GitHub Actions.

Every GitHub Action used in:

ci.yml
release.yml

must be reviewed for supply-chain safety.

Prefer immutable SHA pinning for third-party GitHub Actions where practical and consistent with project policy.

Do not leave critical release pipeline actions floating on mutable tags such as:

uses: some/action@v4

without explicitly documenting why.

For every pinned action, preserve traceability/documentation of the original release/version.

Also verify:

least-privilege permissions
job-specific permissions where possible
contents: read for ordinary CI jobs
packages: write only for registry publishing jobs
no unnecessary id-token: write
no unnecessary repository write permission
no plaintext credentials

Document any unavoidable exceptions.

7. Real Release Candidate Validation

Do not publish latest as the primary release artifact.

The release workflow must validate that the Git tag and repository VERSION are consistent.

Example strategy:

VERSION = 0.1.0
Release tag = v0.1.0

Before release publication:

Validate tag format.
Validate VERSION format.
Validate tag version equals VERSION.
Validate the tag commit belongs to the intended release branch/lineage.
Reject inconsistent versions.
Reject accidental duplicate release publication where registry-side evidence can prove duplication.
Ensure the release workflow uses the tag commit SHA as the immutable source identity.

Do not implement version rewriting inside the release workflow.

The repository version must already be correct before the release tag is created.

8. Real GHCR Publication

Configure the release workflow to publish the API and Worker images to GitHub Container Registry.

The image naming convention must be deterministic.

Example:

ghcr.io/<owner>/<repository>-api
ghcr.io/<owner>/<repository>-worker

Use the actual GitHub repository owner and repository name.

Do not hardcode a placeholder owner.

Required tags:

For version 0.1.0 and commit SHA <shortsha>:

0.1.0
sha-<shortsha>

Optional convenience tag:

latest

Only publish latest if the project explicitly defines the release semantics for it.

The deployment path must never depend on latest.

After publication, capture:

image name
semantic version tag
SHA tag
immutable registry digest

The release documentation must distinguish:

Mutable tag:
0.1.0

Immutable image identity:
ghcr.io/...@sha256:<digest>

The digest is the deployment identity.

9. OCI Metadata Validation

Verify that published images contain correct OCI metadata.

At minimum:

org.opencontainers.image.version
org.opencontainers.image.revision
org.opencontainers.image.source
org.opencontainers.image.created

Where supported by the existing architecture, also verify:

build number
branch
repository URL

Cross-check:

VERSION
↔ assembly metadata
↔ deployment diagnostics
↔ Docker build args
↔ OCI labels
↔ GitHub release tag
↔ Git commit SHA

These values must describe the same build identity.

Do not accept contradictory metadata.

10. Immutable Deployment Manifest

docker-compose.release.yml must support deployment using immutable published images.

The final deployment must be possible without building source code.

Example conceptual pattern:

AI_FACTORY_API_IMAGE=ghcr.io/...@sha256:<digest>
AI_FACTORY_WORKER_IMAGE=ghcr.io/...@sha256:<digest>

docker compose \
  -f docker-compose.yml \
  -f docker-compose.release.yml \
  up -d

The release overlay must not require:

docker build

or local source files beyond the Compose manifests and deployment configuration.

If the current release overlay only supports a mutable tag, extend it so immutable digest deployment is supported.

Prefer explicit environment variables such as:

AI_FACTORY_API_IMAGE
AI_FACTORY_WORKER_IMAGE

rather than reconstructing image names from partial variables.

Validate that the release configuration fails safely if required immutable image identities are absent.

11. Published Artifact Smoke Test

After real GHCR publication, perform a smoke test using the published images.

Critical rule:

Do not test images that were built locally from the source checkout.

The deployment must pull the published images from GHCR.

Validate:

Local images for the target tags/digests are removed or cannot accidentally satisfy the run.
docker compose pull retrieves the published artifacts.
The images used by the containers match the expected digests.
api-migrator runs successfully.
API starts only after successful migration.
Worker starts only after successful migration.
API becomes healthy.
Worker becomes healthy.
API /health/live returns success.
API /health/ready returns success.
Worker liveness probe is worker.live.
Worker readiness behavior remains correct.
Deployment diagnostics expose the expected version metadata.
Backup CLI works against the published API image.
Restore CLI works against the published API image.
API and Worker recover correctly after restore/restart.

Record the exact image digests actually running.

12. Deployment Diagnostics End-to-End Validation

Phase 17.5.2 identified an important limitation:

DeploymentDiagnosticsService reads:

Assembly.GetEntryAssembly()

which under dotnet test refers to the xUnit host rather than the real API executable.

This phase must close the empirical validation gap.

Do not necessarily redesign the production implementation if the real process behaves correctly.

Instead, run the real published API container and verify:

/api/operations/deployment

contains the expected sanitized metadata:

SemanticVersion
GitCommit
GitBranch
BuildTimestampUtc
BuildNumber
InformationalVersion

The metadata must match the published release.

If real container execution exposes a production bug, fix it and add the narrowest appropriate regression coverage.

Do not claim the VERSION → runtime diagnostics chain is verified until a real API process proves it.

13. Release Workflow Execution

Create and execute a real release candidate workflow.

The workflow must perform the existing release gates, including:

Version validation.
Build.
Full test suite.
Required stability runs.
Docker image build.
Security scanning.
SBOM generation if implemented.
Migration failure regression gate.
Backup/restore gate.
Container validation.
Registry publication.
Digest capture.
Published-image smoke test.

If the existing release.yml does not support testing a release without creating a permanent production tag, implement a safe release-candidate strategy.

Possible approaches include:

pre-release tag
workflow_dispatch with explicit version validation
temporary test repository package version
dedicated release candidate branch

Do not weaken release validation merely to avoid testing.

14. GitHub Release Artifact

After successful registry publication and validation, create a GitHub Release only if that is already intended by the project release strategy.

If added, the release must contain:

release version
commit SHA
build/run ID
API image digest
Worker image digest
SBOM artifact reference if available
security scan summary
deployment instructions using immutable digests

Do not include secrets.

If GitHub Release creation is intentionally deferred, document that explicitly.

15. Failure Scenarios That Must Be Empirically Verified

At least one real failure-path validation should be performed where safe.

Recommended:

15.1 Version mismatch

Prove that:

VERSION = 0.1.0
tag = v0.1.1

is rejected before image publication.

This should be performed in a safe test branch/tag or workflow-dispatch environment.

15.2 Migration failure

Re-run the existing migration fault injection in the real CI/container environment.

Verify:

api-migrator → exit 1
api → blocked
worker → blocked
15.3 Missing immutable image configuration

Verify the release deployment overlay fails safely when required image references are absent.

Do not perform destructive production tests.

16. Security Requirements

Before declaring GO:

No secrets committed to GitHub.
GitHub Actions permissions follow least privilege.
Registry publishing uses GitHub Actions token/appropriate scoped credentials rather than a hardcoded PAT.
Published images are scanned.
Dependency vulnerabilities are surfaced according to the existing gate policy.
OCI labels contain no secrets.
Deployment diagnostics remain sanitized.
Backup/restore logs remain sanitized.
Docker images continue to run as non-root.
Deployment uses immutable digests for the production path.

A mutable tag alone is insufficient evidence of supply-chain integrity.

17. Test and Validation Requirements

Run and report:

dotnet build apps/AI.Factory.slnx -c Release

Expected:

0 warnings
0 errors

Run the full suite locally as a regression baseline.

Then perform the real GitHub Actions CI validation.

Record both separately.

Do not combine local and remote evidence into one undifferentiated "passed" statement.

Use categories:

Category=Resilience
Category=Stress
Category=Load
Category=ContainerIntegration

The real CI result is authoritative for CI readiness.

18. Documentation

Create:

.ai/context/phase-17.5.3-github-bootstrap-real-ci-ghcr-release-validation.md

The document must include:

Phase objective.
Starting repository/remote state.
GitHub repository decision.
Repository hygiene checks.
Exact CI workflow run IDs.
CI commit SHA and branch.
CI job results.
Real runner failures encountered.
Root causes.
Fixes.
GHCR image names.
Published tags.
Immutable digests.
OCI metadata validation.
Published-image smoke-test evidence.
Deployment diagnostics evidence.
Migration fault injection evidence.
Backup/restore evidence.
Security scan results.
Known limitations.
Explicit GO/NO-GO criteria.
Final release readiness decision.

Every claim about GitHub Actions, GHCR publication, or published-image execution must distinguish:

Actually executed

from:

Implemented but not executed

Never present unexecuted workflow code as empirical validation.

19. Explicit GO / NO-GO Criteria
GO requires all of:
Repository successfully available on GitHub.
CI workflow successfully executed on real GitHub Actions.
Full CI gate passes.
No committed secrets found.
Release version validation works.
Real release workflow executes successfully.
API image successfully published to GHCR.
Worker image successfully published to GHCR.
Immutable digests captured.
Published images successfully pulled.
Deployment uses published images, not local builds.
Migrator ordering succeeds.
API healthy.
Worker healthy.
Deployment diagnostics metadata matches release metadata.
Backup succeeds.
Restore succeeds.
Migration failure blocking remains verified.
Build remains 0 warnings / 0 errors.
Full regression suite remains green.
NO-GO if any of:
GitHub Actions workflow fails.
Registry publication fails.
Published image cannot be pulled.
Runtime image digest differs from expected digest.
Deployment relies on a mutable tag without an immutable identity.
Release metadata is inconsistent.
Secrets are committed.
API/Worker fail to start after migration.
Health checks fail.
Backup/restore fails.
Required release validation is only locally simulated.
20. Scope Discipline

This phase is not permission to redesign unrelated application features.

Do not:

redesign Clean Architecture
replace SQLite
introduce Kubernetes
introduce a new cloud provider
rewrite the resilience subsystem
add distributed tracing unless needed to fix a discovered release validation defect
add unrelated observability platforms
modify passing business logic

Only change code when required to:

make real CI execution succeed correctly
publish and validate immutable release artifacts
fix a genuine production/runtime metadata issue
improve supply-chain/release correctness
fix a defect empirically discovered by this phase
21. Final Deliverable

Provide:

Summary

What was actually executed.

GitHub Evidence

Repository URL, branch, workflow run IDs and statuses.

CI Evidence

Actual GitHub Actions results.

Registry Evidence

Published image names, tags and immutable digests.

Deployment Evidence

Exact published images/digests deployed and health results.

Problems Found

List every real issue discovered.

Problems Fixed

List root cause and fix.

Deferred Limitations

Clearly distinguish remaining unexecuted capabilities.

Explicit Decision

One of:

GO

or:

NO-GO

The decision must be based only on empirical evidence collected in this phase.

Do not commit automatically unless project instructions explicitly require it.
Do not push, create repositories, create release tags, or publish artifacts without explicit authorization for external side effects.