# Phase 17.4.10 — Production Operational Runbook

## 1. Overview & Operational Architecture

This runbook outlines day-to-day operations, monitoring procedures, incident response actions, failure diagnosis, and recovery workflows for the AI Factory platform in production.

Platform Architecture:
- `migrator`: Dedicated ephemeral container running schema migrations before services start.
- `api`: ASP.NET Core web service exposing REST API, health probes, and protected operational diagnostics.
- `worker`: Background processing worker executing AI workflows, lease sweeps, and agent runs.
- Storage: Persistent Docker volume for SQLite (`ai_factory_production_data`) and Agent Workspace (`ai_factory_production_workspace`).

---

## 2. Normal Operations

### 2.1 Starting the Platform

Always use Docker Compose to guarantee dependency ordering (migration -> services):

```powershell
# 1. Ensure required environment variables are set in .env
# OPENROUTER_API_KEY=your_key_here
# OPERATIONAL_ACCESS_KEY=your_operational_key_here

# 2. Build and start stack in detached mode
docker compose up -d

# 3. Observe startup logs
docker compose logs -f --tail=50
```

Expected startup sequence:
1. `migrator` starts, applies any pending migrations, and exits with code `0`.
2. `api` and `worker` start concurrently once `migrator` finishes successfully.
3. `api` becomes healthy within 15 seconds (passes `/health/live` and `/health/ready`).
4. `worker` creates `/tmp/ai-factory-health/worker.ready` and begins polling.

### 2.2 Stopping the Platform

Graceful shutdown ensures in-flight AI workflow leases are released back to `Pending` and HTTP requests drain cleanly:

```powershell
# Gracefully stop containers with 30s-45s shutdown budget
docker compose down
```

### 2.3 Health Probing & Monitoring

```powershell
# Check container status
docker compose ps

# API Liveness (is process responsive?)
curl -i http://localhost:8080/health/live

# API Readiness (is DB connected and accessible?)
curl -i http://localhost:8080/health/ready

# Worker Probe Inspection
docker compose exec worker sh -c "ls -la /tmp/ai-factory-health"
# - worker.live: Process is alive (present in Starting, Ready, Degraded, Stopping)
# - worker.ready: Worker is actively accepting claims (present only in Ready)
# - worker.degraded: Worker has sustained dependency failures (present only in Degraded)
#
# Dockerfile.worker's HEALTHCHECK intentionally tests worker.live only (liveness),
# not worker.ready — a Degraded worker is alive and must not be restarted by the
# container orchestrator over a transient dependency outage. Use worker.ready
# directly (as above) when an orchestrator needs readiness for traffic routing.
```

### 2.4 Inspecting Operational Diagnostics

Protected operational endpoints require `X-Operational-Key` header if `Security:RequireAccessKey` is enabled:

```powershell
$headers = @{ "X-Operational-Key" = $env:OPERATIONAL_ACCESS_KEY }

# 1. Operational Metrics Snapshot (live counters & timers)
Invoke-RestMethod -Uri http://localhost:8080/api/operations/metrics -Headers $headers

# 2. Database Diagnostics (connection status, file size, pool stats)
Invoke-RestMethod -Uri http://localhost:8080/api/operations/database -Headers $headers

# 3. Configuration Diagnostics (sanitized loaded configuration)
Invoke-RestMethod -Uri http://localhost:8080/api/operations/configuration -Headers $headers

# 4. Deployment Diagnostics (versions, environment, runtime details)
Invoke-RestMethod -Uri http://localhost:8080/api/operations/deployment -Headers $headers
```

---

## 3. Failure Operations & Troubleshooting

### 3.1 Scenario: API Unavailable (HTTP 500 / 502 / Connection Refused)
- **Symptoms**: Health check fails, client requests return 502 Bad Gateway or connection refused.
- **Diagnostic Steps**:
  ```powershell
  docker compose logs --tail=100 api
  docker compose ps api
  ```
- **Resolution**:
  - If container exited due to missing secret: verify `.env` contains valid `OPENROUTER_API_KEY`.
  - If crashed: restart container via `docker compose restart api`.

### 3.2 Scenario: Worker Degraded (`WorkerLifecycleState.Degraded`)
- **Symptoms**: `worker.degraded` file exists, `worker.ready` is missing, `worker.live` is present. Metrics show `WorkerDegraded > 0`.
- **Root Cause**: 3 consecutive poll failures occurred (e.g. database busy or transient network error).
- **Behavior**: The worker remains alive and continues polling with backoff.
- **Resolution**:
  - The worker **automatically recovers** to `Ready` once the underlying dependency succeeds.
  - If degradation persists > 5 minutes, inspect worker logs:
    ```powershell
    docker compose logs --tail=100 worker
    ```
  - Check database availability and disk space.

### 3.3 Scenario: Worker Faulted (`WorkerLifecycleState.Faulted`)
- **Symptoms**: Both `worker.ready` and `worker.live` are absent. Docker container marked unhealthy.
- **Root Cause**: Unrecoverable exception occurred in background service host.
- **Resolution**:
  ```powershell
  docker compose logs --tail=200 worker
  docker compose restart worker
  ```

### 3.4 Scenario: Circuit Breaker Open (HTTP 503 `service_unavailable`)
- **Symptoms**: API or worker requests to external AI provider fail fast with `503 Service Unavailable`, code `"service_unavailable"`. Metrics show `CircuitOpened > 0`.
- **Behavior**: System protects caller threads and prevents cascading failure. Permitted probe requests test the provider in `HalfOpen` state.
- **Resolution**:
  - Check external provider status (e.g. status.openrouter.ai).
  - System automatically recovers to `Closed` state once provider is restored.

### 3.5 Scenario: Bulkhead Saturated (HTTP 429 `too_many_requests`)
- **Symptoms**: Client requests rejected with HTTP 429 and ProblemDetails code `"too_many_requests"`.
- **Root Cause**: Concurrent requests exceeded execution slots and waiting queue capacity.
- **Resolution**:
  - Advise callers to respect `Retry-After` header.
  - If sustained, review load patterns and tune `BulkheadOptions` capacity limits in configuration.

### 3.6 Scenario: Database Locked / Unavailable (HTTP 503 `database_unavailable`)
- **Symptoms**: API returns 503 `database_unavailable`. SQLite reports `busy` or `locked`.
- **Resolution**:
  - Check for long-running uncommitted transactions or external file access.
  - Ensure volume mount is on high-performance local disk, not networked NFS.
  - Automatic exponential retries absorb transient locks; persistent locking requires API & Worker restart.

### 3.7 Scenario: Migration Failure
- **Symptoms**: `migrator` container exits with non-zero code. API and Worker refuse to start.
- **Resolution**:
  ```powershell
  docker compose logs migrator
  ```
  - Review migration error. Do not force container startup.
  - Follow Disaster Recovery Runbook to restore database from backup if schema was partially altered.

---

## 4. Recovery Procedures

### 4.1 Restart API Service
```powershell
docker compose restart api
curl -f http://localhost:8080/health/ready
```

### 4.2 Restart Worker Service
```powershell
docker compose restart worker
docker compose exec worker sh -c "test -f /tmp/ai-factory-health/worker.ready && echo 'OK'"
```

### 4.3 Restart Complete Stack
```powershell
docker compose down
docker compose up -d
docker compose ps
```

### 4.4 Recovery After Dependency Outage
When an external AI provider recovers:
1. Circuit Breakers automatically transition from `Open` to `HalfOpen`.
2. Successful probe calls close the circuit and restore normal request execution.
3. No manual service restart is required.

### 4.5 Secret Rotation

`OPENROUTER_API_KEY` (AI provider):
1. Generate the new key in the OpenRouter dashboard; do not revoke the old key yet.
2. Update `.env` (or the secret manager entry it is sourced from) with the new value.
3. Rolling-restart the services that read it: `docker compose up -d --no-deps api worker` — this recreates
   both containers so the new environment value is loaded. There is no in-process reload; a restart is required.
4. Verify: `curl -f http://localhost:8080/health/ready` and confirm a real AI call succeeds (check `/api/operations/resilience`
   for circuit state — it must not be `Open` for the AI dependency).
5. Only after step 4 succeeds, revoke the old key in the OpenRouter dashboard.

`OPERATIONAL_ACCESS_KEY` (operational endpoint access key):
1. Generate a new random key (`openssl rand -base64 32` or equivalent).
2. Update `.env`, then `docker compose up -d --no-deps api worker` to pick it up.
3. Verify with the new key: `curl -H "X-Operational-Key: <new_key>" http://localhost:8080/api/operations/metrics` returns 200.
4. Discard the old key value — it is invalid the moment the new environment value is loaded (single active key, no grace overlap).

Database credentials — this deployment uses SQLite (file-based, no server credential). If a future
deployment moves to a networked provider (PostgreSQL, SQL Server), rotation must follow that provider's
credential rotation procedure and update `DatabaseOptions:ConnectionString` the same rolling-restart way
described above; this is not yet implemented or tested against this codebase.

### 4.6 Migration Rollback Policy

EF Core migrations applied by the migrator are **forward-only**. There is no supported "downgrade the
production database" command in this operational model.

- Do not run `dotnet ef database update <previous-migration>` against production to "roll back" — EF Core's
  down-migrations are not exercised in this codebase's test suite and are not a verified rollback path.
- A failed migration (migrator container exits non-zero, or `docker compose logs migrator` shows an
  exception) requires investigation, not a downgrade attempt: read the exception, confirm whether any DDL
  applied partially, and fix forward with a new migration once the root cause is understood.
- If production data integrity is already compromised by a bad migration, the supported recovery path is
  the Disaster Recovery Runbook's restore procedure (restore the pre-migration backup, then re-apply a
  corrected migration) — not a schema downgrade.
- Release rollback (reverting the application container to the previous image tag) is safe only when that
  previous image's expected schema is a subset of what the database now has — i.e., only when the migration
  itself was additive and backward-compatible. Verify this on a case-by-case basis before rolling back a release.

### 4.7 Backup & Restore (Phase 17.4.12)

Concrete implementation of the strategy described in the Disaster Recovery Runbook §3–4. Both commands
run the API image in its dedicated CLI mode (same pattern as `--migrate-only`) — no web pipeline starts,
so neither command ever runs concurrently with live request traffic.

**Create a backup** (requires `Backup:Enabled=true`; already the Production default):
```powershell
docker compose run --rm api --backup-database
```
Uses SQLite's native online backup API (`SqliteConnection.BackupDatabase`), so it is safe to run against
a live database — it never blindly copies the file. Writes to a `.tmp` file first, validates with
`PRAGMA integrity_check` (unless `Backup:ValidateAfterBackup=false`), then atomically renames into
`Backup:BackupDirectory` (`/var/lib/ai-factory/backups` — a separate Docker volume from the data volume:
`ai_factory_production_backups`). Applies retention (`Backup:RetentionCount`, default 7) after each
successful backup. Exit code 0 = success, 1 = failure (nothing partial is left behind).

**Restore from a backup** (operator-triggered only — there is no HTTP restore endpoint):
```powershell
docker compose run --rm api --restore-database /var/lib/ai-factory/backups/<backup-file>.db
```
Validates the backup file's integrity before touching anything. Takes an automatic pre-restore safety
backup of the current database first (logged, and named the same way as a normal backup) — if that
safety backup itself fails, the restore still proceeds (an operator explicitly requested it) but the
failure is logged as a warning. Copies the backup into a staging file, then atomically replaces the
active database file. Exit code 0 = success, 1 = failure (validation failures never touch the active
database), 2 = missing the required file-path argument.

After a restore, restart the API and Worker containers so they pick up the replaced database file:
```powershell
docker compose restart api worker
```

**List available backups** — there is no CLI flag for this; inspect the volume directly:
```powershell
docker run --rm -v ai_factory_production_backups:/b busybox ls -la /b
```

---

## 5. Escalation Matrix

| Condition | Primary Action | Secondary Action | Escalation Tier |
|---|---|---|---|
| Single transient API error | Automatic retry | Monitor metrics | Tier 1 (Automated) |
| Worker Degraded (< 5 min) | Automatic retry/recovery | Inspect logs | Tier 1 (Automated) |
| Worker Degraded (> 5 min) | Inspect dependency health | Restart Worker | Tier 2 (DevOps / SRE) |
| Worker Faulted | Review error logs | Restart Worker | Tier 2 (DevOps / SRE) |
| Circuit Open (> 15 min) | Check OpenRouter status | Switch provider / Alert | Tier 2 (DevOps / AI Eng) |
| Persistent Database Locked | Review active operations | Restart Stack | Tier 2 (DevOps / SRE) |
| Database Corruption / Disk Loss | Disaster Recovery Runbook | Restore from Backup | Tier 3 (Database Lead / Architect) |
| Migration Script Failure | Analyze schema difference | Rollback release | Tier 3 (Lead Engineer) |

---

## 6. CI/CD, Release & Rollback (Phase 17.5.1)

CI validation (build, full test suite, the four `Category=` filters, secret scan, dependency
scan, docker compose validation, and a container smoke test), release certification (repeated
stability runs, versioned builds, image tagging, SBOM generation, container vulnerability
scanning, the migration-failure regression gate, and the backup/restore release gate), image
tagging/rollback strategy, and the full list of known limitations (no container registry
configured, dependency-scan severity-bucketing limitation, etc.) are documented in
[`phase-17.5.1-cicd-release-automation-supply-chain.md`](./phase-17.5.1-cicd-release-automation-supply-chain.md).
Use that document, not this section, as the source of truth for anything CI/CD- or
release-pipeline-related; this runbook's §4.6/§4.7 above remain the source of truth for the
underlying migration/backup/restore *mechanics* that the release pipeline exercises.
