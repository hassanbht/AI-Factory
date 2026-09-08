# Phase 17.5.4 — GitHub Access Recovery, Hosted CI Execution & GHCR Empirical Release

## 1. Phase Objective

The objective of Phase 17.5.4 is to remove the external GitHub access blocker identified in Phase 17.5.3 and complete the empirical CI/CD validation that could not previously be executed.

Phase 17.5.3 established that:

- repository hygiene passed;
- local build and test gates passed;
- CI/CD workflow definitions are implemented;
- Docker Compose configurations are valid;
- release configuration fails safely when required variables are missing;
- no real GitHub Actions workflow was executed;
- no GHCR image was published;
- no immutable registry digest was captured;
- no published image was deployed.

Therefore this phase must focus on actual hosted execution rather than further speculative CI/CD implementation.

The required end-to-end chain is:

Source repository
→ GitHub repository access
→ Push
→ Real GitHub Actions CI
→ Real release workflow
→ GHCR publication
→ Immutable digest capture
→ Pull published images
→ Release Compose deployment
→ Migration ordering validation
→ API health validation
→ Worker health validation
→ Deployment metadata validation
→ Backup validation
→ Restore validation

No GO decision may be issued for release automation until this chain has been empirically completed.

---

# 2. Mandatory First Step — Read Existing Instructions and Prior Evidence

Before making any changes:

1. Read `AGENTS.md`.
2. Inspect repository structure.
3. Read:

   - `.ai/context/phase-17.5.1-cicd-release-automation-supply-chain.md`
   - `.ai/context/phase-17.5.2-cicd-runtime-validation-registry-artifact-integrity.md`
   - `.ai/context/phase-17.5.3-github-bootstrap-real-ci-ghcr-release-validation.md`

4. Inspect:

   - `.github/workflows/ci.yml`
   - `.github/workflows/release.yml`
   - `VERSION`
   - `apps/Directory.Build.props`
   - `Dockerfile.api`
   - `Dockerfile.worker`
   - `docker-compose.yml`
   - `docker-compose.release.yml`

5. Inspect current Git state:

```bash
git status
git branch --show-current
git remote -v
git log --oneline -20
git tag --list
git ls-remote origin
```

Do not assume that the repository is reachable simply because an `origin` remote exists.

---

# 3. GitHub Access Recovery Gate

Phase 17.5.3 identified:

```text
origin:
https://github.com/hassanbehtooei/AI-Factory.git

git ls-remote origin:
Repository not found

gh CLI:
Not installed on host

SSH:
Permission denied (publickey)

No GitHub PAT configured
```

The first responsibility of this phase is to determine the exact cause.

Possible causes include:

1. Repository does not exist.
2. Repository exists but is private and the current credentials lack access.
3. Repository name or owner is incorrect.
4. GitHub authentication is missing.
5. GitHub authentication exists but is not available to Git.
6. Remote URL is stale or incorrect.
7. SSH authentication is configured incorrectly.
8. The repository belongs to a different GitHub account or organization.

Do not guess which case applies.

---

# 4. Preferred Authentication Strategy

Use the safest supported GitHub authentication method available for the development environment.

Preferred order:

## Option A — GitHub CLI

Install or configure `gh` only if allowed by the host environment.

Then authenticate interactively:

```bash
gh auth login
```

Verify:

```bash
gh auth status
```

Then verify repository access.

## Option B — HTTPS Git Credential Manager / Browser Authentication

If GitHub CLI installation is not appropriate, use the host's supported secure credential mechanism.

Do not write tokens directly into:

- source files;
- `.env` files committed to Git;
- Dockerfiles;
- workflow YAML;
- shell history intentionally;
- project documentation.

## Option C — SSH

Use SSH only if a properly configured key already exists or the user explicitly chooses SSH authentication.

Verify:

```bash
ssh -T git@github.com
```

Do not generate or upload keys silently.

---

# 5. External Side-Effect Authorization Boundary

Before performing any action that creates or modifies external GitHub state, clearly identify the intended action.

Examples:

- creating a GitHub repository;
- pushing commits;
- pushing tags;
- creating a GitHub Release;
- publishing packages to GHCR.

If explicit user authorization is required by project or environment policy, stop and request it.

Do not fabricate successful external operations.

Once authorization exists, perform the minimum required external action.

---

# 6. Repository Existence and Remote Validation

Determine whether:

```text
hassanbehtooei/AI-Factory
```

exists and whether the authenticated account has access.

If it exists:

- confirm ownership/access;
- confirm intended visibility;
- confirm the current `origin` URL is correct.

If it does not exist:

- report that clearly;
- propose creation using the existing repository name;
- prefer private visibility unless the user explicitly wants the project public;
- do not create a repository without required authorization.

If the repository exists under another owner or organization:

- do not overwrite the remote;
- report the exact discrepancy;
- propose the safe correction.

---

# 7. Pre-Push Safety Gate

Before the first push, repeat repository hygiene validation.

Inspect:

```bash
git status
git status --ignored
git ls-files
```

Search tracked content for:

- API keys;
- tokens;
- passwords;
- connection strings;
- `.env`;
- `.db`;
- `.sqlite`;
- `.wal`;
- `.shm`;
- `.pem`;
- `.key`;
- `.pfx`;
- private certificates;
- backup files;
- production artifacts.

Important:

If a sensitive file is already tracked, do not merely add it to `.gitignore`.

Remove it correctly from the tracked set and assess whether history rewriting or credential rotation is necessary.

Do not push until this gate passes.

---

# 8. Preserve Existing Local Work

Phase 17.5.3 explicitly reported:

> Working tree untracked resilience code was preserved.

Before any commit or push:

1. inspect all modified and untracked files;
2. identify files belonging to the intended Phase 17.x implementation;
3. identify generated artifacts;
4. identify unrelated experiments;
5. do not accidentally discard user work.

Do not use destructive commands such as:

```bash
git clean -fd
git reset --hard
```

unless explicitly authorized.

---

# 9. Commit Preparation

Only after repository hygiene and working-tree review:

1. inspect the complete diff;
2. ensure Phase 17.5.1–17.5.3 implementation files are included correctly;
3. ensure generated artifacts are excluded;
4. ensure no secrets are included;
5. ensure documentation matches implementation.

Do not commit unrelated user work.

If multiple logical changes are present, use clean commits that preserve traceability.

Do not rewrite history unless explicitly required.

---

# 10. First Push and Real CI Execution

After successful GitHub access and required authorization:

1. push the intended branch;
2. record:

   - remote URL;
   - branch;
   - commit SHA;
   - push result.

3. identify the GitHub Actions CI workflow run triggered by the push.

The workflow must run on a real GitHub-hosted runner.

Capture:

- workflow run ID;
- URL/reference if available;
- commit SHA;
- branch;
- trigger type;
- runner OS;
- each job result;
- total duration.

Do not claim CI passed until GitHub reports success.

---

# 11. CI Failure Investigation

If real GitHub Actions fails:

1. retrieve the actual job logs;
2. identify the first real root cause;
3. distinguish:

   - runner/environment issue;
   - workflow issue;
   - dependency issue;
   - source-code issue;
   - Docker issue;
   - permission issue.

Fix only the root cause.

Do not:

- remove quality gates;
- skip failing tests;
- weaken security scans;
- suppress failures;
- replace real CI validation with local validation.

After fixing, push the correction and rerun CI.

Repeat until either:

```text
CI PASSES
```

or a genuine blocking issue remains.

---

# 12. Real CI Acceptance Criteria

The real GitHub Actions CI workflow must demonstrate successful execution of the required gates.

At minimum:

- checkout succeeds;
- restore succeeds;
- Release build succeeds;
- build has 0 warnings;
- build has 0 errors;
- full test suite succeeds;
- Resilience tests succeed;
- Stress tests succeed;
- Load tests succeed;
- ContainerIntegration tests succeed;
- secret scanning succeeds;
- dependency/security scanning succeeds;
- Docker Compose validation succeeds;
- container validation/smoke tests succeed where required by the workflow.

Record actual counts and results.

Do not replace actual CI counts with the previous local counts.

---

# 13. Real Release Candidate

Once CI is green, validate the release workflow.

Use the project's VERSION file as the authoritative semantic version.

Example:

```text
VERSION:
0.1.0

Git tag:
v0.1.0
```

Before publishing:

1. validate VERSION format;
2. validate tag format;
3. validate tag version equals VERSION;
4. validate commit identity;
5. validate release branch/ancestry;
6. reject mismatch.

Do not modify VERSION automatically inside the release workflow.

---

# 14. Safe First GHCR Publication

Before publishing, inspect the release workflow permissions.

Publishing permissions must be minimal.

Expected separation:

```text
Normal CI:
contents: read

Registry publishing job:
packages: write
```

Do not grant unnecessary repository write permissions.

Do not use a hardcoded PAT.

Use GitHub Actions-provided authentication where appropriate.

Publish:

```text
API image
Worker image
```

using the actual repository owner/name.

Required release tags:

```text
<semantic-version>
sha-<short-commit>
```

Do not use `latest` as the deployment identity.

---

# 15. Immutable Digest Capture

After GHCR publication, capture the immutable digest of both images.

Record separately:

```text
API:
repository
semantic version tag
SHA tag
sha256 digest

Worker:
repository
semantic version tag
SHA tag
sha256 digest
```

Example deployment identity:

```text
ghcr.io/<owner>/<image>@sha256:<digest>
```

The release deployment validation must use the digest.

A mutable tag alone is not sufficient.

---

# 16. Published Image Pull Validation

Before deployment testing:

1. ensure local source builds cannot accidentally satisfy the deployment;
2. pull the images from GHCR;
3. inspect the pulled image identities;
4. verify the pulled digests equal the recorded release digests.

The validation must prove that runtime containers are based on published registry artifacts.

Do not accept:

```text
docker compose up
```

if Docker can silently use an existing local image instead of pulling the published artifact.

---

# 17. Published Artifact Deployment Validation

Deploy using:

```text
docker-compose.yml
+
docker-compose.release.yml
```

with immutable image identities.

Do not build source code during this validation.

Required sequence:

1. pull published images;
2. start migrator;
3. verify migrator exits successfully;
4. verify API starts only after successful migration;
5. verify Worker starts only after successful migration;
6. verify API health;
7. verify Worker health.

Validate:

```text
API:
/health/live
/health/ready

Worker:
worker.live
worker.ready
```

The Docker healthcheck must remain liveness-based.

---

# 18. Runtime Metadata Validation

Phase 17.5.2 documented that unit tests cannot fully prove:

```text
VERSION
→ Assembly metadata
→ DeploymentDiagnosticsService
→ Running API
```

because `Assembly.GetEntryAssembly()` behaves differently under xUnit.

This phase must validate the real running API.

Call the existing deployment diagnostics endpoint.

Verify:

- SemanticVersion;
- GitCommit;
- GitBranch;
- BuildTimestampUtc;
- BuildNumber;
- InformationalVersion.

Cross-check against:

- VERSION;
- Git commit SHA;
- GitHub Actions run/build ID;
- release tag;
- OCI metadata.

All values must represent the same release.

If they do not, treat this as a production defect and fix the root cause.

---

# 19. OCI Label Validation

Inspect the published API and Worker images.

Validate at minimum:

```text
org.opencontainers.image.version
org.opencontainers.image.revision
org.opencontainers.image.source
org.opencontainers.image.created
```

Cross-check:

```text
VERSION
↔ Git tag
↔ Git SHA
↔ Assembly InformationalVersion
↔ Deployment diagnostics
↔ OCI labels
↔ GHCR digest
```

Any contradiction is a release gate failure.

---

# 20. Backup and Restore Validation Against Published API Image

Run backup using the published API artifact.

Validate:

```text
backup succeeds
backup validates
no orphan WAL/SHM sidecars remain
retention works
```

Then perform restore using the published image.

Validate:

```text
pre-restore safety backup
restore succeeds
API restarts successfully
Worker restarts successfully
health checks recover
```

Do not test only locally built images.

---

# 21. Migration Failure Fault Injection

Re-run the existing migration failure mechanism against the real container/deployment configuration.

Validate empirically:

```text
api-migrator → exit code 1

api → does not start

worker → does not start
```

The dependent services must remain blocked because:

```text
condition: service_completed_successfully
```

Do not rely only on static configuration inspection.

Record the actual container states.

---

# 22. Supply-Chain Security Validation

Verify the actual published release pipeline includes:

1. no committed secrets;
2. least-privilege workflow permissions;
3. pinned or documented GitHub Actions policy;
4. image vulnerability scanning;
5. dependency vulnerability scanning;
6. SBOM generation if implemented;
7. immutable digest capture;
8. digest-based deployment validation.

If a security scan finds a blocking vulnerability, do not ignore it.

Follow the project's documented severity policy.

If no severity policy exists, document the finding rather than silently defining a new blocking threshold.

---

# 23. GitHub Release

Create a GitHub Release only if the release strategy and user authorization permit it.

If created, include:

- semantic version;
- commit SHA;
- GitHub Actions run/build ID;
- API digest;
- Worker digest;
- SBOM reference if applicable;
- security scan summary;
- immutable deployment instructions.

Do not include secrets or internal credentials.

If release creation is deferred, document that explicitly.

---

# 24. Regression Validation

After all fixes:

Run the local regression baseline:

```bash
dotnet build apps/AI.Factory.slnx -c Release
dotnet test apps/AI.Factory.slnx -c Release --no-build --blame-hang-timeout 90s
```

Then confirm the hosted CI result independently.

Do not combine local and hosted evidence.

The hosted result is authoritative for CI readiness.

---

# 25. Documentation

Create:

```text
.ai/context/phase-17.5.4-github-access-hosted-ci-ghcr-empirical-release.md
```

The document must contain:

## A. Starting State

- Git remote configuration;
- repository reachability;
- authentication state;
- Phase 17.5.3 blocker.

## B. GitHub Access Resolution

- root cause;
- chosen authentication method;
- repository verification.

Do not include credentials or tokens.

## C. Push Evidence

- branch;
- commit SHA;
- push result.

## D. GitHub Actions Evidence

- workflow run IDs;
- jobs;
- results;
- durations.

## E. Problems Found

Every real runner or registry problem discovered.

## F. Problems Fixed

Root cause and implementation fix.

## G. GHCR Evidence

- image names;
- tags;
- immutable digests.

## H. Published Deployment Evidence

- pulled digest;
- running digest;
- migrator status;
- API health;
- Worker health.

## I. Runtime Metadata Evidence

Cross-check results.

## J. Backup/Restore Evidence

Results against published images.

## K. Migration Fault Injection

Actual container states after failure.

## L. Security Evidence

Actual scans and findings.

## M. Known Limitations

Only genuine remaining limitations.

## N. Explicit GO/NO-GO Criteria

Every criterion must have actual evidence.

---

# 26. Explicit GO / NO-GO Criteria

## GO requires:

- GitHub repository access works.
- Source branch successfully pushed.
- Real GitHub Actions CI executed successfully.
- Required CI gates passed.
- Release workflow executed successfully.
- API image published to GHCR.
- Worker image published to GHCR.
- Immutable digests captured.
- Published images successfully pulled.
- Runtime deployment uses published digests.
- Migrator succeeds.
- API succeeds.
- Worker succeeds.
- Health probes succeed.
- Runtime deployment metadata matches release metadata.
- Backup succeeds.
- Restore succeeds.
- Services recover after restore.
- Migration failure empirically blocks API and Worker.
- Security gates complete successfully according to policy.
- Build remains 0 warnings / 0 errors.
- Regression suite remains green.

## NO-GO if:

- GitHub authentication remains unavailable;
- repository cannot be accessed;
- push cannot be completed;
- real CI fails;
- release workflow fails;
- GHCR publication fails;
- immutable digest cannot be captured;
- published image cannot be pulled;
- deployment accidentally uses local images;
- runtime metadata differs from release identity;
- migration ordering fails;
- health checks fail;
- backup/restore fails;
- migration failure does not block dependents;
- a required security gate fails.

---

# 27. Scope Discipline

Do not redesign unrelated application code.

Do not:

- replace SQLite;
- introduce Kubernetes;
- introduce cloud infrastructure;
- redesign Clean Architecture;
- rewrite resilience subsystems;
- add unrelated observability platforms;
- modify passing business logic.

Only change code/workflows/configuration when required by an empirically discovered GitHub, CI, GHCR, metadata, registry, or deployment defect.

---

# 28. Final Deliverable

Provide:

## Summary

What was actually executed.

## GitHub Access

How the Phase 17.5.3 blocker was resolved.

## Push Evidence

Branch and commit.

## Real CI Evidence

Workflow IDs and results.

## Release Evidence

Tag/version/build identity.

## GHCR Evidence

Published images and immutable digests.

## Published Deployment Evidence

Proof that pulled registry artifacts were actually deployed.

## Runtime Metadata Evidence

VERSION → runtime validation.

## Problems Found

All real issues.

## Problems Fixed

Root causes and fixes.

## Deferred Limitations

Only real remaining gaps.

## Explicit Decision

Exactly one:

# GO

or

# NO-GO

The decision must be based exclusively on empirical evidence.

Do not fabricate GitHub, GHCR, workflow, registry, digest, or deployment results.

Do not automatically create a repository, push source code, create tags, publish images, or create releases unless the required external authorization is available.