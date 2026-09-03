# Phase 17.4.8 — Deployment & Container Production Readiness

## Overview

Phase 17.4.8 hardens the AI Factory platform for production deployment and container operations. It establishes a multi-stage Docker build pipeline, a dedicated migration execution topology, persistent named volumes for SQLite WAL files and agent workspaces, non-root execution (`USER $APP_UID`), signal-aware graceful shutdown, health-aware container lifecycle management, and protected operational deployment diagnostics (`GET /api/operations/deployment`).

---

## 1. Deployment Architecture

The production topology employs a separation of concerns across stateless web traffic, stateful background work, and one-shot schema migrations:

```text
                    ┌─────────────────────────┐
                    │ Reverse Proxy / Ingress │
                    │   (:80 / :443 TLS)      │
                    └────────────┬────────────┘
                                 │
                                 ▼
                     ┌───────────────────────┐
                     │    AI.Factory.Api     │
                     │  (:8080 non-root user)│
                     │  /health/live (200)   │
                     │  /health/ready (200)  │
                     └───────────┬───────────┘
                                 │
                                 ▼
                     ┌───────────────────────┐
                     │ Named Persistent Vol  │
                     │ /var/lib/ai-factory/  │
                     │         data          │
                     │   (SQLite WAL mode)   │
                     └───────────┬───────────┘
                                 ▲
                                 │
                     ┌───────────┴───────────┐
                     │   AI.Factory.Worker   │
                     │ (non-root background) │
                     │ /tmp/worker.ready     │
                     │ Graceful SIGTERM (45s)│
                     └───────────────────────┘
                                 ▲
                                 │ (depends_on: completed)
                     ┌───────────┴───────────┐
                     │       migrator        │
                     │  (dedicated one-shot) │
                     │  --migrate-only       │
                     └───────────────────────┘
```

- **`migrator`**: Runs the published application binary with `--migrate-only` to apply all pending EF Core migrations. Exits with code 0 upon success.
- **`api`**: Stateless ASP.NET Core web host serving REST endpoints, health probes, and operational diagnostics. Runs as non-root user `app` (UID 1654).
- **`worker`**: Background processing host running workflow recovery, task scheduling, and orchestration engines.
- **`ai_factory_data`**: Persistent Docker named volume hosting SQLite database files (`factory.db`, `factory.db-wal`, `factory.db-shm`).
- **`ai_factory_workspace`**: Persistent Docker named volume hosting agent sandboxes and generated project files.

---

## 2. API Container Lifecycle

1. **Container Launch**: Docker starts the container via exec entrypoint `ENTRYPOINT ["dotnet", "AI.Factory.Api.dll"]`.
2. **Startup Pipeline**:
   - Environment variables loaded (`ASPNETCORE_ENVIRONMENT=Production`).
   - Configuration bound and validated (fail-fast on missing configuration).
   - Secret provider initialized (`EnvironmentSecretProvider`).
   - EF Core command timeout and SQLite connection string configured.
   - Core services and background coordinators registered.
   - HTTP listener starts on port 8080.
3. **Liveness Probe (`/health/live`)**: Responds with 200 OK as soon as the HTTP server is listening.
4. **Readiness Probe (`/health/ready`)**: Evaluates database connectivity, configuration health, and circuit breakers. Reports 200 OK when ready to receive traffic.
5. **Termination (`SIGTERM`)**:
   - Docker delivers `SIGTERM` directly to process ID 1.
   - `IHostApplicationLifetime.ApplicationStopping` fires.
   - `ShutdownHealthCheck` causes `/health/ready` to immediately return 503 Service Unavailable, signaling reverse proxies to stop routing new requests.
   - In-flight HTTP requests drain within the `stop_grace_period: 30s` window.
   - Process exits cleanly.

---

## 3. Worker Container Lifecycle

1. **Startup Ordering**: Blocked until `migrator` service completes successfully (`service_completed_successfully`).
2. **Container Launch**: Docker executes `ENTRYPOINT ["dotnet", "AI.Factory.Worker.dll"]`.
3. **Initialization**:
   - Options validated and `FactoryDbContext` registered.
   - `WorkerHealthCoordinator` sets state to `Starting`, touches `/tmp/ai-factory-health/worker.live`.
   - `WorkflowRecoveryHostedService` restores interrupted runs from previous shutdowns.
   - `OrchestrationRunWorker` begins polling for queued work.
4. **Readiness Signal**: `WorkerHealthLifecycleHostedService` catches `ApplicationStarted` and invokes `MarkReady()`, writing `/tmp/ai-factory-health/worker.ready`.
5. **Termination (`SIGTERM`)**:
   - Docker delivers `SIGTERM` directly to process.
   - `IHostApplicationLifetime.ApplicationStopping` fires.
   - `WorkerHealthCoordinator.MarkStopping()` deletes `/tmp/ai-factory-health/worker.ready` to revoke readiness.
   - `IApplicationShutdown.InitiateShutdown()` signals cancellation tokens to active jobs.
   - Clean shutdown completes within `stop_grace_period: 45s`.

---

## 4. Health Check Model

| Check | Scope | Endpoint / Probe | Expected Semantics |
|---|---|---|---|
| **API Liveness** | Process alive | `GET /health/live` | Never touches database or external providers. Verifies process can respond. |
| **API Readiness** | Traffic ready | `GET /health/ready` | Probes DB connectivity (`CanConnectAsync`), config health, circuit breaker state, and shutdown status. |
| **Worker Liveness** | Process alive | File `/tmp/ai-factory-health/worker.live` | Created on process start; touched periodically. |
| **Worker Readiness** | Ready for jobs | File `/tmp/ai-factory-health/worker.ready` | Created once hosted services start; removed immediately when stopping or faulted. |

---

## 5. Liveness vs. Readiness Semantics

- **Do NOT overload Liveness**: Liveness must not fail due to a temporary database lock, network hiccup, or open circuit breaker. If liveness fails, Docker will unnecessarily kill and restart the container, causing cascading restart storms.
- **Readiness protects traffic**: If database connectivity drops or shutdown begins, readiness fails (HTTP 503 or missing ready file). Load balancers cut incoming traffic while the container remains alive to self-heal or drain work.

---

## 6. Persistent Storage Model

```text
Host Filesystem (Docker Managed)
       └── var/lib/docker/volumes/ai_factory_production_data/_data
             ├── factory.db
             ├── factory.db-wal
             └── factory.db-shm
```

- Mounted at `/var/lib/ai-factory/data` in all containers.
- SQLite runs with Write-Ahead Logging (`WAL`) mode enabled.
- Container rebuilds (`docker compose down && docker compose up`) preserve the database volume.
- Directory permissions are pre-created and assigned to non-root UID 1654 (`app:app`) during Docker image build.

---

## 7. SQLite Production Limitations & Scaling Boundary

- **Single-Node Sharing**: Multiple containers on the *same host* (such as API and Worker) can safely share a single SQLite database file using SQLite WAL mode, EF Core command timeout, and Phase 17.4.7 optimistic concurrency.
- **Horizontal Scaling Limitation**: SQLite is a serverless, single-file database. It cannot be distributed across multiple host nodes in Kubernetes or Docker Swarm clusters without distributed network filesystems (which risk file corruption).
- **Relational Provider Migration**: When scaling horizontally across multiple cluster nodes, transition to PostgreSQL by setting:
  ```env
  Database__Provider=PostgreSql
  Database__ConnectionString=Host=postgres;Database=aifactory;Username=...;Password=...
  ```
  The Clean Architecture abstractions (`IDatabaseResiliencePolicy`, `IDatabaseTransactionManager`, `FactoryDbContext`) require zero application code rewrites.

---

## 8. Database Migration Strategy (Option A)

- **Dedicated Runner**: Migrations are never applied concurrently by API or Worker instances.
- **Execution**: The `migrator` service runs `dotnet AI.Factory.Api.dll --migrate-only` against the mounted volume.
- **Deterministic Sequencing**:
  ```yaml
  depends_on:
    migrator:
      condition: service_completed_successfully
  ```
- **Visible Failure**: If migrations fail (e.g. invalid disk permissions or schema conflict), `migrator` logs the error, exits with a non-zero code, and prevents API and Worker from starting against an unmigrated schema.

---

## 9. Environment Profiles & Configuration

- `appsettings.json`: Contains safe, environment-agnostic default settings.
- `appsettings.Production.json`:
  - Logs set to `Information` for application, `Warning` for ASP.NET / EF Core.
  - Production database connection string configured to persistent volume path.
  - Swagger UI disabled in production.
  - Strict secret validation enabled (`FailFastOnMissingSecret: true`).
- `.env.example`: Provides operator template for runtime environment variables.

---

## 10. Secret Injection

- Secrets are never committed to git or baked into Docker layers.
- Injected via environment variables at runtime (`OPENROUTER_API_KEY`, `OPERATIONAL_ACCESS_KEY`).
- Ingested through `EnvironmentSecretProvider` and `SensitiveDataSanitizer`.
- Diagnostics endpoints strictly redact all secret values and connection strings.

---

## 11. Graceful Shutdown & Signal Handling

- **Exec Form Entrypoint**: Dockerfiles use `ENTRYPOINT ["dotnet", "AI.Factory.Api.dll"]` rather than shell wrapping (`sh -c`). This ensures Linux signals (`SIGTERM`, `SIGINT`) reach the runtime directly.
- **Grace Periods**:
  - API: `stop_grace_period: 30s` (sufficient for draining in-flight HTTP requests).
  - Worker: `stop_grace_period: 45s` (sufficient for completing or pausing task execution units).
- **Cancellation Propagation**: In-flight steps utilize linked `CancellationToken` from `IApplicationShutdown`.

---

## 12. Restart Safety

- **No Stale Overwrites**: Optimistic concurrency tokens (`long Version`) prevent stale worker instances from overwriting newer database states.
- **Idempotent Audit Logs**: Retried operations preserve pre-existing audit entries without throwing primary key violations.
- **Recovery Service**: On startup, `WorkflowRecoveryHostedService` scans for runs left in `Running` state and recovers or fails them cleanly.

---

## 13. Resource Guidance

| Service | CPU Reservation | CPU Limit | Memory Reservation | Memory Limit |
|---|---|---|---|---|
| **`api`** | 0.25 core | 1.5 cores | 256 MB | 1024 MB |
| **`worker`** | 0.50 core | 2.0 cores | 512 MB | 2048 MB |
| **`migrator`** | 0.10 core | 1.0 core | 128 MB | 512 MB |

Limits prevent runaway processes or memory leaks from impacting the host OS or neighboring containers.

---

## 14. Container Security Decisions

1. **Non-Root Execution**: Runs under built-in user `app` (UID 1654, GID 1654).
2. **Minimal Runtime Images**: Final stage uses `aspnet:10.0` or `runtime:10.0`, discarding the .NET SDK, compiler, source code, and test packages.
3. **Filesystem Hardening**: Root filesystem permissions restricted; temporary directories (`/tmp`) backed by `tmpfs` mounts.
4. **Diagnostics Protection**: `GET /api/operations/deployment` is protected by `OperationalEndpointSecurityMiddleware` and rate-limited.

---

## 15. Production Deployment Checklist

1. [ ] Copy `.env.example` to `.env` on host.
2. [ ] Populate `OPENROUTER_API_KEY` and generate a strong `OPERATIONAL_ACCESS_KEY`.
3. [ ] Set `REQUIRE_OPERATIONAL_KEY=true` in production.
4. [ ] Validate configuration: `docker compose config`.
5. [ ] Build images: `docker compose build`.
6. [ ] Execute initial migration: `docker compose run --rm migrator`.
7. [ ] Start services: `docker compose up -d`.
8. [ ] Verify health: `curl -f http://localhost:8080/health/ready`.
9. [ ] Inspect deployment diagnostics: `curl -H "X-Operational-Key: <key>" http://localhost:8080/api/operations/deployment`.

---

## 16. Rollback Considerations

- **Container Rollback**: Tagging images with version numbers (e.g. `ai-factory-api:1.0.0`) allows instant rollback via `docker compose up -d`.
- **Database Rollback**: Back up `factory.db` before applying migrations. EF Core migrations can be rolled back using `dotnet ef database update <TargetMigration>`.

---

## 17. Future PostgreSQL Migration Considerations

When transitioning to a distributed multi-node cluster (Kubernetes / ECS):
1. Launch PostgreSQL service.
2. Update `Database__Provider=PostgreSql` and connection string in `.env`.
3. API and Worker containers scale horizontally to *N* replicas without filesystem locks or single-host restrictions.
