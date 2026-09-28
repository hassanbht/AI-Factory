using AI.Factory.Application.Abstractions.Execution;
using AI.Factory.Application.Abstractions.Gateway;
using AI.Factory.Application.Execution;

namespace AI.Factory.Application.Gateway;

/// <summary>
/// Resolves a <see cref="ModelRoute"/> or gateway key to an <see cref="IModelGateway"/> instance.
/// Fails closed: unknown gateway, unconfigured gateway, or disabled gateway produce an unresolved result.
/// Never silently falls back between gateways (e.g. OmniRoute -> OpenRouter).
/// </summary>
public sealed class ModelGatewayResolver
{
    private readonly IModelGatewayRegistry _registry;
    private readonly ModelGatewayOptions _options;
    private readonly ExecutionOptions? _executionOptions;

    public ModelGatewayResolver(
        IModelGatewayRegistry registry,
        ModelGatewayOptions options,
        ExecutionOptions? executionOptions = null)
    {
        _registry = registry;
        _options = options;
        _executionOptions = executionOptions;
    }

    public ModelGatewayResolution Resolve(ModelRoute? route, string? executionProfile = null)
    {
        string? requestedKey = null;

        if (!string.IsNullOrWhiteSpace(route?.GatewayKey))
        {
            requestedKey = route.GatewayKey;
        }
        else if (!string.IsNullOrWhiteSpace(executionProfile)
            && _executionOptions is not null
            && _executionOptions.Profiles.TryGetValue(executionProfile, out var profile)
            && !string.IsNullOrWhiteSpace(profile.Gateway))
        {
            requestedKey = profile.Gateway;
        }
        else if (!string.IsNullOrWhiteSpace(_options.DefaultGateway))
        {
            requestedKey = _options.DefaultGateway;
        }

        if (string.IsNullOrWhiteSpace(requestedKey))
        {
            return ModelGatewayResolution.Failed(null, "No model gateway specified and no default gateway configured.");
        }

        return Resolve(requestedKey);
    }

    public ModelGatewayResolution Resolve(string gatewayKey)
    {
        if (string.IsNullOrWhiteSpace(gatewayKey))
        {
            return ModelGatewayResolution.Failed(null, "Gateway key cannot be null or empty.");
        }

        // Check if gateway is disabled in options
        if (_options.Gateways.TryGetValue(gatewayKey, out var entryOptions) && !entryOptions.Enabled)
        {
            return ModelGatewayResolution.Failed(gatewayKey, $"Model gateway '{gatewayKey}' is disabled in configuration.");
        }

        if (!_registry.TryGet(gatewayKey, out var gateway) || gateway is null)
        {
            return ModelGatewayResolution.Failed(gatewayKey, $"Model gateway '{gatewayKey}' is not registered.");
        }

        return new ModelGatewayResolution(true, gateway.GatewayKey, gateway, null);
    }

    public IModelGateway GetRequiredGateway(string gatewayKey)
    {
        var resolution = Resolve(gatewayKey);
        if (!resolution.IsResolved || resolution.Gateway is null)
        {
            if (_options.Gateways.TryGetValue(gatewayKey, out var entryOptions) && !entryOptions.Enabled)
            {
                throw new ModelGatewayDisabledException(gatewayKey);
            }

            throw new ModelGatewayNotFoundException(gatewayKey);
        }

        return resolution.Gateway;
    }
}

public sealed record ModelGatewayResolution(
    bool IsResolved,
    string? GatewayKey,
    IModelGateway? Gateway,
    string? FailureReason)
{
    public static ModelGatewayResolution Failed(string? gatewayKey, string failureReason) =>
        new(false, gatewayKey, null, failureReason);
}
