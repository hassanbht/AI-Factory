# Phase 16.7 — Health Checks

Implement production health checks.

Requirements:

1. Add endpoints:

   /health/live
   /health/ready
   /health

2. Liveness must only verify the application is alive.

3. Readiness must verify required runtime dependencies such as:
   - SQLite/database accessibility
   - required configuration validity
   - critical background worker registration

4. Do not make expensive AI inference requests during health checks.

5. External provider checks must be lightweight and configurable.

6. Return appropriate HTTP status codes.

7. Keep health check implementation isolated from business logic.

8. Add integration tests.

Run build and tests.