namespace AI.Factory.Application.Abstractions;

/// <summary>
/// Provider-neutral abstraction for resolving operational and external integration secrets.
/// Implementations must not log secret values or include secret values in exception messages.
/// </summary>
public interface ISecretProvider
{
    /// <summary>
    /// Identifier of this secret provider (e.g. "Environment", "InMemory", "Composite").
    /// </summary>
    string ProviderName { get; }

    /// <summary>
    /// Resolves a secret by its logical name (e.g. "OPENROUTER_API_KEY").
    /// Returns null if the secret is not found.
    /// </summary>
    Task<string?> GetSecretAsync(string secretName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves a secret with safe resolution metadata.
    /// </summary>
    Task<SecretResolutionResult> ResolveSecretAsync(string secretName, CancellationToken cancellationToken = default);
}

/// <summary>
/// Holds resolution result along with safe metadata.
/// Value is transiently held in memory and must NEVER be serialized in diagnostics or logs.
/// </summary>
public sealed record SecretResolutionResult(
    bool Found,
    string? Value,
    string ProviderName,
    DateTime ResolvedAtUtc);
