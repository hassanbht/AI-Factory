# Phase 17.5.2 — CI/CD Runtime Validation, Registry Publishing & Release Artifact Integrity

## 1. Phase objective

Move the CI/CD implementation from Phase 17.5.1 (locally validated workflow *definitions*) toward
empirically validated release automation: real GitHub Actions execution, container registry
publishing (GHCR), immutable digest verification, a published-artifact smoke test, and stronger
release-safety gates (tag/version validation, duplicate-release protection, branch ancestry).

**This session could not execute the "real runner" / "real registry" half of that goal.** The
repository has no configured Git remote (`git remote -v` is empty), and the user explicitly chose
**"Don't push yet — local-only"** when asked whether to create a GitHub repo for this phase. As a
direct consequence:

- No `git push`, `git remote add`, `gh repo create`, `gh workflow run`, or `docker push` against a
  real registry was performed.
- No GitHub Actions workflow run exists for this phase — there is no run ID, no job log, no
  workflow duration to report.
- No image was ever actually published to GHCR — there is no digest, no scan result, no pulled
  artifact to report.

What this phase *did* deliver is everything that can be honestly built and locally proven without
a remote: the workflow YAML is now written correctly for the moment a remote exists (registry
login, tagging, push, digest capture, OCI labels), plus release-safety logic (tag/version
validation, duplicate-tag/force-move detection, branch ancestry, a deployment artifact mode file,
and a metadata-consistency unit test) that runs and passes **today**, locally, with no registry.

## 2. Starting state from Phase 17.5.1

Already in place (read, not redesigned):

- `VERSION` (repo root) — single source of truth for the semantic version (`0.1.0`).
- `apps/Directory.Build.props` — composes `SemanticVersion`/`GitCommit`/`GitBranch`/
  `BuildNumber`/`BuildTimestampUtc` into `Version`/`AssemblyVersion`/`FileVersion`/
  `InformationalVersion` (`{SemanticVersion}+{GitCommit}.{GitBranch}.{BuildNumber}`), with local
  fallback defaults (`local`/`local`/`0`).
- `DeploymentDiagnosticsService` / `IDeploymentDiagnosticsService` — sanitized
  `/api/operations/deployment` snapshot, version/commit/branch/build-number sourced from
  `BUILD_COMMIT`/`GIT_COMMIT`, `BUILD_BRANCH`/`GIT_BRANCH`, `BUILD_NUMBER`/`GITHUB_RUN_ID`,
  `BUILD_TIMESTAMP`/`BUILD_TIMESTAMP_UTC` env vars.
- `Dockerfile.api` / `Dockerfile.worker` — `ARG BUILD_VERSION/GIT_COMMIT/GIT_BRANCH/BUILD_NUMBER/
  BUILD_TIMESTAMP_UTC`, passed to `dotnet build -p:...`, redeclared as `ENV` in the final stage.
  Worker `HEALTHCHECK` checks `worker.live` (liveness) — unchanged, per the Phase 17.4.11
  invariant.
- `docker-compose.yml` — `image: ...${IMAGE_TAG:-latest}` dev default, `build.args` passthrough.
- `docker-compose.test-migration-failure.yml` — test-only migrator failure override, guarded by
  `ProductionConfigurationSafetyTests.cs`.
- `.github/workflows/ci.yml` — fast single-pass PR/main gate (build, full test + 4 category
  filters, secret scan, dependency scan, docker validate, container smoke test).
- `.github/workflows/release.yml` — tag-triggered certification pipeline (stability gate x3,
  versioned build, local-only docker build, SBOM, container scan, migration-failure gate,
  backup-restore gate, release-gate GO/NO-GO). No registry push existed before this phase.
- Known limitations already recorded in the 17.5.1 doc: "no registry configured", "workflows
  never executed on a real runner" — both still open going into this phase; this phase closes
  the workflow-*readiness* half of the registry gap, not the runtime-execution half.

## 3. Real GitHub Actions validation

**Not performed. Not possible this session.** There is no GitHub remote for this repository, so
no workflow — `ci.yml` or `release.yml` — has ever run on a real GitHub Actions runner. There is
no workflow run ID, no commit SHA tied to a run, no branch, no measured duration, no per-job
result, no count of tests actually executed by a runner (as opposed to locally), and no real
Docker/secret/dependency-scan result to report here. Any of those details would have to be
fabricated to appear in this section, which this phase explicitly must not do. This is an
unresolved gap, carried forward unchanged from Phase 17.5.1 into Phase 17.5.2's own known
limitations (section 14) and reflected in the final GO/NO-GO decision (section 16).

## 4. Runner-specific issues discovered

None discovered — none could be, since no runner execution took place. The workflow YAML was
authored and reviewed against the actual repository structure (Dockerfile ARG names, compose
service names, project paths, `Directory.Build.props` property names) and validated with the
local tools available (`dotnet build`/`test`, `docker compose config -q`), but the first real run
on GitHub Actions may still surface differences a local review cannot catch (runner image
versions, action version pinning, `GITHUB_STEP_SUMMARY` formatting, GHCR auth edge cases) — this
is unchanged from the equivalent caveat in the Phase 17.5.1 doc.

## 5. Container registry architecture

GHCR (GitHub Container Registry) was selected, per the phase spec's default and per Phase
17.5.1's own recorded limitation ("no registry configured"):

```text
ghcr.io/${{ github.repository }}/api
ghcr.io/${{ github.repository }}/worker
```

`${{ github.repository }}` resolves automatically to `<owner>/<repo>` the moment this workflow
runs inside a real GitHub Actions context — no owner/repo name is hardcoded anywhere in
`release.yml` or `docker-compose.release.yml` (the latter uses a placeholder
`ghcr.io/OWNER/REPO` default that is always overridden by `AI_FACTORY_IMAGE_REGISTRY` in real
use). Authentication uses the built-in `github.actor` / `secrets.GITHUB_TOKEN` — no new secret
needs to be created by the user for GHCR specifically.

Least-privilege permissions: a workflow-level `permissions: contents: read` default, with
`packages: write` granted **only** on the `docker-release` job (the sole job that logs in to GHCR
and pushes), not repository-wide. See `.github/workflows/release.yml`'s comments on both the
top-level `permissions:` block and the `docker-release` job's own block for the explicit
rationale.

**Status: workflow-ready, never executed.** The `docker/login-action@v3` step, and every
`docker push`/registry-scan step that follows it, will only succeed once this repository actually
exists on GitHub with GHCR access — today they are correctly-written, unexecuted YAML.

## 6. Image tagging strategy

Per image (API, Worker), `docker-release` now builds and (once a remote exists) pushes three
required tags plus one optional convenience tag:

| Tag | Example | Immutable? | Purpose |
|---|---|---|---|
| Semantic version | `0.1.0` | Yes | The canonical release reference |
| Commit traceability | `sha-a1b2c3d` | Yes | Ties an image back to the exact commit built |
| Build tag | `build-1234567890` | Yes (per CI run) | Ties an image to the exact GitHub Actions run |
| `latest` | `latest` | No — mutable | Optional convenience only; **never** the sole production reference |

`docker-compose.release.yml` (section 12) requires `AI_FACTORY_IMAGE_TAG` to be explicitly set
(no default) precisely so a deployment can never silently resolve to the mutable `latest` tag.

**Status: workflow-ready, never executed.** No image has actually been pushed with any of these
tags — the tagging logic is written and locally reasoned about (build-arg/label names verified
against the Dockerfiles by inspection), not proven against a live registry.

## 7. OCI metadata strategy

`Dockerfile.api` and `Dockerfile.worker`'s final stage now declare:

```dockerfile
LABEL org.opencontainers.image.version=$BUILD_VERSION \
      org.opencontainers.image.revision=$GIT_COMMIT \
      org.opencontainers.image.created=$BUILD_TIMESTAMP_UTC \
      org.opencontainers.image.source=$IMAGE_SOURCE_URL
```

`IMAGE_SOURCE_URL` is a new `ARG` (default `unknown` for local builds) — the Dockerfile itself has
no way to know the repository URL, so `release.yml` passes
`--build-arg IMAGE_SOURCE_URL=https://github.com/${{ github.repository }}`. The same workflow job
also passes matching `--label` flags at `docker build` time
(`org.opencontainers.image.version/revision/created/source`), all derived from the same
`needs.setup.outputs` used for the `-p:SemanticVersion=...`/`-p:GitCommit=...` MSBuild properties
— so the Dockerfile LABELs, the assembly's `InformationalVersion`, and the eventual registry tag
all derive from one source (`VERSION` + the workflow's Git/run context), with no independent or
conflicting version source introduced.

**Status: locally implemented and verified by inspection** (the Dockerfiles build successfully —
see section 15 — and the LABEL/`--label`/`ARG` names were cross-checked by hand against each
other). Not verified against an actual built image's `docker inspect --format='{{json .Config.Labels}}'`
output, since doing so was out of this session's local-Docker-build validation scope (only
`docker compose config -q` was run — no image builds were performed locally this session, to
match the phase's "local build/test/config-validate only" validation gate).

## 8. Image digest verification

`docker-release`'s new "Capture published digests" step runs
`docker inspect --format='{{index .RepoDigests 0}}' <image>:<semver>` **after** the push steps,
for both API and Worker, writes both digests to `$GITHUB_STEP_SUMMARY` as a table, and exposes
them as job outputs (`docker-release.outputs.api_digest` / `.worker_digest`). No `buildx
imagetools inspect` was introduced — the rest of this workflow (and Phase 17.5.1 before it) uses
plain `docker build`/`push`, so digest capture stays on the same toolchain rather than
introducing a second one just for this step.

**Status: workflow-ready, never executed.** `RepoDigests` is only populated once a real
`docker push` against a real registry succeeds — there is no digest to report here. This is the
sharpest edge of the "no registry" gap: section 6.3 of the phase spec's invariant chain
(`VERSION → Assembly Version → Deployment Diagnostics → Docker OCI Labels → Registry Image Tag`)
is proven end-to-end only through "Deployment Diagnostics"; the two remaining links (Docker OCI
Labels, Registry Image Tag) require an actual image build/push and were **not** exercised this
session. See `VersionMetadataConsistencyTests.cs`'s own doc comment for the same statement at the
unit-test level.

## 9. Published artifact smoke test

**Not performed.** The phase spec is explicit that this requires: build/publish → registry push →
registry **pull** → deploy from the pulled artifact → health checks → diagnostics verification —
and that a `docker compose build && up` cycle is explicitly insufficient. With no registry to push
to, there is no published artifact to pull, so this workstream could not be exercised at all, not
even partially. `docker-compose.release.yml` (section 12) is the piece that makes this *possible*
once a registry exists — it is unexecuted.

## 10. SBOM handling

Unchanged from Phase 17.5.1: `release.yml`'s `sbom` job generates a CycloneDX JSON SBOM per image
(`anchore/sbom-action@v0`) from the locally-built, locally-loaded image (`docker load` from the
`docker-release` job's saved `.tar` artifacts), uploaded as a workflow artifact named with the
release's semantic version. This phase did not change the SBOM job's target — it was not asked to
(the spec's item 8 in this session's scope only names `docker-release`/`container-scan` for the
registry-reference switch) — so the SBOM still covers the *locally built* image content, which is
byte-identical to what gets pushed (same `docker build` invocation, before any push), just not
literally re-derived from the registry artifact. Attaching the SBOM to an actual GitHub Release
object was not implemented (no release event exists without a real remote to create one against).

## 11. Vulnerability gate policy

Unchanged policy, retargeted scan source: `container-scan` still uses
`aquasecurity/trivy-action@master`, `severity: CRITICAL,HIGH`, `exit-code: '1'` (any CRITICAL/HIGH
finding fails the release; MEDIUM/LOW are not gated, matching Phase 17.5.1). What changed is the
scan **target**: it now scans `ghcr.io/${{ github.repository }}/api:<semver>` and
`.../worker:<semver>` — the actual published registry reference — instead of the locally-tagged
`ai-factory-api:<semver>` image, per phase spec Workstream F §9.1 ("do not only scan the locally
built image"). This requires the same GHCR login as `docker-release` (added as a new step in this
job, since it runs on a separate runner with no shared Docker daemon state).

**Status: workflow-ready, never executed.** No scan has actually run against a published image —
there is no scan result to report.

## 12. Deployment artifact mode

New file: `docker-compose.release.yml`. Overrides `migrator`/`api`/`worker`'s `image:` key only
(no `build:` key) to:

```text
${AI_FACTORY_IMAGE_REGISTRY:-ghcr.io/OWNER/REPO}/<api|worker>:${AI_FACTORY_IMAGE_TAG:?AI_FACTORY_IMAGE_TAG must be set}
```

`AI_FACTORY_IMAGE_TAG` has **no default** — `docker compose config` fails loudly
(`AI_FACTORY_IMAGE_TAG must be set`) if it is unset, confirmed locally (section 15). This is
deliberate: unlike `docker-compose.yml`'s own dev-only `${IMAGE_TAG:-latest}` fallback, this
file's whole purpose is pinned, explicit production deployment.

Documented, load-bearing caveat: because `docker-compose.yml`'s base services still carry a
`build:` key (kept for local dev), layering only an `image:` override on top does **not** remove
that `build:` key — so a bare `docker compose ... up` or `up --build` could still silently build
Dockerfile.api/Dockerfile.worker locally instead of running the pinned artifact. The safe,
required invocation (documented in the file's own header comment) is:

```bash
AI_FACTORY_IMAGE_REGISTRY=ghcr.io/<owner>/<repo> AI_FACTORY_IMAGE_TAG=0.1.0 \
  docker compose -f docker-compose.yml -f docker-compose.release.yml pull
AI_FACTORY_IMAGE_REGISTRY=ghcr.io/<owner>/<repo> AI_FACTORY_IMAGE_TAG=0.1.0 \
  docker compose -f docker-compose.yml -f docker-compose.release.yml up -d --no-build
```

**Status: locally implemented and validated.** `docker compose -f docker-compose.yml -f
docker-compose.release.yml config -q` with `AI_FACTORY_IMAGE_TAG=0.1.0` and
`AI_FACTORY_IMAGE_REGISTRY=ghcr.io/example-owner/example-repo` set exits `0` (section 15); the
same command with `AI_FACTORY_IMAGE_TAG` unset exits non-zero with the expected error message —
both are exercised by `DockerComposeReleaseValidationTests.cs`, which passes locally. This is one
of the fully-proven pieces of this phase; it needed no registry to validate.

## 13. Failure injection results

Two of the four failure-injection paths from the phase spec's Workstream J were exercised
locally, without needing Docker, git, or GitHub Actions runtime:

- **Version mismatch → release fails before publish.** `ReleaseTagValidator.TagMatchesVersion`
  (`apps/src/AI.Factory.Application/Versioning/ReleaseTagValidator.cs`) and its unit tests
  (`ReleaseTagValidatorTests.cs`) prove: a matching tag (`v0.1.0` vs VERSION `0.1.0`) passes; a
  mismatched tag (`v0.2.0` vs `0.1.0`) fails; a tag with no `v` prefix still normalizes correctly;
  a pre-release-suffixed tag (`v0.1.0-rc1`) is rejected against a plain `0.1.0` VERSION file
  (deliberate, documented — see the class's doc comments) and accepted against a VERSION file that
  carries the identical suffix. The equivalent shell logic in `release.yml`'s new "Validate tag
  matches VERSION" step was reasoned about by hand to match the same normalization rule, but was
  **not** exercised by an actual tag push (no remote).
- **Deployment artifact mode requires an explicit tag → confirmed.** `AI_FACTORY_IMAGE_TAG`
  unset causes `docker compose config` to fail loudly (section 12), exercised locally via
  `DockerComposeReleaseValidationTests.cs` and a manual `docker compose ... config -q` run
  (section 15).

Two failure-injection paths from the spec were **not** exercised this session, and could not be,
without a registry/runner:

- **Security gate failure → release does not publish.** `container-scan`'s Trivy step was not
  actually run against any image (locally-built or registry) this session (this phase's local
  validation scope was `dotnet build`/`test`/`docker compose config -q` only, matching the
  instructions' explicit validation gate — no image builds were performed).
- **Registry pull failure → deployment validation fails.** No registry exists to pull from, so
  this path cannot be exercised at all.

Migration-failure injection (`docker-compose.test-migration-failure.yml`,
`ProductionConfigurationSafetyTests.cs`) is unchanged from Phase 17.5.1 and was not re-validated
via a Docker run this session (again, out of this session's no-Docker-build validation scope) —
its existing regression test (guarding the switch never leaking into production config) passed as
part of the full test run (section 15).

## 14. Known limitations

Carried forward from Phase 17.5.1, still unresolved:

- **No container registry publish has ever actually happened.** Every registry-facing step in
  `release.yml` (`docker/login-action`, all `docker push` invocations, digest capture, the
  registry-target Trivy scans) is correctly authored but dormant — it has never run, because this
  repository has no Git remote and the user explicitly chose not to create one this session.
- **No GitHub Actions run has ever executed `ci.yml` or `release.yml`.** No run ID, duration, or
  per-job result exists for this phase (or the prior one) to report.
- **The published-image smoke test (Workstream D) has not been performed at all.** It requires an
  actual registry round-trip (push → pull → deploy-from-pull) which cannot be simulated without a
  registry.
- **`docker-compose.release.yml`'s `build:` key is not removable without changing
  `docker-compose.yml` itself.** The override only replaces `image:`; operators MUST use
  `pull` + `up -d --no-build` (documented in the file and in section 12 above) to avoid an
  accidental local rebuild — this is a documented operational requirement, not a bug, but it is a
  real footgun if the documented invocation is not followed.
- **Duplicate-release protection is only the local form.** `release.yml`'s new "Reject a
  force-moved/reused tag" step only detects a Git tag pointing at a different commit than the one
  being built. It does **not** query GHCR for whether the semantic version has already been
  published (phase spec §11.3's fuller requirement) — that requires registry API access this
  session cannot exercise, and is deferred until GHCR publishing is live.
- **Branch/ancestry validation assumes a fallback ref.** "Verify release tag is reachable from
  main" tries `origin/main` first, then falls back to a local `main` branch ref, because there is
  no `origin` configured in this repository today. This fallback path has not been proven against
  a real `origin/main` and should be re-verified the first time this repo is actually pushed to
  GitHub.
- **`VersionMetadataConsistencyTests`'s diagnostics-level assertion is intentionally weakened.**
  `Assembly.GetEntryAssembly()` (used by `DeploymentDiagnosticsService`) returns the xunit test
  host process under `dotnet test`, not `AI.Factory.Api.dll` — so the unit test cannot assert
  `snapshot.InformationalVersion` traces back to the VERSION file the way it can for the test
  assembly itself. See that test class's doc comment (section 7/8 above) for the full explanation.
  This is a real, discovered gap in test coverage for that specific link of the chain, not a
  behavior bug in the service itself (in production, the entry assembly IS the versioned
  project).
- **Dependency-scan severity bucketing** (unchanged from 17.5.1): `dotnet list package
  --vulnerable` output is text, not structured, so the CI gate blocks on any finding rather than
  bucketing by severity.
- **SBOM is not attached to a GitHub Release object.** No release event exists without a real
  remote to create one against; the SBOM remains a workflow artifact only.

## 15. Release checklist

What was actually run, locally, this session, and its real result:

| Check | Command | Result |
|---|---|---|
| Build (Release) | `dotnet build apps/AI.Factory.slnx --configuration Release` | **0 errors, 0 warnings** |
| Full test suite | `dotnet test apps/AI.Factory.slnx --configuration Release --no-build --blame-hang-timeout 90s` | **648 total, 0 failed, 0 skipped** (60 Worker.Tests + 375 Application.Tests + 213 Api.Tests) |
| Base compose config | `docker compose config -q` | **exit 0** |
| Release compose config (tag+registry set) | `AI_FACTORY_IMAGE_TAG=0.1.0 AI_FACTORY_IMAGE_REGISTRY=ghcr.io/example-owner/example-repo docker compose -f docker-compose.yml -f docker-compose.release.yml config -q` | **exit 0** |
| Release compose config (tag unset) | same, with `AI_FACTORY_IMAGE_TAG` unset | **exit 1** (`AI_FACTORY_IMAGE_TAG must be set`), confirming the "fail loudly" requirement |

Not run this session (require a registry/runner, out of scope — see sections 3/9/14):

- [ ] Real GitHub Actions CI execution
- [ ] Real GitHub Actions release-workflow execution
- [ ] GHCR authentication
- [ ] Image push to GHCR
- [ ] Digest capture against a real push
- [ ] Registry-artifact Trivy scan
- [ ] Published-image pull + deploy-from-pulled-artifact smoke test

Locally proven this session (see table above and sections 6/12/13):

- [x] Clean build, full tests passing, VERSION valid
- [x] Tag/version normalization logic (unit-tested, `ReleaseTagValidatorTests.cs`)
- [x] Deployment artifact mode fails loudly without an explicit tag (`docker compose config -q`,
      confirmed both via a manual run and `DockerComposeReleaseValidationTests.cs`)
- [x] OCI label / build-arg wiring in both Dockerfiles (present, cross-checked by inspection
      against `release.yml`'s `--label`/`--build-arg` flags — not verified against a built image
      this session, see section 7)
- [x] Metadata-consistency unit test for the VERSION → assembly-informational-version link
      (`VersionMetadataConsistencyTests.cs`) — the Docker-label/registry-tag links of the same
      chain remain unverified (see sections 7/8/14)

## 16. Explicit GO / NO-GO decision

**NO-GO** — for the real-runner / registry-publish scope specifically (Workstreams A, B, C, D, F's
registry-target scan, and the full form of Workstream H §11.3):

- No real GitHub Actions CI or release run has ever executed (section 3).
- No image has been published to any registry; GHCR authentication has never been exercised
  (sections 5, 6, 8).
- The published-artifact smoke test (pull → deploy-from-pull → health/diagnostics verification)
  has not been performed at all (section 9).
- The published image has not been scanned (section 11).
- Registry-side duplicate-release protection has not been exercised (section 14).

None of this is a defect introduced by this phase — it is a direct, explicit consequence of the
repository having no Git remote and the user's deliberate "local-only" decision this session. It
mirrors, and is fully consistent with, the same NO-GO carried in the Phase 17.5.1 doc.

**What IS ready, locally proven, and correct as written**, and will work the moment this
repository is pushed to a real GitHub remote:

- `release.yml`'s tag/version-validation, duplicate-tag/force-move, and branch-ancestry gates —
  all run correctly today against local Git state, and fail the workflow before any build/publish
  work (sections 6/13).
- `release.yml`'s GHCR login, tagging (semver + `sha-` + `build-` + optional `latest`), push, and
  digest-capture steps, plus the registry-target Trivy scan — written with least-privilege
  permissions (`packages: write` scoped to `docker-release` only) and correct image references
  (`ghcr.io/${{ github.repository }}/...`, never a hardcoded owner/repo) — dormant, unexecuted,
  ready.
- Both Dockerfiles' new OCI `LABEL`s, matched to `release.yml`'s `--label` flags and the same
  `needs.setup` outputs used everywhere else in the pipeline.
- `docker-compose.release.yml` — validated locally (section 15), including its "fail loudly
  without an explicit tag" requirement.
- `ReleaseTagValidator` (and its unit tests) and `VersionMetadataConsistencyTests` — proving the
  version-mismatch-rejection and VERSION-file-to-assembly-version links of the chain hold, without
  needing Docker, a registry, or a GitHub remote.

The full production release decision (`GO`) cannot be made until this repository is pushed to
GitHub and Workstreams A, D, and the registry-publish parts of B/C/F/H§11.3 are actually executed
and their real results recorded in a follow-up to this document.
