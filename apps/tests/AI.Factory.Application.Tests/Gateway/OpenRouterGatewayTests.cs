using System.Net;
using AI.Factory.Application.Abstractions.Gateway;
using AI.Factory.Infrastructure.Gateways;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AI.Factory.Application.Tests.Gateway;

public sealed class OpenRouterGatewayTests
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

    private static (OpenRouterGateway Gateway, FakeSecretProvider SecretProvider, List<HttpRequestMessage> CapturedRequests) CreateGateway(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory,
        string? apiKey = "sk-or-v1-testkey")
    {
        var secretProvider = new FakeSecretProvider(new Dictionary<string, string?>
        {
            ["OPENROUTER_API_KEY"] = apiKey
        });

        var captured = new List<HttpRequestMessage>();
        var handler = new DelegatingHandlerStub((req, ct) =>
        {
            captured.Add(req);
            return Task.FromResult(responseFactory(req));
        });

        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://openrouter.ai/api/v1/")
        };

        var gateway = new OpenRouterGateway(
            client,
            secretProvider,
            NullLogger<OpenRouterGateway>.Instance);

        return (gateway, secretProvider, captured);
    }

    [Fact]
    public async Task ExecuteAsync_HappyPath_ParsesResponseUsageAndHeaders()
    {
        const string responseJson = """
        {
            "id": "gen-12345",
            "model": "deepseek/deepseek-v4-flash-0731",
            "choices": [
                {
                    "message": {
                        "role": "assistant",
                        "content": "Hello from OpenRouter!"
                    },
                    "finish_reason": "stop"
                }
            ],
            "usage": {
                "prompt_tokens": 12,
                "completion_tokens": 8,
                "total_tokens": 20
            }
        }
        """;

        var (gateway, _, captured) = CreateGateway(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson, System.Text.Encoding.UTF8, "application/json")
        });

        var request = new ModelGatewayRequest(
            ModelId: "deepseek/deepseek-v4-flash-0731",
            Messages: new[] { new ModelGatewayMessage("user", "Hi") },
            MaxTokens: 50,
            Temperature: 0.2f);

        var response = await gateway.ExecuteAsync(request);

        Assert.True(response.IsSuccess);
        Assert.Equal("Hello from OpenRouter!", response.Text);
        Assert.Equal("stop", response.FinishReason);
        Assert.Equal("gen-12345", response.RequestId);
        Assert.NotNull(response.Usage);
        Assert.Equal(12, response.Usage.InputTokens);
        Assert.Equal(8, response.Usage.OutputTokens);
        Assert.Equal(20, response.Usage.TotalTokens);

        var sentRequest = Assert.Single(captured);
        Assert.Equal("Bearer", sentRequest.Headers.Authorization?.Scheme);
        Assert.Equal("sk-or-v1-testkey", sentRequest.Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task ExecuteAsync_MissingApiKey_FailsClosedWithAuthenticationFailure()
    {
        var (gateway, _, _) = CreateGateway(_ => new HttpResponseMessage(HttpStatusCode.OK), apiKey: null);

        var request = new ModelGatewayRequest(
            ModelId: "deepseek/deepseek-v4-flash-0731",
            Messages: new[] { new ModelGatewayMessage("user", "Hi") });

        var response = await gateway.ExecuteAsync(request);

        Assert.False(response.IsSuccess);
        Assert.NotNull(response.Failure);
        Assert.Equal(ModelGatewayFailureReason.Authentication, response.Failure.Reason);
        Assert.Contains("API key", response.Failure.Message);
    }

    [Fact]
    public async Task ExecuteAsync_Http402_ClassifiedAsBillingFailure()
    {
        const string errorJson = """
        {
            "error": {
                "message": "Insufficient credits",
                "code": 402
            }
        }
        """;

        var (gateway, _, _) = CreateGateway(_ => new HttpResponseMessage((HttpStatusCode)402)
        {
            Content = new StringContent(errorJson, System.Text.Encoding.UTF8, "application/json")
        });

        var request = new ModelGatewayRequest(
            ModelId: "deepseek/deepseek-v4-flash-0731",
            Messages: new[] { new ModelGatewayMessage("user", "Hi") });

        var response = await gateway.ExecuteAsync(request);

        Assert.False(response.IsSuccess);
        Assert.NotNull(response.Failure);
        Assert.Equal(ModelGatewayFailureReason.Billing, response.Failure.Reason);
        Assert.Equal(402, response.Failure.StatusCode);
        Assert.Contains("Insufficient credits", response.Failure.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, ModelGatewayFailureReason.Authentication)]
    [InlineData(HttpStatusCode.Forbidden, ModelGatewayFailureReason.Authorization)]
    [InlineData(HttpStatusCode.BadRequest, ModelGatewayFailureReason.InvalidRequest)]
    [InlineData((HttpStatusCode)429, ModelGatewayFailureReason.RateLimited)]
    [InlineData(HttpStatusCode.RequestTimeout, ModelGatewayFailureReason.Timeout)]
    [InlineData(HttpStatusCode.NotFound, ModelGatewayFailureReason.ModelUnavailable)]
    [InlineData(HttpStatusCode.BadGateway, ModelGatewayFailureReason.GatewayUnavailable)]
    [InlineData(HttpStatusCode.ServiceUnavailable, ModelGatewayFailureReason.GatewayUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout, ModelGatewayFailureReason.Timeout)]
    [InlineData(HttpStatusCode.InternalServerError, ModelGatewayFailureReason.ProviderUnavailable)]
    public async Task ExecuteAsync_StatusCodes_MapToCorrectFailureReason(HttpStatusCode statusCode, ModelGatewayFailureReason expectedReason)
    {
        var (gateway, _, _) = CreateGateway(_ => new HttpResponseMessage(statusCode)
        {
            Content = new StringContent($"{{\"error\": \"Error message for {(int)statusCode}\"}}", System.Text.Encoding.UTF8, "application/json")
        });

        var request = new ModelGatewayRequest(
            ModelId: "test-model",
            Messages: new[] { new ModelGatewayMessage("user", "Test") });

        var response = await gateway.ExecuteAsync(request);

        Assert.False(response.IsSuccess);
        Assert.NotNull(response.Failure);
        Assert.Equal(expectedReason, response.Failure.Reason);
        Assert.Equal((int)statusCode, response.Failure.StatusCode);
    }

    [Fact]
    public void FormatModelIdentifier_AddsOpenRouterPrefixIfMissing()
    {
        var (gateway, _, _) = CreateGateway(_ => new HttpResponseMessage(HttpStatusCode.OK));

        Assert.Equal("openrouter/deepseek/deepseek-v4", gateway.FormatModelIdentifier("deepseek/deepseek-v4"));
        Assert.Equal("openrouter/deepseek/deepseek-v4", gateway.FormatModelIdentifier("openrouter/deepseek/deepseek-v4"));
        Assert.Equal("", gateway.FormatModelIdentifier(""));
    }

    [Fact]
    public void GetTransportInfo_ReturnsCleanTransportMetadata()
    {
        var (gateway, _, _) = CreateGateway(_ => new HttpResponseMessage(HttpStatusCode.OK));

        var info = gateway.GetTransportInfo();

        Assert.Equal("https://openrouter.ai/api/v1", info.BaseUrl);
        Assert.Equal("OPENROUTER_API_KEY", info.ApiKeySecretName);
        Assert.Equal("OPENROUTER_API_KEY", info.EnvironmentVariableName);
        Assert.Equal("openrouter", info.CliProviderName);
    }
}
