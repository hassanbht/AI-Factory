# Phase 16.4 — Circuit Breaker

Implement circuit breaker protection for external providers.

Requirements:

1. Add circuit breaker behavior for:
   - OpenRouter provider
   - Hermes agent provider

2. Circuit states:
   - Closed
   - Open
   - HalfOpen

3. Make thresholds configurable:
   - consecutive failure threshold
   - break duration
   - half-open trial count

4. When circuit is open:
   - fail fast
   - return a meaningful provider-unavailable error
   - do not call the external provider

5. Provider health should be independently tracked.

6. Log circuit transitions.

7. Do not implement this logic in controllers.

8. Preserve dependency inversion through existing interfaces.

9. Add tests for:
   - opening after repeated failures
   - fail fast
   - half-open recovery
   - successful reset

Run build and tests.