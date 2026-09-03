# Phase 17.4.6 — Configuration & Secret Management

## Overview

Phase 17.4.6 establishes a provider-neutral, production-ready configuration and secret management architecture for AI Factory across both the API and Worker applications. It introduces strongly typed options, fail-fast startup validation, provider-neutral secret resolution abstractions (`ISecretProvider`), safe configuration diagnostics without credential exposure, and seamless observability integration.

---

## 1. Configuration Architecture

The configuration architecture adheres to Clean Architecture principles:
- **Domain Layer**: Completely configuration-independent. Does not reference `IConfiguration` or `Microsoft.Extensions.Options`.
- **Application Layer (`AI.Factory.Application.Configuration` & `AI.Factory.Application.Abstractions`)**: Defines plain strongly typed option models (`DatabaseOptions`, `SecretManagementOptions`, `AiProviderOptions`, `SecurityOptions`) and abstract contracts (`ISecretProvider`, `IConfigurationDiagnosticsService`).
- **Infrastructure Layer (`AI.Factory.Infrastructure.Configuration`)**: Implements secret providers (`EnvironmentSecretProvider`, `InMemorySecretProvider`, `CompositeSecretProvider`) and the diagnostics engine (`ConfigurationDiagnosticsService`).
- **Composition Roots (`AI.Factory.Api` & `AI.Factory.Worker`)**: Bind configuration from `appsettings.json`, environment variables, and CLI arguments, execute fail-fast validation, and register singletons into the DI container.

---

## 2. Configuration Precedence

Configuration sources follow standard .NET hierarchical precedence (last loaded wins):
1. `appsettings.json` (Base defaults).
2. `appsettings.{Environment}.json` (Environment-specific overrides, e.g. `appsettings.Development.json`).
3. Environment variables (e.g. `Security__OperationalEndpoints__AccessKey`, `Database__ConnectionString`).
4. Command line arguments (highest precedence for process execution).

---

## 3. Strongly Typed Options

| Options Class | Configuration Section | Purpose |
|---|---|---|
| `DatabaseOptions` | `Database` | Encapsulates database provider ("Sqlite") and connection string. Enforces presence without exposing connection strings to diagnostics. |
| `SecretManagementOptions` | `SecretManagement` | Controls secret provider selection ("Environment", "InMemory", "Composite") and fail-fast behavior. |
| `AiProviderOptions` | `AiProviders` | Manages external AI model provider credentials by secret reference (`ApiKeySecretName`), never storing raw keys in files. |
| `SecurityOptions` | `Security` | Manages operational endpoint access keys (`AccessKeySecretName` or direct `AccessKey`), rate limiting, request size bounds, CORS, and security headers. |
| `ExecutionLimitsOptions` | `ExecutionLimits` | Manages execution timeouts and repair attempt bounds. |
| `ResilienceOptions` | `Resilience` | Configures retry, timeout, circuit breaker, and bulkhead profiles. |
| `ShutdownOptions` | `Shutdown` | Configures graceful shutdown timeouts and cancellation horizons. |
| `OrchestrationOptions` | `Orchestration` | Configures workspace paths, max attempts, and polling intervals. |

---

## 4. Startup Validation

All production-critical options execute synchronous `.Validate()` at application startup before accepting traffic or claiming background work:
- `DatabaseOptions.Validate()`: Throws `InvalidOperationException` if connection string is null or whitespace.
- `SecretManagementOptions.Validate()`: Throws if enabled but provider name is empty.
- `AiProviderOptions.Validate()`: Throws if a provider requiring an API key has no `ApiKeySecretName` configured.
- `SecurityOptions.Validate()`: Throws if `RequireAccessKey` is enabled but neither `AccessKey` nor `AccessKeySecretName` is configured, or if `AccessKey` < 16 characters.
- Failures fail fast with clear, non-sensitive exception messages that never print raw connection strings or secrets.

---

## 5. Secret Provider Abstraction

Provider-neutral contract defined in `AI.Factory.Application.Abstractions`:

```csharp
public interface ISecretProvider
{
    string ProviderName { get; }

    Task<string?> GetSecretAsync(string secretName, CancellationToken cancellationToken = default);

    Task<SecretResolutionResult> ResolveSecretAsync(string secretName, CancellationToken cancellationToken = default);
}

public sealed record SecretResolutionResult(
    bool Found,
    string? Value,
    string ProviderName,
    DateTime ResolvedAtUtc);
```

### Guarantees
- No secret values are logged or written to `AuditLogEntry`.
- Missing secrets return `Found: false` without throwing or exposing secret names.
- Cancellation tokens are propagated and respected.
- `SecretResolutionResult.Value` is held transiently in memory and never serialized in diagnostic endpoints.

---

## 6. Supported Secret Providers

1. **`EnvironmentSecretProvider`**:
   - Resolves secrets directly from process environment variables via `Environment.GetEnvironmentVariable(secretName)`.
   - Default provider for production, containers (Docker/Kubernetes), and local development.
2. **`InMemorySecretProvider`**:
   - Stores secrets in thread-safe memory (`ConcurrentDictionary`).
   - Primarily used in unit and integration test fixtures to inject fake credentials without modifying the developer host environment.
3. **`CompositeSecretProvider`**:
   - Evaluates a list of inner providers in priority order (e.g. Primary -> Fallback).

---

## 7. Development vs. Production Behavior

- **Development**:
  - `AccessKey` can be specified directly in `appsettings.Development.json` for convenience.
  - Safe default SQLite connection string is populated if omitted.
- **Production**:
  - Raw secrets must NOT exist in configuration files.
  - Secrets are referenced by name (e.g. `ApiKeySecretName: "OPENROUTER_API_KEY"`).
  - Missing critical secrets fail fast on startup or first resolution.
  - Unsafe fallbacks are disabled.

---

## 8. Secret Reference Convention

Normal configuration sections must reference secrets by logical name rather than embedding literal values:

```json
"AiProviders": {
  "Providers": {
    "OpenRouter": {
      "Enabled": true,
      "BaseUrl": "https://openrouter.ai/api/v1",
      "RequiresApiKey": true,
      "ApiKeySecretName": "OPENROUTER_API_KEY"
    }
  }
}
```

The consumer resolves the secret via `ISecretProvider`:
```csharp
var apiKey = await secretProvider.GetSecretAsync(settings.ApiKeySecretName, ct);
```

---

## 9. Secret Rotation Compatibility

- Secrets are resolved on demand or transiently scoped rather than permanently baked into static singleton strings.
- `ISecretProvider` supports dynamic lookups; changes to environment variables or future secret store updates are immediately visible on subsequent resolutions.
- No cached secrets are exposed through diagnostics or heap snapshots.

---

## 10. Safe Diagnostics

Endpoint: `GET /api/operations/configuration`
- Protected by `OperationalEndpointSecurityMiddleware` and rate-limited under `OperationalApi`.
- Returns `ConfigurationDiagnosticSnapshot` containing:
  - `EnvironmentName`
  - `IsValid`
  - `Database`: `Configured`, `Provider`, `HasConnectionString` (CRITICAL: Connection string itself is omitted).
  - `SecretManagement`: `Enabled`, `ProviderName`, `FailFastOnMissingSecret`.
  - `Security`: Safe boolean flags and limits.
  - `Resilience`: Count of configured profiles and profile names.
  - `ExternalProviders`: Provider names and `HasSecretReference` status (NO secret values).
  - `ConfigurationSources`: List of active configuration provider type names.

---

## 11. Health Check Behavior

- **`ConfigurationHealthCheck`**:
  - Registered as a readiness health check (`tags: ["ready"]`).
  - Re-evaluates `ExecutionLimitsOptions`, `DatabaseOptions`, `SecretManagementOptions`, and `SecurityOptions`.
  - Reports `Healthy` when all options validate successfully.
  - Reports `Unhealthy` when any required option is missing or invalid.
  - Sanitizes exception messages via `SensitiveDataSanitizer` to prevent secret leaks in health responses.
  - Does NOT perform remote provider network I/O during probes, ensuring fast, deterministic health probe responses.

---

## 12. API and Worker Wiring

Both `AI.Factory.Api` and `AI.Factory.Worker` register the same core configuration services:
- `DatabaseOptions`
- `SecretManagementOptions`
- `AiProviderOptions`
- `SecurityOptions`
- `ISecretProvider` (`EnvironmentSecretProvider`)
- `IConfigurationDiagnosticsService` (`ConfigurationDiagnosticsService`)
- The Worker does NOT depend on ASP.NET Core `HttpContext` or HTTP routing middleware.

---

## 13. Security Guarantees & Redaction

- **Zero Persistence of Secrets**: Secrets are never stored in SQLite database tables or `AuditLogEntry` metadata.
- **Redaction in Observability**:
  - `SensitiveDataSanitizer` redacts Bearer tokens, passwords, API keys, connection strings, and local file paths.
  - `ApiExceptionMapper` intercepts database exceptions and sanitizes ProblemDetails messages.
- **Audit Logging**: Added `ConfigurationValidationFailed`, `SecretResolutionFailed`, and `RequiredSecretMissing` to `AuditEventType` and `IOperationalMetrics`.

---

## 14. Extension Points for Future Cloud Secret Providers

Future cloud secret providers (e.g. Azure Key Vault, AWS Secrets Manager, HashiCorp Vault) can be introduced by implementing `ISecretProvider`:
```csharp
public class AzureKeyVaultSecretProvider : ISecretProvider { ... }
```
Because `ISecretProvider` resides in `AI.Factory.Application.Abstractions`, adding a new provider requires only an Infrastructure implementation and registration in the composition root, leaving Domain and Application business logic untouched.

---

## 15. Intentionally Deferred Items

- **Cloud SDK Dependencies**: Azure Key Vault, AWS Secrets Manager, and HashiCorp Vault SDKs are intentionally deferred to avoid adding unneeded heavy external dependencies until deployment environments demand them.
- **Database Secret Storage**: Storing encrypted secrets in SQLite was intentionally rejected because the database is not an approved hardware security module or secrets manager.
