# Phase 18.1.1 — AI Provider Abstraction & Intelligent Model Routing Foundation

## Goal

A provider-neutral AI model abstraction layer: orchestration code depends on
`IAiModelRouter.ExecuteAsync(request)`, never on a concrete provider SDK.
Deterministic, testable routing baseline only — no ML-based routing.

## Architecture

Reused rather than duplicated: `IModelRouter`/`ModelRouter`/`ModelRoutingOptions`
(task-category selection) and `AiProviderOptions` (provider credential/config
registry) already existed from prior phases. This phase adds the missing
*execution* layer on top of them.

```
Application/Abstractions/IModelProvider.cs        AiCapability, AiFailureCategory,
                                                    AiCompletionRequest/Result, IModelProvider
Application/Abstractions/IAiModelProviderRegistry.cs
Application/Abstractions/IAiModelRouter.cs
Application/Orchestration/AiModelProviderRegistry.cs   pure in-memory registry
Application/Orchestration/AiModelRouter.cs             deterministic routing + fallback
Application/Configuration/AiRoutingOptions.cs          MaxFallbackAttempts, CapabilityPreferredModel

Infrastructure/Providers/FakeModelProvider.cs              deterministic test/dev provider
Infrastructure/Providers/OpenAiCompatibleModelProvider.cs  generic OpenAI-chat-compatible HTTP client
Infrastructure/Providers/ResilienceModelProviderDecorator.cs   wraps any IModelProvider with IResilienceExecutor
```

Domain has zero dependency on any of this. Application never references an
HTTP client, an OpenAI/OpenRouter SDK, or a wire DTO — those live only in
`OpenAiCompatibleModelProvider.cs`'s private nested records.

## Provider abstraction

`IModelProvider`: `ProviderId`, `SupportedCapabilities`, `CompleteAsync(AiCompletionRequest) -> AiCompletionResult`.
`AiCompletionResult.Success == false` always carries a normalized
`AiFailureCategory` (Authentication, RateLimited, Timeout, Unavailable,
InvalidRequest, ProviderFailure, Cancelled) — no provider exception ever
escapes to a caller.

`ModelCapability` (existing catalog entry type) gained a `Capabilities: List<string>`
property — a model with an empty list only participates in the legacy
task-category router; a non-empty list makes it eligible for capability-based
`IAiModelRouter` routing.

## Routing algorithm (deterministic, in `AiModelRouter.BuildCandidateOrder`)

1. `request.PreferredModelId`, if capability-compatible + available + registered.
2. `AiRoutingOptions.CapabilityPreferredModel[capability]`, same filters.
3. Remaining capability-compatible, available models, ordered by their
   provider's `ProviderCredentialSettings.Priority` (descending), ties broken
   by declared order in `ModelRouting:Models`.

Candidates are de-duplicated preserving that order, then tried in sequence,
bounded by `AiRoutingOptions.MaxFallbackAttempts` (validated 1–10, default 3).
A candidate's failure only advances the chain when its `AiFailureCategory` is
one of `{RateLimited, Timeout, Unavailable, ProviderFailure}` — every
fallback increments `IOperationalMetrics.RecordAiRoutingFallback()` and is
logged with the previous failure's category. `Authentication`,
`InvalidRequest`, and `Cancelled` stop the chain immediately, no fallback.
No compatible candidate at all → an `Unavailable` failure result, no
exception thrown.

## Failure normalization & resilience

`ResilienceModelProviderDecorator` wraps every configured provider (mirrors
the existing `ResilienceAgentExecutorDecorator` pattern exactly) using
`IResilienceExecutor` with the pre-existing `ResilienceOptions.ProviderCall`
profile (3 retries, 1 min attempt / 5 min operation timeout, circuit breaker,
bulkhead 10/20 — unchanged from Phase 17.4.x). Only `AiFailureCategory` values
in the retryable set trigger a retry, via a private `AiProviderTransientException`
signal that `IFailureClassifier` now recognizes as `FailureCategory.Provider`
(transient) — one added `case` line, reusing the existing classifier rather
than building a second one. Retry exhaustion, circuit-open, and bulkhead
rejection are all caught and turned back into a normalized `AiCompletionResult`
— nothing but that record ever reaches `AiModelRouter`.

`OpenAiCompatibleModelProvider` makes exactly one HTTP call per invocation
(no retry logic of its own — that's the decorator's job) and maps HTTP status
codes / exceptions to categories: 401/403 → Authentication, 429 → RateLimited
(preserves `Retry-After` as `AiCompletionResult.RetryAfter` for future
adaptive routing), 408/504 → Timeout, 400/422/404 → InvalidRequest,
503/502 → Unavailable, other 5xx → ProviderFailure, `TaskCanceledException`
→ Timeout, caller-token cancellation → Cancelled, `HttpRequestException` →
ProviderFailure.

## Configuration

`AiProviders:Providers:<name>` (existing section, extended):
`ProviderType` (`OpenAiCompatible` default | `Fake`), `Priority` (int,
default 0), `TimeoutSeconds` (default 60) — alongside the existing
`Enabled`/`BaseUrl`/`RequiresApiKey`/`ApiKeySecretName`.

`ModelRouting:Models[].Capabilities` (new, string list) — e.g.
`["CodeGeneration", "GeneralChat"]`.

`AiRouting` (new section): `MaxFallbackAttempts` (default 3, validated 1–10),
`CapabilityPreferredModel` (capability name → ModelId).

No secret values are ever stored in configuration — only `ApiKeySecretName`,
resolved at call time through the existing `ISecretProvider`/
`EnvironmentSecretProvider`. Docker env-var passthrough
(`OPENROUTER_API_KEY=${OPENROUTER_API_KEY:-}` in `docker-compose.yml`) is
unchanged — no new provider secret was added, so no compose file edit was
needed.

## DI wiring (`Program.cs`, Api and Worker — identical block, no new extension method)

Matches the repo's 100%-inline registration convention (no `AddApplication()`/
`AddInfrastructure()` exists anywhere in this codebase — introducing one here
would be the one inconsistent file). For each enabled `AiProviders` entry: a
named `HttpClient` is registered, then inside a single `AddScoped<IAiModelProviderRegistry>`
factory, a `FakeModelProvider` or `OpenAiCompatibleModelProvider` is built,
wrapped in `ResilienceModelProviderDecorator`, and registered; every
`ModelRouting:Models` entry with a non-empty `Capabilities` list is mapped to
its provider. `IAiModelRouter` is `AddScoped` (it depends on the scoped
registry, matching the existing `IAgentExecutor` decorator's scoping).

## Observability

`IOperationalMetrics` (existing god-interface, extended in place — not a new
metrics class): `RecordAiRequestStarted`, `RecordAiRequestSucceeded(durationMs)`,
`RecordAiRequestFailed`, `RecordAiProviderFailure`, `RecordAiRateLimited`,
`RecordAiRoutingFallback`, all `Interlocked`-based; `OperationalMetricsSnapshot`
gained matching immutable fields (`AiRequests`, `AiRequestsSucceeded`, ...,
`AverageAiRequestDurationMs`, `TotalAiRequestDurationMs`). `ConfigurationDiagnosticsService`
already listed `AiProviderOptions.Providers` entries as sanitized
`ProviderDiagnosticInfo` (name/enabled/secret-reference-name only) from a
prior phase — untouched, still correct for the new fields since nothing
secret was added to that type.

## API surface

No new endpoint. No anonymous arbitrary-prompt execution path exists —
`IAiModelRouter` is only reachable from internal orchestration code via DI.

## Tests added (all in `AI.Factory.Application.Tests`, matching the existing convention of testing Infrastructure code from that project)

- `AiModelProviderRegistryTests.cs` — registration, duplicate provider rejection, duplicate model registration, unresolvable model.
- `AiModelRouterTests.cs` — explicit preference, unsupported capability, unavailable-model skip, priority ordering, deterministic fallback, non-fallback-eligible failure (InvalidRequest), bounded fallback chain, cancellation.
- `ResilienceModelProviderDecoratorTests.cs` — success/no-retry, non-retryable (Authentication) no-retry, retryable (RateLimited) retry-then-succeed, retries-exhausted normalized failure.
- `OpenAiCompatibleModelProviderTests.cs` — success mapping incl. token usage, Bearer header from resolved secret, 429→RateLimited+RetryAfter, status-code table (401/403/400/503/500), `TaskCanceledException`→Timeout, caller cancellation→Cancelled, network failure→ProviderFailure, missing-secret failure with no secret value leaked into the error message.
- `ConfigurationValidationTests.cs` (Api.Tests, extended) — `AiRoutingOptions.Validate()` bounds, `ProviderCredentialSettings` new-field defaults.
- `OperationalMetricsTests.cs` (extended) — independent AI counters, 1000-way concurrent-increment exact-count regression test.

## Validation — ACTUALLY EXECUTED

- `dotnet build apps/AI.Factory.slnx -c Debug` → succeeded, 0 warnings, 0 errors.
- `dotnet build apps/AI.Factory.slnx -c Release` → succeeded, 0 warnings, 0 errors.
- `dotnet test apps/AI.Factory.slnx -c Release --no-build` →
  Worker.Tests: 60/60 passed. Application.Tests: 414/414 passed. Api.Tests: 220/220 passed. (694 total, 0 failed, 0 skipped.)
- `dotnet test ... --filter "Category=Resilience"` → 6/6 passed (Worker.Tests).
- `dotnet test ... --filter "Category=Stress"` → 15/15 (Application.Tests) + 4/4 (Api.Tests) passed.
- `dotnet test ... --filter "Category=Load"` → 9/9 passed (Api.Tests).
- `dotnet test ... --filter "Category=ContainerIntegration"` → 2/2 passed (Api.Tests).
- `docker compose -f docker-compose.yml config -q` → exit 0 (valid).
- `docker compose -f docker-compose.release.yml config -q` → exit 1, pre-existing/expected: it requires `AI_FACTORY_API_IMAGE`/`AI_FACTORY_WORKER_IMAGE` digest env vars normally injected by the release pipeline (Phase 17.5.x); no docker-compose file was touched by this phase, so this is not a regression.

## IMPLEMENTED BUT NOT EMPIRICALLY EXECUTED

- No real call to OpenRouter, OpenAI, or any live HTTP endpoint was made.
  `OpenAiCompatibleModelProvider` is verified only against a stub
  `HttpMessageHandler` in-process — its request/response mapping is correct
  by test, not by a live integration run.
- The `Fake` provider type (`AiModelProviderKind.Fake`) is wired into DI but
  no `appsettings.json` entry currently sets `ProviderType: Fake` — it exists
  for local/dev use and is exercised directly by unit tests, not through the
  full DI graph end-to-end.
- `docker-compose.release.yml` validation with real digest env vars set was
  not attempted (out of scope — no release is being cut in this phase).

## Limitations / deferred (future Phase 18 work)

- Single-turn request/response only (`Prompt` + optional `Context`); no
  multi-message conversation history yet — `AiCompletionRequest` was
  deliberately left extensible (additive optional fields) for that.
- No active/live health probing of providers — availability today is purely
  configuration-driven (`Enabled`, `IsAvailable`), as explicitly scoped for
  this phase; the provider abstraction does not block adding a probe later.
- No embedding-specific request shape yet, even though `AiCapability.Embedding`
  exists as an enum value — no embedding provider implementation in this phase.
- Hermes/Nous-specific `IModelProvider` implementation is not part of this
  phase (only Fake + OpenAI-compatible, per scope).
