using AI.Factory.Application.Abstractions.Gateway;

namespace AI.Factory.Application.Tests.Gateway;

public sealed class FakeModelGateway : IModelGateway
{
    public FakeModelGateway(string key) => GatewayKey = key;

    public string GatewayKey { get; }

    public string FormatModelIdentifier(string modelId) => modelId;

    public ModelGatewayTransportInfo GetTransportInfo() =>
        new("https://fake.gateway/v1", $"{GatewayKey.ToUpperInvariant()}_API_KEY", $"{GatewayKey.ToUpperInvariant()}_API_KEY", GatewayKey.ToLowerInvariant());
}
