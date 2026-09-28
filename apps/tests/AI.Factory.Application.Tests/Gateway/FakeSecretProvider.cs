using AI.Factory.Application.Abstractions;

namespace AI.Factory.Application.Tests.Gateway;

public sealed class FakeSecretProvider : ISecretProvider
{
    private readonly Dictionary<string, string?> _secrets = new(StringComparer.OrdinalIgnoreCase);

    public FakeSecretProvider(IDictionary<string, string?>? initial = null)
    {
        if (initial is not null)
        {
            foreach (var (k, v) in initial)
            {
                _secrets[k] = v;
            }
        }
    }

    public string ProviderName => "FakeSecretProvider";

    public void SetSecret(string name, string? value) => _secrets[name] = value;

    public Task<string?> GetSecretAsync(string secretName, CancellationToken cancellationToken = default)
    {
        _secrets.TryGetValue(secretName, out var value);
        return Task.FromResult(value);
    }

    public Task<SecretResolutionResult> ResolveSecretAsync(string secretName, CancellationToken cancellationToken = default)
    {
        var found = _secrets.TryGetValue(secretName, out var value) && value is not null;
        return Task.FromResult(new SecretResolutionResult(found, value, ProviderName, DateTime.UtcNow));
    }
}
