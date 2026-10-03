using AI.Factory.Infrastructure.Gateways;
using Xunit;

namespace AI.Factory.Application.Tests.Gateway;

/// <summary>
/// Phase K — OpenRouter as a transport descriptor: the LiteLLM model route OpenHands receives and
/// the endpoint/credential reference both runtimes are given. The model request itself is sent
/// by the runtime, so there is no HTTP behaviour to test here.
/// </summary>
public sealed class OpenRouterGatewayTests
{
    [Theory]
    [InlineData("deepseek/deepseek-v4-flash-0731:free", "openrouter/deepseek/deepseek-v4-flash-0731:free")]
    [InlineData("anthropic/claude-sonnet-5", "openrouter/anthropic/claude-sonnet-5")]
    [InlineData("openrouter/anthropic/claude-sonnet-5", "openrouter/anthropic/claude-sonnet-5")]
    [InlineData("", "")]
    public void FormatModelIdentifier_IsTheDeterministicLiteLlmRoute_NeverDoublePrefixed(string modelId, string expected)
    {
        Assert.Equal(expected, new OpenRouterGateway().FormatModelIdentifier(modelId));
    }

    [Fact]
    public void FormatModelIdentifier_NeverInventsAnAutoRouter()
    {
        var formatted = new OpenRouterGateway().FormatModelIdentifier("qwen/qwen3-coder");

        Assert.DoesNotContain("auto", formatted, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("qwen/qwen3-coder", formatted);
    }

    [Fact]
    public void GetTransportInfo_ReturnsEndpointAndSecretNameOnly()
    {
        var info = new OpenRouterGateway().GetTransportInfo();

        Assert.Equal("https://openrouter.ai/api/v1", info.BaseUrl);
        Assert.Equal("OPENROUTER_API_KEY", info.ApiKeySecretName);
        Assert.Equal("OPENROUTER_API_KEY", info.EnvironmentVariableName);
        Assert.Equal("openrouter", info.CliProviderName);
    }

    [Fact]
    public void GetTransportInfo_HonoursConfiguredEndpointAndSecretName()
    {
        var info = new OpenRouterGateway("https://example.invalid/api/v1/", "CUSTOM_OPENROUTER_KEY").GetTransportInfo();

        Assert.Equal("https://example.invalid/api/v1", info.BaseUrl);
        Assert.Equal("CUSTOM_OPENROUTER_KEY", info.ApiKeySecretName);
    }
}
