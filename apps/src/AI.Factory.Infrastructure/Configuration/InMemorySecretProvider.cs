using System.Collections.Concurrent;
using AI.Factory.Application.Abstractions;

namespace AI.Factory.Infrastructure.Configuration;

/// <summary>
/// In-memory secret provider intended for unit and integration testing.
/// Does not persist secrets to disk or database.
/// </summary>
public sealed class InMemorySecretProvider : ISecretProvider
{
    public const string ProviderIdentifier = "InMemory";

    private readonly ConcurrentDictionary<string, string> _secrets = new(StringComparer.OrdinalIgnoreCase);

    public string ProviderName => ProviderIdentifier;

    public InMemorySecretProvider(IDictionary<string, string>? initialSecrets = null)
    {
        if (initialSecrets is not null)
        {
            foreach (var (key, value) in initialSecrets)
            {
                _secrets[key] = value;
            }
        }
    }

    public void SetSecret(string secretName, string secretValue)
    {
        _secrets[secretName] = secretValue;
    }

    public Task<string?> GetSecretAsync(string secretName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(secretName))
        {
            return Task.FromResult<string?>(null);
        }

        _secrets.TryGetValue(secretName, out var val);
        return Task.FromResult(val);
    }

    public Task<SecretResolutionResult> ResolveSecretAsync(string secretName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(secretName))
        {
            return Task.FromResult(new SecretResolutionResult(
                Found: false,
                Value: null,
                ProviderName: ProviderName,
                ResolvedAtUtc: DateTime.UtcNow));
        }

        var found = _secrets.TryGetValue(secretName, out var val);
        return Task.FromResult(new SecretResolutionResult(
            Found: found,
            Value: val,
            ProviderName: ProviderName,
            ResolvedAtUtc: DateTime.UtcNow));
    }
}
