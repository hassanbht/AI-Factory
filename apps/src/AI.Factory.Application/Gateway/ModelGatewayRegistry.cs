using AI.Factory.Application.Abstractions.Gateway;

namespace AI.Factory.Application.Gateway;

/// <summary>
/// Thread-safe in-memory registry of <see cref="IModelGateway"/> implementations.
/// Key lookup is case-insensitive. Fails closed on duplicates and unknown keys.
/// </summary>
public sealed class ModelGatewayRegistry : IModelGatewayRegistry
{
    private readonly Dictionary<string, IModelGateway> _gateways =
        new(StringComparer.OrdinalIgnoreCase);

    public ModelGatewayRegistry(IEnumerable<IModelGateway>? gateways = null)
    {
        if (gateways is not null)
        {
            foreach (var gateway in gateways)
            {
                Register(gateway);
            }
        }
    }

    public void Register(IModelGateway gateway)
    {
        ArgumentNullException.ThrowIfNull(gateway);

        if (string.IsNullOrWhiteSpace(gateway.GatewayKey))
        {
            throw new ArgumentException("Gateway key cannot be null or empty.", nameof(gateway));
        }

        if (_gateways.ContainsKey(gateway.GatewayKey))
        {
            throw new DuplicateModelGatewayException(gateway.GatewayKey);
        }

        _gateways[gateway.GatewayKey] = gateway;
    }

    public bool TryGet(string gatewayKey, out IModelGateway? gateway)
    {
        if (string.IsNullOrWhiteSpace(gatewayKey))
        {
            gateway = null;
            return false;
        }

        return _gateways.TryGetValue(gatewayKey, out gateway);
    }

    public IModelGateway Get(string gatewayKey)
    {
        if (!TryGet(gatewayKey, out var gateway) || gateway is null)
        {
            throw new ModelGatewayNotFoundException(gatewayKey ?? "(null)");
        }

        return gateway;
    }

    public IModelGateway GetRequired(string gatewayKey) => Get(gatewayKey);

    public IReadOnlyList<string> Keys => _gateways.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();

    public IReadOnlyCollection<IModelGateway> GetAll() => _gateways.Values.ToList();
}
