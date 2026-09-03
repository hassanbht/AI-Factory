using AI.Factory.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace AI.Factory.Infrastructure.Configuration;

/// <summary>
/// Composite secret provider that evaluates multiple inner secret providers in priority order.
/// </summary>
public sealed class CompositeSecretProvider : ISecretProvider
{
    public const string ProviderIdentifier = "Composite";

    private readonly IReadOnlyList<ISecretProvider> _providers;
    private readonly ILogger<CompositeSecretProvider> _logger;

    public string ProviderName => ProviderIdentifier;

    public CompositeSecretProvider(
        IEnumerable<ISecretProvider> providers,
        ILogger<CompositeSecretProvider> logger)
    {
        _providers = providers.ToList();
        _logger = logger;
    }

    public async Task<string?> GetSecretAsync(string secretName, CancellationToken cancellationToken = default)
    {
        var result = await ResolveSecretAsync(secretName, cancellationToken);
        return result.Found ? result.Value : null;
    }

    public async Task<SecretResolutionResult> ResolveSecretAsync(string secretName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(secretName))
        {
            return new SecretResolutionResult(
                Found: false,
                Value: null,
                ProviderName: ProviderName,
                ResolvedAtUtc: DateTime.UtcNow);
        }

        foreach (var provider in _providers)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = await provider.ResolveSecretAsync(secretName, cancellationToken);
            if (result.Found)
            {
                _logger.LogDebug(
                    "Secret '{SecretName}' resolved successfully from provider '{InnerProvider}'",
                    secretName,
                    provider.ProviderName);

                return result;
            }
        }

        return new SecretResolutionResult(
            Found: false,
            Value: null,
            ProviderName: ProviderName,
            ResolvedAtUtc: DateTime.UtcNow);
    }
}
