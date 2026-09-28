using AI.Factory.Application.Abstractions.Gateway;

namespace AI.Factory.Application.Tests.Gateway;

public sealed class FakeModelGateway : IModelGateway
{
    private readonly Func<ModelGatewayRequest, CancellationToken, Task<ModelGatewayResponse>> _execute;

    public FakeModelGateway(
        string key,
        ModelGatewayCapabilities capabilities = ModelGatewayCapabilities.Chat,
        Func<ModelGatewayRequest, CancellationToken, Task<ModelGatewayResponse>>? execute = null)
    {
        GatewayKey = key;
        Capabilities = capabilities;
        _execute = execute ?? new Func<ModelGatewayRequest, CancellationToken, Task<ModelGatewayResponse>>(
            (req, _) => Task.FromResult(ModelGatewayResponse.Successful(key, req.ModelId, "fake response", "stop", null, "req-1", 10)));
    }

    public string GatewayKey { get; }

    public ModelGatewayCapabilities Capabilities { get; }

    public int CallCount { get; private set; }

    public ModelGatewayRequest? LastRequest { get; private set; }

    public Task<ModelGatewayResponse> ExecuteAsync(ModelGatewayRequest request, CancellationToken cancellationToken = default)
    {
        CallCount++;
        LastRequest = request;
        return _execute(request, cancellationToken);
    }

    public string FormatModelIdentifier(string modelId) => modelId;

    public ModelGatewayTransportInfo GetTransportInfo() =>
        new("https://fake.gateway/v1", $"{GatewayKey.ToUpperInvariant()}_API_KEY", $"{GatewayKey.ToUpperInvariant()}_API_KEY", GatewayKey.ToLowerInvariant());
}
