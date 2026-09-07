# Phase 17.5.1 — CI/CD Release Automation, Supply-Chain Security & Production Deployment Validation

## 1. Objective

Give AI Factory an automated, auditable path from a merged commit to a versioned, scanned,
regression-gated release — without changing any orchestration/business logic. This phase adds
CI, release, and supply-chain tooling on top of the production readiness delivered in
17.4.1–17.4.13 (resilience, backup/restore, deployment health checks). It does not touch
Domain/Application/Infrastructure business logic.

## 2. Versioning model

- `VERSION` (repo root, plain text, e.g. `0.1.0`) is the single source of truth for the
  semantic version.
- `apps/Directory.Build.props` reads it and defines, for every project under `apps/`:
  - `SemanticVersion` — from `VERSION`, overridable via `-p:SemanticVersion=...`
  - `GitCommit` — default `local`, overridable via `-p:GitCommit=...`
  - `GitBranch` — default `local`, overridable via `-p:GitBranch=...`
  - `BuildNumber` — default `0`, overridable via `-p:BuildNumber=...`
  - `BuildTimestampUtc` — current UTC time at build, overridable via `-p:BuildTimestampUtc=...`
  - `Version` / `AssemblyVersion` / `FileVersion` / `InformationalVersion` are composed from
    the above (`InformationalVersion` = `{SemanticVersion}+{GitCommit}.{GitBranch}.{BuildNumber}`).
- At runtime, `DeploymentDiagnosticsService` (`apps/src/AI.Factory.Infrastructure/Diagnostics/DeploymentDiagnosticsService.cs`)
  independently reads `GitCommit`/`GitBranch`/`BuildNumber`/`BuildTimestamp` from environment
  variables (`BUILD_COMMIT`/`GIT_COMMIT`, `BUILD_BRANCH`/`GIT_BRANCH`, `BUILD_NUMBER`/`GITHUB_RUN_ID`,
  `BUILD_TIMESTAMP`/`BUILD_TIMESTAMP_UTC`), exposed via `/api/operations/deployment`. These are two
  separate mechanisms feeding the same values: MSBuild properties bake version info into the
  assembly at compile time; the container ENV vars (set from the same build args, see §4) let the
  running process report the same values without needing to read assembly metadata.

## 3. CI (`ci.yml`) vs Release (`release.yml`) — what runs where and why

| Stage | Trigger | What runs | Why |
|---|---|---|---|
| PR validation | `pull_request` (any branch) | `build-test`, `secret-scan`, `dependency-scan`, `docker-validate`, `container-smoke-test` — each once | Fast feedback on every change; must not block on flaky-timing 3x reruns |
| Main-branch validation | `push` to `main` | same jobs as PR validation | Confirms main stays green after merge; identical gate to PR, no extra rigor needed since nothing ships from main directly |
| Release certification | tag `v*.*.*` or `workflow_dispatch` | `stability-gate` (test suite x3 + all 4 category filters), `versioned-build`, `docker-release`, `sbom`, `container-scan`, `migration-failure-gate`, `backup-restore-gate`, `release-gate` | Only a tagged release needs the heavier, slower certification: repeated runs to catch flakiness, image scanning, SBOM, and destructive regression gates (migration failure, backup/restore) that are too slow/disruptive to run on every PR |

`ci.yml` intentionally does **one** full test pass — the repeated stability loop is release-only,
per the phase scope, so PR feedback stays fast.

## 4. Docker image tagging strategy

- **Dev / CI**: `docker-compose.yml` builds `ai-factory-api:${IMAGE_TAG:-latest}` and
  `ai-factory-worker:${IMAGE_TAG:-latest}` — defaults to `latest` for local `docker compose up`.
- **CI/release build args**: `docker-compose.yml`'s three `build:` stanzas (`migrator`, `api`,
  `worker`) each pass `BUILD_VERSION`, `GIT_COMMIT`, `GIT_BRANCH`, `BUILD_NUMBER`,
  `BUILD_TIMESTAMP_UTC` (env-substituted, all defaulted for local builds) into the matching
  `ARG`s in `Dockerfile.api`/`Dockerfile.worker`, which pass them to `dotnet build` as
  `-p:SemanticVersion=... -p:GitCommit=... -p:GitBranch=... -p:BuildNumber=...` and then
  re-expose them as `ENV BUILD_COMMIT/BUILD_BRANCH/BUILD_NUMBER/BUILD_TIMESTAMP_UTC` in the
  final stage, so the running container's `/api/operations/deployment` reports real values.
- **Release**: `release.yml`'s `docker-release` job tags each image with both the semantic
  version (`$SEMANTIC_VERSION`, e.g. `0.1.0`) and the short commit SHA (`$GIT_COMMIT`) — both
  immutable. No `latest` tag is produced by the release pipeline.
- **Rollback**: redeploy a previous immutable tag (semver or commit SHA) via
  `IMAGE_TAG=<previous-tag> docker compose up -d`. Because tags are immutable and never
  overwritten, rollback is a pure redeploy — no rebuild needed.

## 5. Supply-chain scanning

- **Dependency scan** (`dependency-scan` job, CI only): `dotnet list apps/AI.Factory.slnx
  package --vulnerable --include-transitive`. **Known limitation**: this CLI's output is
  human-readable text, not machine-parseable JSON/SARIF with per-package severity, so the gate
  treats *any* reported vulnerability as blocking (it greps for the literal phrase "has the
  following vulnerable packages", which only appears when findings exist) rather than bucketing
  by CRITICAL/HIGH/MEDIUM/LOW. A future iteration could parse `--format json` (if/when the CLI
  adds it) or switch to a SARIF-producing scanner to restore severity-based gating.
- **Secret scan** (`secret-scan` job, CI only): `gitleaks/gitleaks-action@v2` over full git
  history (`fetch-depth: 0`). No `.gitleaks.toml` allowlist was added — a grep of the test
  projects for entropy-looking secret literals (`ApiKey/AccessKey/Secret/Token/Password = "..."`)
  found none; the many `password`/`secret`/`token` matches in test files are assertion strings
  (e.g. `Assert.DoesNotContain("secret", ...)`) and field names, not real-looking credentials.
- **Container vulnerability scan** (`container-scan` job, release only):
  `aquasecurity/trivy-action@master` against both built images, `severity: CRITICAL,HIGH`,
  `exit-code: 1` — any CRITICAL/HIGH finding fails the release. Scan output uploaded as an
  artifact for audit.
- **SBOM** (`sbom` job, release only): `anchore/sbom-action@v0` generates a CycloneDX JSON SBOM
  per image, uploaded as an artifact named with the release's semantic version.

## 6. Migration-failure regression gate

`docker-compose.test-migration-failure.yml` overrides `migrator`'s command to
`["--migrate-only", "--simulate-failure"]` (the same effect as setting
`AI_FACTORY_MIGRATE_FAIL_TEST=1`; see `Program.cs` around the `--migrate-only` handling). This
must **never** appear in files that ship to production:
- `ProductionConfigurationSafetyTests.cs` (new, `apps/tests/AI.Factory.Api.Tests/Deployment/`)
  asserts `docker-compose.yml` and `apps/src/AI.Factory.Api/appsettings.Production.json` never
  contain `AI_FACTORY_MIGRATE_FAIL_TEST` or `--simulate-failure` (case-insensitive) — a
  fast, always-on regression guard that runs in every `dotnet test` invocation, CI or local.
- `release.yml`'s `migration-failure-gate` job independently exercises the real behavior: bring
  up the migrator with the failure override, assert it exits non-zero, and assert `api`/`worker`
  never reach a running/healthy state (since they `depends_on: migrator: condition:
  service_completed_successfully`).

## 7. Backup/restore release validation

`release.yml`'s `backup-restore-gate` job brings up an isolated stack
(`docker compose -p ai-factory-release-test up -d`) so it cannot collide with other release
jobs, waits for `api`/`worker` to report healthy, then execs the API container's CLI flags
directly (same binary the `migrator` service already uses this pattern for):
`docker compose exec -T api dotnet AI.Factory.Api.dll --backup-database`, followed by locating
the newest file under `/var/lib/ai-factory/backups` and running
`--restore-database /var/lib/ai-factory/backups/<file>`. Both must exit 0, and `api`/`worker`
must remain healthy afterward. The stack is torn down (`down -v`) in an `if: always()` step.

## 8. Deployment smoke test (CI)

`ci.yml`'s `container-smoke-test` job (after `docker-validate`): `docker compose up -d`, poll
(bounded, ~2 minutes) for `migrator` to exit 0, then for `api`/`worker` to report `healthy` via
`docker inspect --format='{{.State.Health.Status}}'`, then `curl -f
http://localhost:8080/health/live` and `/health/ready`, then confirm the worker liveness file
(`docker compose exec -T worker test -f /tmp/ai-factory-health/worker.live` — the worker image
is Debian-based (`mcr.microsoft.com/dotnet/runtime:10.0`), so `test` is available as a shell
builtin), then fetch `/api/operations/deployment` and grep (case-insensitive) for
`password|secret|apikey|Data Source=` to assert none appear. `docker compose down -v` always
runs as a final step (`if: always()`).

## 9. Rollback strategy

- **Application**: redeploy a previous immutable image tag (semver or commit SHA) — see §4.
  No rebuild required since tags are never overwritten.
- **Database**: forward-only EF Core migrations. There is no automated downgrade path — rolling
  back application code to an older version while the database has already been migrated
  forward is only safe if the older code tolerates the new schema, or if a database restore
  (from the backup/restore mechanism validated in §7) is performed alongside the application
  rollback. This is an intentional constraint carried over from prior phases, not new to
  17.5.1.
- **Configuration**: `appsettings.Production.json` and `docker-compose.yml` are version
  controlled; rollback is a git revert of the relevant commit plus a redeploy.

## 10. Release artifacts produced

- `.trx` test result files (CI and release, per job)
- Dependency scan output (`vulnerable-packages.txt`, CI)
- Docker images saved as `.tar` (release, consumed by `sbom`/`container-scan` jobs)
- CycloneDX SBOM JSON per image (release)
- Trivy scan output (release)
- All release-job artifacts are named with `$SEMANTIC_VERSION-$GIT_COMMIT` so re-running a
  release workflow never collides with a prior run's artifacts.

## 11. GO/NO-GO gate

`release.yml`'s `release-gate` job (`needs: [stability-gate, versioned-build, docker-release,
sbom, container-scan, migration-failure-gate, backup-restore-gate]`, `if: always()`) reads each
dependency's `needs.<job>.result`, writes a GO/NO-GO table to `$GITHUB_STEP_SUMMARY`, and exits
non-zero (failing the job itself, not just reporting) if any required job did not succeed.

## 12. Known Limitations

- **No container registry configured.** `release.yml` builds and tags images locally within
  the workflow run (via `docker save`/`docker load` between jobs) but never pushes to a
  registry — there is no registry/credentials configured for this repository yet. A real
  release currently produces validated, scanned, SBOM'd images that exist only inside the
  GitHub Actions run; wiring up a registry (GHCR, ACR, etc.) and a push step is a follow-up.
- **Dependency-scan severity bucketing.** As described in §5, `dotnet list package
  --vulnerable` output cannot be reliably parsed for per-severity gating with the tools
  available here, so the CI gate blocks on any finding rather than only CRITICAL/HIGH. This is
  more conservative (noisier) than the container scan's severity-aware gate.
- **Backup/restore gate exercises one backup/restore cycle, not data integrity.** The
  `backup-restore-gate` job proves the CLI flags exit 0 and the services stay healthy; it does
  not assert that specific rows/entities survive the round trip byte-for-byte. Deeper
  data-integrity assertions belong to `DatabaseBackupServiceTests.cs` (unit/integration level),
  not this release gate.
- **`.gitleaks.toml` was not added.** No entropy-looking secret literals were found in the test
  projects during this phase's review, so no allowlist file was created. If a future test
  fixture legitimately needs a synthetic-secret-shaped string, add `.gitleaks.toml` then, with
  a comment on each entry stating what it is, why it's safe, and a review-by date.
- **None of the workflow YAML in this phase has been executed in real GitHub Actions.** It was
  authored against the actual repo structure, tested locally only via `docker compose config -q`
  and `dotnet build`/`dotnet test` (see the phase completion report for exact commands/results).
  The first real run on GitHub Actions may surface environment differences (runner image
  versions, action version pinning, `GITHUB_STEP_SUMMARY` formatting) that a local review cannot
  catch.
