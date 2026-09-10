namespace AI.Factory.Application.Abstractions;

/// <summary>
/// In-memory catalog of configured AI model providers, populated once at
/// startup from configuration. Maps a stable provider ID to its
/// <see cref="IModelProvider"/> instance, and a routable model ID to the
/// provider ID that serves it.
/// </summary>
public interface IAiModelProviderRegistry
{
    /// <summary>Registers a provider instance. Throws if <see cref="IModelProvider.ProviderId"/> is already registered.</summary>
    void RegisterProvider(IModelProvider provider);

    /// <summary>Maps a model ID to an already-registered provider ID. Throws if the model ID is already mapped.</summary>
    void RegisterModel(string modelId, string providerId);

    bool TryGetProvider(string providerId, out IModelProvider? provider);

    /// <summary>Resolves a model ID to its provider instance via the model-to-provider mapping.</summary>
    bool TryResolveModel(string modelId, out IModelProvider? provider);

    IReadOnlyCollection<string> RegisteredProviderIds { get; }

    IReadOnlyCollection<string> RegisteredModelIds { get; }
}

public sealed class DuplicateProviderRegistrationException : Exception
{
    public DuplicateProviderRegistrationException(string providerId)
        : base($"AI model provider '{providerId}' is already registered.")
    {
    }
}

public sealed class DuplicateModelRegistrationException : Exception
{
    public DuplicateModelRegistrationException(string modelId)
        : base($"Model ID '{modelId}' is already mapped to a provider.")
    {
    }
}
