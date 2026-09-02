# Phase 16.3 — Resilience and Retry Policy

Implement safe retry behavior for external AI and infrastructure operations.

Requirements:

1. Do not implement blanket retries.

2. Retry only transient failures, including:
   - HTTP 429
   - HTTP 500
   - HTTP 502
   - HTTP 503
   - HTTP 504
   - transient network failures
   - transient timeout failures

3. Do not retry:
   - authentication failures
   - invalid API key
   - validation errors
   - invalid request payloads
   - business rule failures

4. Implement exponential backoff with jitter.

5. Retry policy must be configurable.

6. Record every retry attempt with:
   - execution id
   - operation
   - attempt number
   - failure reason
   - delay before retry

7. Prevent duplicate side effects.

8. Apply resilience wrappers to:
   - OpenRouter model calls
   - Hermes execution calls
   - external HTTP integrations

9. Keep retry logic infrastructure-specific and do not leak HTTP implementation details into Domain.

10. Add tests for:
    - transient retry
    - non-transient no retry
    - max retry reached
    - cancellation during retry delay

Run build and full tests.