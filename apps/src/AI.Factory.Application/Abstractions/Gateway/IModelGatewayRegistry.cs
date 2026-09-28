namespace AI.Factory.Application.Abstractions.Gateway;

/// <summary>
/// Registry of available <see cref="IModelGateway"/> implementations.
/// Key lookup is case-insensitive. Never silently substitutes an unregistered or alternative gateway.
/// </summary>
public interface IModelGatewayRegistry
{
    void Register(IModelGateway gateway);

    bool TryGet(string gatewayKey, out IModelGateway? gateway);

    IModelGateway Get(string gatewayKey);

    IModelGateway GetRequired(string gatewayKey) => Get(gatewayKey);

    IReadOnlyList<string> Keys { get; }

    IReadOnlyCollection<IModelGateway> GetAll();
}
