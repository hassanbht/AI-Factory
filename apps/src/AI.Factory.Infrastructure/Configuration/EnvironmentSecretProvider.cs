using AI.Factory.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace AI.Factory.Infrastructure.Configuration;

/// <summary>
/// Resolves secrets from process environment variables.
/// Safe for containers, local environments, and CI/CD without storing secrets in files.
/// </summary>
public sealed class EnvironmentSecretProvider : ISecretProvider
{
    public const string ProviderIdentifier = "Environment";

    private readonly ILogger<EnvironmentSecretProvider> _logger;

    public string ProviderName => ProviderIdentifier;

    public EnvironmentSecretProvider(ILogger<EnvironmentSecretProvider> logger)
    {
        _logger = logger;
    }

    public Task<string?> GetSecretAsync(string secretName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(secretName))
        {
            return Task.FromResult<string?>(null);
        }

        cancellationToken.ThrowIfCancellationRequested();

        var value = Environment.GetEnvironmentVariable(secretName);
        return Task.FromResult(value);
    }

    public Task<SecretResolutionResult> ResolveSecretAsync(string secretName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(secretName))
        {
            return Task.FromResult(new SecretResolutionResult(
                Found: false,
                Value: null,
                ProviderName: ProviderName,
                ResolvedAtUtc: DateTime.UtcNow));
        }

        cancellationToken.ThrowIfCancellationRequested();

        var value = Environment.GetEnvironmentVariable(secretName);
        var found = !string.IsNullOrEmpty(value);

        if (!found)
        {
            _logger.LogDebug("Secret '{SecretName}' was not found in environment variables", secretName);
        }

        return Task.FromResult(new SecretResolutionResult(
            Found: found,
            Value: value,
            ProviderName: ProviderName,
            ResolvedAtUtc: DateTime.UtcNow));
    }
}
