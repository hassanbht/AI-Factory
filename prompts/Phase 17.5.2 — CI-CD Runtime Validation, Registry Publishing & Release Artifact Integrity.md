# Phase 17.5.2 — CI/CD Runtime Validation, Registry Publishing & Release Artifact Integrity

## 1. Phase Objective

The objective of Phase 17.5.2 is to move the CI/CD implementation created in Phase 17.5.1 from **locally validated workflow definitions** to **empirically validated release automation**.

Phase 17.5.1 established:

- deterministic versioning,
- CI workflow definitions,
- release workflow definitions,
- category-specific quality gates,
- Docker build validation,
- SBOM generation,
- vulnerability scanning,
- deployment diagnostics metadata,
- local release-gate validation.

However, the workflows have not yet been proven on a real GitHub Actions runner, and release artifacts are not yet published to a real container registry.

This phase must therefore validate the complete lifecycle:

> Source Commit → GitHub Actions CI → Tests → Security Gates → Docker Build → Artifact Metadata → Registry Publish → Immutable Image Verification → Release Gate

The goal is not merely to make GitHub Actions "green". The goal is to prove that the generated production artifact is:

1. traceable,
2. reproducible,
3. immutable,
4. security-scanned,
5. correctly versioned,
6. published to a real registry,
7. verifiable after publication,
8. deployable using the exact published image.

---

# 2. Starting Conditions

Phase 17.5.1 has already implemented and locally validated:

- `VERSION`
- `apps/Directory.Build.props`
- CI workflow
- Release workflow
- Git metadata propagation
- Build number propagation
- Docker build arguments
- deployment diagnostics metadata
- SBOM generation
- Trivy/container scanning
- category-based test gates
- migration failure regression gate
- backup/restore regression gate

The following limitation remains explicitly unresolved:

> The workflows have not yet been executed on an actual GitHub Actions runner.

The following limitation also remains:

> No container registry is currently configured, so release images are not pushed or consumed as immutable deployment artifacts.

This phase closes those gaps.

---

# 3. Scope Rules

Before changing code:

1. Read `AGENTS.md`.
2. Inspect existing `.github/workflows/ci.yml`.
3. Inspect existing `.github/workflows/release.yml`.
4. Inspect:
   - `VERSION`
   - `apps/Directory.Build.props`
   - Dockerfiles
   - `docker-compose.yml`
   - deployment diagnostics implementation
   - Phase 17.5.1 documentation.
5. Do not redesign already-working versioning unless an actual GitHub Actions runtime failure proves it necessary.
6. Do not replace deterministic test infrastructure created in previous phases.
7. Do not introduce a second versioning mechanism.
8. Preserve local developer builds without requiring GitHub Actions environment variables.

All fixes must be evidence-driven.

---

# 4. Workstream A — Real GitHub Actions CI Execution

## 4.1 Execute CI on a Real Runner

Trigger the existing CI workflow on a real GitHub Actions runner.

Validate every job individually.

At minimum verify:

- repository checkout,
- .NET SDK setup,
- dependency restore,
- Release build,
- full test suite,
- Resilience tests,
- Stress tests,
- Load tests,
- ContainerIntegration tests,
- secret scanning,
- dependency scanning,
- Docker validation,
- Docker build,
- smoke test steps.

Do not mark a job successful merely because a similar command succeeded locally.

The actual workflow execution result is the source of truth.

---

## 4.2 Fix Runner-Specific Failures

If GitHub Actions produces failures caused by differences between local and CI environments, investigate the root cause.

Typical areas include:

- missing SDK versions,
- Linux vs Windows filesystem behavior,
- Docker permissions,
- BuildKit differences,
- Git metadata availability,
- shallow clone behavior,
- line-ending differences,
- executable permissions,
- temporary path assumptions,
- environment variable assumptions,
- compose version differences,
- GitHub Actions permission scopes,
- security scanner availability.

Do not add arbitrary retries.

Every retry must have a documented reason.

Do not hide failures using:

```yaml
continue-on-error: true
```

unless the step is explicitly optional and the reason is documented.

---

## 4.3 CI Runtime Evidence

The phase documentation must record:

- workflow run ID,
- commit SHA,
- branch,
- workflow duration,
- each job result,
- total tests executed,
- Docker validation result,
- security scan result.

Do not expose tokens, secrets, or credentials.

---

# 5. Workstream B — Container Registry Integration

## 5.1 Select Registry

Use GitHub Container Registry (GHCR) unless the repository already has a configured production registry.

Preferred registry:

```text
ghcr.io/<owner>/<repository>/api
ghcr.io/<owner>/<repository>/worker
```

Do not hardcode personal credentials.

Use GitHub Actions authentication and the minimum required permissions.

---

## 5.2 Required GitHub Permissions

Review workflow permissions explicitly.

Use least privilege.

For example, publishing to GHCR should require only the necessary:

```yaml
permissions:
  contents: read
  packages: write
```

Do not grant repository-wide write permissions unless required.

Document why every non-default permission exists.

---

## 5.3 Image Publishing Strategy

The release workflow must publish separate images for:

- API
- Worker

Required tags:

### Immutable release tag

```text
<semantic-version>
```

Example:

```text
0.1.0
```

### Commit traceability tag

```text
sha-<short-sha>
```

Example:

```text
sha-a1b2c3d
```

### Optional build tag

```text
build-<build-number>
```

Example:

```text
build-123
```

Do not use mutable tags as the only production deployment reference.

`latest` may exist as an optional convenience tag but must not be the only production deployment artifact.

---

# 6. Workstream C — Immutable Image Digest Verification

## 6.1 Capture Published Digest

After each image is pushed:

1. capture the registry digest,
2. record the digest as a release artifact,
3. verify the pushed digest can be pulled,
4. ensure the image metadata matches the intended release version.

Required metadata:

- SemanticVersion
- GitCommit
- GitBranch
- BuildNumber
- BuildTimestampUtc

---

## 6.2 OCI Labels

Add or validate standard OCI image labels.

Recommended labels:

```text
org.opencontainers.image.version
org.opencontainers.image.revision
org.opencontainers.image.created
org.opencontainers.image.source
```

Values must be generated from the same versioning source used by:

- .NET assembly metadata,
- deployment diagnostics,
- Docker image metadata.

There must be no conflicting independent version source.

---

## 6.3 Metadata Consistency Gate

Create an automated validation proving consistency between:

1. `VERSION`,
2. assembly informational version,
3. deployment diagnostics,
4. Docker image labels,
5. published image tag.

Example invariant:

```text
VERSION
   ↓
Assembly Version
   ↓
Deployment Diagnostics
   ↓
Docker OCI Labels
   ↓
Registry Image Tag
```

The release must fail if metadata diverges.

---

# 7. Workstream D — Published Image Smoke Test

Phase 17.5.1 validated locally built images.

This phase must validate the **actual registry artifact**.

The smoke test must:

1. build/publish the release image,
2. pull the image from the registry,
3. run Docker Compose using the pulled image,
4. not rebuild images locally,
5. execute health checks,
6. verify deployment diagnostics,
7. verify metadata.

This distinction is critical.

The following is insufficient:

```text
docker compose build
docker compose up
```

The required validation is:

```text
registry publish
    ↓
registry pull
    ↓
deployment from pulled artifact
```

---

## 7.1 Required Smoke Assertions

Validate:

### API

```text
/health/live  → 200
/health/ready → 200
```

### Worker

Verify:

```text
worker.live exists
```

Do not require readiness for container liveness.

### Deployment diagnostics

Verify the sanitized diagnostics endpoint reports:

- expected version,
- expected Git commit,
- expected branch when available,
- expected build number,
- expected image metadata.

### Database migration

Verify:

```text
migrator completed successfully
```

before:

```text
API started
Worker started
```

---

# 8. Workstream E — Deployment Artifact Mode

Extend Docker Compose so production deployment can explicitly consume published images.

Do not remove local development build support.

Preferred design:

### Local development

```text
docker compose up --build
```

may continue to build locally.

### Published release deployment

Introduce a production artifact mode using environment variables.

Example:

```text
AI_FACTORY_IMAGE_TAG=0.1.0
AI_FACTORY_IMAGE_REGISTRY=ghcr.io/<owner>/<repo>
```

Then:

```text
docker compose -f docker-compose.yml -f docker-compose.release.yml up -d
```

or an equivalent existing repository pattern.

Requirements:

1. Release deployment must use `image:` references.
2. Release deployment must not silently build local Dockerfiles.
3. Image tags must be explicit.
4. Prefer digest pinning for final production deployment.

---

# 9. Workstream F — Image Security Gate

## 9.1 Registry Artifact Scan

Scan the actual published image.

Do not only scan the locally built image.

The scan target must be the release artifact.

Example:

```text
ghcr.io/.../api:<version>
```

and:

```text
ghcr.io/.../worker:<version>
```

---

## 9.2 Vulnerability Policy

Define an explicit policy.

Example:

- Critical vulnerabilities → fail release.
- High vulnerabilities → fail release unless explicitly documented exception exists.
- Medium/Low → report.

Do not silently suppress vulnerabilities.

If exceptions are supported:

1. they must be documented,
2. they must have a reason,
3. they must be time-limited where possible.

---

# 10. Workstream G — SBOM Release Artifact

Phase 17.5.1 generates an SBOM.

This phase must ensure the SBOM is associated with the actual release.

Required:

- API SBOM,
- Worker SBOM,
- version metadata,
- commit metadata,
- release artifact retention.

Preferred:

Attach SBOM files to the GitHub Release.

If GitHub Release creation is already part of the release pipeline, associate the SBOM directly.

Do not include secrets or credentials.

---

# 11. Workstream H — Release Automation Safety

## 11.1 Tag Validation

The release workflow must validate that the Git tag matches the repository version.

Example:

```text
VERSION = 0.1.0
Git tag = v0.1.0
```

Acceptable normalization:

```text
v0.1.0 → 0.1.0
```

Reject:

```text
VERSION = 0.1.0
Git tag = v0.2.0
```

The release must fail before publishing.

---

## 11.2 Branch Policy

Release tags should only be accepted from the approved release branch/history.

Do not assume branch information is always directly available from tag events.

Use Git ancestry validation where necessary.

Example requirement:

```text
release tag commit must be reachable from main
```

Adjust only if the repository uses a different documented branch model.

---

## 11.3 Duplicate Release Protection

Prevent accidental republishing.

If a semantic version already exists:

- detect it,
- fail safely,
- do not overwrite the existing immutable release tag.

A new release should require a new version.

---

# 12. Workstream I — Automated Registry and Release Tests

Add automated tests where practical.

At minimum test:

1. version/tag normalization,
2. version mismatch rejection,
3. deployment metadata consistency,
4. image reference generation,
5. release configuration validation.

Tests must not require real credentials.

Separate:

- unit/configuration validation,
- real GitHub Actions runtime validation.

Do not mock away the entire release process and call that end-to-end verification.

---

# 13. Workstream J — Failure Injection for CI/CD Gates

Introduce safe validation of critical release failure paths.

At minimum verify:

### Version mismatch

Expected:

```text
release fails before publish
```

### Security gate failure

Expected:

```text
release does not publish
```

### Migration failure

Expected:

```text
deployment smoke test fails
```

### Registry pull failure

Expected:

```text
deployment validation fails
```

Do not intentionally corrupt production images.

Failure injection must use:

- controlled test tags,
- test environment variables,
- local/test workflow branches,
- disposable artifacts.

---

# 14. Documentation Requirements

Create:

```text
.ai/context/phase-17.5.2-cicd-runtime-validation-registry-artifact-integrity.md
```

Document:

## 1. Phase objective

## 2. Starting state from Phase 17.5.1

## 3. Real GitHub Actions validation

Include actual run IDs and results.

## 4. Runner-specific issues discovered

If any.

## 5. Container registry architecture

## 6. Image tagging strategy

## 7. OCI metadata strategy

## 8. Image digest verification

## 9. Published artifact smoke test

## 10. SBOM handling

## 11. Vulnerability gate policy

## 12. Deployment artifact mode

## 13. Failure injection results

## 14. Known limitations

## 15. Release checklist

## 16. Explicit GO / NO-GO decision

Be explicit about what was actually executed versus only inspected.

---

# 15. Required Validation Gates

Before declaring the phase complete:

## Build

```bash
dotnet build apps/AI.Factory.slnx -c Release
```

Required:

```text
0 errors
0 warnings
```

---

## Full test suite

Run:

```bash
dotnet test apps/AI.Factory.slnx -c Release --no-build --blame-hang-timeout 90s
```

All tests must pass.

---

## Docker configuration

Run:

```bash
docker compose config -q
```

and validate release deployment configuration.

---

## CI runtime

A real GitHub Actions CI run must complete successfully.

This phase cannot claim full CI validation based only on local YAML inspection.

---

## Release runtime

If registry credentials are available:

1. create a controlled test release or release candidate,
2. publish images,
3. capture digests,
4. pull published images,
5. deploy pulled artifacts,
6. run smoke tests.

If registry publishing cannot be performed because credentials/permissions are unavailable, document this as a hard limitation and return:

```text
NO-GO for production release automation
```

Do not falsely declare the registry workflow validated.

---

# 16. Production Release Checklist

Before GO:

### Source

- [ ] Clean build
- [ ] Full tests passing
- [ ] VERSION valid
- [ ] Git tag matches VERSION

### CI

- [ ] Real GitHub Actions CI execution passed
- [ ] All required quality gates passed
- [ ] No ignored mandatory failures

### Security

- [ ] Secret scan passed
- [ ] Dependency scan passed
- [ ] Published image scan passed
- [ ] SBOM generated

### Artifact

- [ ] API image published
- [ ] Worker image published
- [ ] Immutable digest captured
- [ ] OCI metadata validated
- [ ] Deployment diagnostics metadata matches artifact

### Deployment

- [ ] Published image pulled successfully
- [ ] Deployment does not rebuild locally
- [ ] Migrator completed successfully
- [ ] API healthy
- [ ] Worker liveness healthy
- [ ] Deployment diagnostics correct

---

# 17. Explicit GO / NO-GO Criteria

## GO

Only if all applicable gates are empirically verified:

1. Real GitHub Actions CI has executed successfully.
2. Release workflow has executed successfully.
3. Registry authentication works.
4. Images are actually published.
5. Published images are scanned.
6. Published images are pulled and deployed.
7. Image digest is captured.
8. Metadata consistency is verified.
9. No mandatory security gate fails.
10. Existing 17.4.x and 17.5.1 regression tests remain green.

---

## NO-GO

Return NO-GO if any of the following are true:

- workflow only validated locally,
- registry publish not actually tested,
- published artifact not pulled back,
- deployment only tested using locally built images,
- image version metadata is inconsistent,
- critical vulnerability gate fails,
- tag/version mismatch is not blocked,
- mandatory workflow jobs are skipped or ignored,
- production deployment can silently build an unverified local image.

---

# 18. Non-Goals

This phase does NOT require:

- Kubernetes,
- Helm,
- Terraform,
- multi-region deployment,
- PostgreSQL migration,
- distributed worker scaling,
- blue/green deployment,
- automatic production deployment.

Do not expand scope into infrastructure redesign.

The purpose of this phase is to prove:

> The exact artifact produced by CI is the artifact that can be securely identified, scanned, published, pulled, and deployed.

---

# 19. Final Report Format

At the end provide:

## Summary

## Files created

## Files modified

## Real GitHub Actions results

Include:

- workflow name,
- run ID,
- commit,
- duration,
- job outcomes.

## Registry results

Include:

- image references,
- tags,
- digests.

Do not expose credentials.

## Published artifact deployment results

## Security scan results

## Problems found

## Problems fixed

## Deferred limitations

## Explicit production release decision

Exactly one:

# GO

or

# NO-GO

The decision must be based only on empirically executed gates.

Do not infer production readiness from local validation alone.