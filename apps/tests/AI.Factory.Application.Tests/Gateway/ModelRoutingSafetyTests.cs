using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Abstractions.Execution;
using AI.Factory.Application.Abstractions.Gateway;
using AI.Factory.Application.Gateway;
using AI.Factory.Application.Orchestration;
using Xunit;

namespace AI.Factory.Application.Tests.Gateway;

/// <summary>
/// Phase P.1 — Model Routing Safety Tests.
/// Validates Group E:
/// - explicit valid model;
/// - auto/... rejected (openrouter/auto, auto/coding, any other auto identifier);
/// - unknown model rejected;
/// - invalid runtime mapping rejected.
/// </summary>
public sealed class ModelRoutingSafetyTests
{
    private static ModelRouter CreateRouter()
    {
        var options = new ModelRoutingOptions
        {
            Models = new List<ModelCapability>
            {
                new()
                {
                    ModelId = "deepseek/deepseek-v4-flash-0731",
                    Provider = "openrouter",
                    ReasoningStrength = 4,
                    MaxContextTokens = 128_000,
                    CostTier = 1,
                    IsCodingOptimized = true,
                    IsAvailable = true
                },
                new()
                {
                    ModelId = "anthropic/claude-sonnet-5",
                    Provider = "openrouter",
                    ReasoningStrength = 5,
                    MaxContextTokens = 200_000,
                    CostTier = 3,
                    IsCodingOptimized = true,
                    IsAvailable = true
                }
            },
            CategoryProfiles = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["coding"] = new List<string> { "deepseek/deepseek-v4-flash-0731" },
                ["code-review"] = new List<string> { "anthropic/claude-sonnet-5" }
            }
        };

        return new ModelRouter(options);
    }

    private static ModelGatewayResolver CreateResolver()
    {
        var options = new ModelGatewayOptions
        {
            DefaultGateway = "OpenRouter",
            Gateways = new Dictionary<string, GatewayEntryOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["OpenRouter"] = new()
                {
                    Enabled = true,
                    BaseUrl = "https://openrouter.ai/api/v1",
                    ApiKeySecretName = "OPENROUTER_API_KEY"
                },
                ["DisabledGateway"] = new()
                {
                    Enabled = false,
                    BaseUrl = "https://disabled.example.com",
                    ApiKeySecretName = "DISABLED_KEY"
                }
            }
        };

        var registry = new ModelGatewayRegistry();
        return new ModelGatewayResolver(registry, options);
    }

    [Theory]
    [InlineData("deepseek/deepseek-v4-flash-0731")]
    [InlineData("anthropic/claude-sonnet-5")]
    [InlineData("qwen/qwen3-coder")]
    [InlineData("openai/gpt-5.1")]
    public void IsAutoOrImplicitModel_ValidExplicitModels_ReturnFalse(string modelId)
    {
        Assert.False(ModelRoutingOptions.IsAutoOrImplicitModel(modelId));
    }

    [Theory]
    [InlineData("openrouter/auto")]
    [InlineData("auto/coding")]
    [InlineData("auto/coding:free")]
    [InlineData("auto")]
    [InlineData("AUTO")]
    [InlineData("provider/auto/coding")]
    public void IsAutoOrImplicitModel_AutoIdentifiers_ReturnTrue(string modelId)
    {
        Assert.True(ModelRoutingOptions.IsAutoOrImplicitModel(modelId));
    }

    [Theory]
    [InlineData("openrouter/auto")]
    [InlineData("auto/coding")]
    [InlineData("auto/coding:free")]
    public void ModelRouter_SelectModel_RejectsAutoModels_FailClosed(string autoModel)
    {
        var options = new ModelRoutingOptions
        {
            Models = new List<ModelCapability>
            {
                new() { ModelId = autoModel, Provider = "openrouter", IsAvailable = true }
            },
            CategoryProfiles = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["coding"] = new List<string> { autoModel }
            }
        };

        var router = new ModelRouter(options);
        var request = new ModelRoutingRequest("coding", TaskComplexity.Complex, 1000, CostPreference.BestQuality);

        Assert.Throws<NoModelAvailableException>(() => router.SelectModel(request));
    }

    [Fact]
    public void ModelGatewayResolver_RejectsAutoModels_FailClosed()
    {
        var resolver = CreateResolver();
        var route = new ModelRoute("openrouter/auto", "OpenRouter");

        var resolution = resolver.Resolve(route);

        Assert.False(resolution.IsResolved);
        Assert.Contains("auto or implicit", resolution.FailureReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ModelGatewayResolver_UnknownGateway_FailsClosed()
    {
        var resolver = CreateResolver();
        var route = new ModelRoute("deepseek/deepseek-v4-flash-0731", "NonExistentGateway");

        var resolution = resolver.Resolve(route);

        Assert.False(resolution.IsResolved);
        Assert.Contains("not registered", resolution.FailureReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ModelGatewayResolver_DisabledGateway_FailsClosed()
    {
        var resolver = CreateResolver();
        var route = new ModelRoute("deepseek/deepseek-v4-flash-0731", "DisabledGateway");

        var resolution = resolver.Resolve(route);

        Assert.False(resolution.IsResolved);
        Assert.Contains("disabled", resolution.FailureReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ModelRouter_SelectModel_ExplicitValidModel_Succeeds()
    {
        var router = CreateRouter();
        var request = new ModelRoutingRequest("coding", TaskComplexity.Complex, 1000, CostPreference.BestQuality);

        var selection = router.SelectModel(request);

        Assert.Equal("deepseek/deepseek-v4-flash-0731", selection.ModelId);
    }
}
