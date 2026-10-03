using AI.Factory.Infrastructure.Gateways;
using Xunit;

namespace AI.Factory.Application.Tests.Gateway;

/// <summary>
/// Phase K — OmniRoute as a transport descriptor. LiteLLM addresses an OpenAI-compatible endpoint
/// as <c>openai/&lt;model&gt;</c> and strips only that prefix, so the Factory model id — including
/// its own provider namespace — reaches OmniRoute unchanged.
/// </summary>
public sealed class OmniRouteGatewayTests
{
    [Theory]
    [InlineData("openai/qwen2.5-coder:7b", "openai/openai/qwen2.5-coder:7b")]
    [InlineData("deepseek/deepseek-v4", "openai/deepseek/deepseek-v4")]
    [InlineData("qwen2.5-coder:7b", "openai/qwen2.5-coder:7b")]
    [InlineData("openrouter/deepseek/deepseek-v4", "openai/openrouter/deepseek/deepseek-v4")]
    [InlineData("", "")]
    public void FormatModelIdentifier_PrefixesTheLiteLlmRoute_AndNeverRewritesTheModelId(string modelId, string expected)
    {
        Assert.Equal(expected, new OmniRouteGateway().FormatModelIdentifier(modelId));
    }

    [Fact]
    public void GetTransportInfo_ReturnsOmniRouteMetadata()
    {
        var info = new OmniRouteGateway().GetTransportInfo();

        Assert.Equal("http://localhost:20128/v1", info.BaseUrl);
        Assert.Equal("OMNIROUTE_API_KEY", info.ApiKeySecretName);
        Assert.Equal("OPENAI_API_KEY", info.EnvironmentVariableName);
        Assert.Equal("openai", info.CliProviderName);
    }
}
