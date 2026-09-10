using AI.Factory.Application.Abstractions;

namespace AI.Factory.Application.Orchestration;

/// <summary>
/// Pure, in-memory <see cref="IAiModelProviderRegistry"/>. No I/O — built
/// once at startup from configuration and held for the lifetime of the
/// scope/container it is registered in.
/// </summary>
public sealed class AiModelProviderRegistry : IAiModelProviderRegistry
{
    private readonly Dictionary<string, IModelProvider> _providersById = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _providerIdByModelId = new(StringComparer.OrdinalIgnoreCase);

    public void RegisterProvider(IModelProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        if (string.IsNullOrWhiteSpace(provider.ProviderId))
        {
            throw new ArgumentException("Provider ID cannot be empty.", nameof(provider));
        }

        if (!_providersById.TryAdd(provider.ProviderId, provider))
        {
            throw new DuplicateProviderRegistrationException(provider.ProviderId);
        }
    }

    public void RegisterModel(string modelId, string providerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        if (!_providersById.ContainsKey(providerId))
        {
            throw new InvalidOperationException(
                $"Cannot map model '{modelId}' to unregistered provider '{providerId}'.");
        }

        if (!_providerIdByModelId.TryAdd(modelId, providerId))
        {
            throw new DuplicateModelRegistrationException(modelId);
        }
    }

    public bool TryGetProvider(string providerId, out IModelProvider? provider)
    {
        return _providersById.TryGetValue(providerId, out provider);
    }

    public bool TryResolveModel(string modelId, out IModelProvider? provider)
    {
        if (_providerIdByModelId.TryGetValue(modelId, out var providerId))
        {
            return _providersById.TryGetValue(providerId, out provider);
        }

        provider = null;
        return false;
    }

    public IReadOnlyCollection<string> RegisteredProviderIds => _providersById.Keys.ToList();

    public IReadOnlyCollection<string> RegisteredModelIds => _providerIdByModelId.Keys.ToList();
}
