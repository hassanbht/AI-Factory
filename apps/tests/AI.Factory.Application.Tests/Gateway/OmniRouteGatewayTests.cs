using System.Net;
using AI.Factory.Application.Abstractions.Gateway;
using AI.Factory.Infrastructure.Gateways;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AI.Factory.Application.Tests.Gateway;

public sealed class OmniRouteGatewayTests
{
    private sealed class DelegatingHandlerStub : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        public DelegatingHandlerStub(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => _handler(request, cancellationToken);
    }

    private static (OmniRouteGateway Gateway, List<HttpRequestMessage> CapturedRequests) CreateGateway(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
    {
        var secretProvider = new FakeSecretProvider(new Dictionary<string, string?>
        {
            ["OMNIROUTE_API_KEY"] = "omniroute-key"
        });

        var captured = new List<HttpRequestMessage>();
        var handler = new DelegatingHandlerStub((req, ct) =>
        {
            captured.Add(req);
            return Task.FromResult(responseFactory(req));
        });

        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:8000/v1/")
        };

        var gateway = new OmniRouteGateway(
            client,
            secretProvider,
            NullLogger<OmniRouteGateway>.Instance);

        return (gateway, captured);
    }

    [Fact]
    public async Task ExecuteAsync_HappyPath_ParsesResponseAndUsage()
    {
        const string responseJson = """
        {
            "id": "omni-999",
            "model": "deepseek-coder",
            "choices": [
                {
                    "message": {
                        "role": "assistant",
                        "content": "Result from OmniRoute"
                    },
                    "finish_reason": "stop"
                }
            ],
            "usage": {
                "prompt_tokens": 15,
                "completion_tokens": 10,
                "total_tokens": 25
            }
        }
        """;

        var (gateway, captured) = CreateGateway(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson, System.Text.Encoding.UTF8, "application/json")
        });

        var request = new ModelGatewayRequest(
            ModelId: "deepseek-coder",
            Messages: new[] { new ModelGatewayMessage("user", "Hello") });

        var response = await gateway.ExecuteAsync(request);

        Assert.True(response.IsSuccess);
        Assert.Equal("Result from OmniRoute", response.Text);
        Assert.Equal("stop", response.FinishReason);
        Assert.Equal("omni-999", response.RequestId);
        Assert.NotNull(response.Usage);
        Assert.Equal(15, response.Usage.InputTokens);
        Assert.Equal(10, response.Usage.OutputTokens);
        Assert.Equal(25, response.Usage.TotalTokens);

        var sentRequest = Assert.Single(captured);
        Assert.Contains("http://localhost:20128/v1/chat/completions", sentRequest.RequestUri?.ToString());
    }

    [Fact]
    public async Task ExecuteAsync_NetworkException_ReturnsGatewayUnavailable()
    {
        var secretProvider = new FakeSecretProvider();
        var handler = new DelegatingHandlerStub((_, _) => throw new HttpRequestException("Connection refused"));
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:20128/v1/") };

        var gateway = new OmniRouteGateway(
            client,
            secretProvider,
            NullLogger<OmniRouteGateway>.Instance);

        var request = new ModelGatewayRequest("model", new[] { new ModelGatewayMessage("user", "Hi") });
        var response = await gateway.ExecuteAsync(request);

        Assert.False(response.IsSuccess);
        Assert.Equal(ModelGatewayFailureReason.GatewayUnavailable, response.Failure?.Reason);
        Assert.Contains("Connection refused", response.Failure?.Message);
    }

    [Fact]
    public void FormatModelIdentifier_StripsOpenRouterPrefix()
    {
        var (gateway, _) = CreateGateway(_ => new HttpResponseMessage(HttpStatusCode.OK));

        Assert.Equal("deepseek/deepseek-v4", gateway.FormatModelIdentifier("openrouter/deepseek/deepseek-v4"));
        Assert.Equal("deepseek-coder", gateway.FormatModelIdentifier("deepseek-coder"));
    }

    [Fact]
    public void GetTransportInfo_ReturnsOmniRouteMetadata()
    {
        var (gateway, _) = CreateGateway(_ => new HttpResponseMessage(HttpStatusCode.OK));

        var info = gateway.GetTransportInfo();

        Assert.Equal("http://localhost:20128/v1", info.BaseUrl);
        Assert.Equal("openai", info.CliProviderName);
    }
}
