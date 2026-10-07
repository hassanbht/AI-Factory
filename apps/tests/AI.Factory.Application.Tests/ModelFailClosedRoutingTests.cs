using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Abstractions.Execution;
using AI.Factory.Application.Abstractions.Gateway;
using AI.Factory.Application.Gateway;
using AI.Factory.Application.Orchestration;
using AI.Factory.Application.Execution;
using Xunit;

namespace AI.Factory.Application.Tests;

/// <summary>
/// Phase P.1 (suite E) — model selection is explicit, deterministic and fail-closed:
/// a valid explicit model routes; auto/... identifiers, malformed identifiers, unknown
/// models, unknown gateways and disabled gateways are all rejected without fallback.
/// </summary>
public sealed class ModelFailClosedRoutingTests
{
    private static ModelCapability Model(string id) => new()
    {
        ModelId = id,
        Provider = "openrouter",
        ReasoningStrength = 4,
        MaxContextTokens = 128_000,
        CostTier = 1,
        IsCodingOptimized = true,
        IsAvailable = true
    };

    private static ModelRoutingOptions OptionsWith(string id)
    {
        var options = new ModelRoutingOptions
        {
            Models = { Model(id) },
            CategoryProfiles =
            {
                ["coding"] = new List<string> { id },
                ["code-review"] = new List<string> { id }
            },
            // Replaced, not merged: only the two agent types this catalog configures, so every
            // mapped category has a profile and a fully coherent configuration carries zero
            // validation problems (the defaults map Repair/Test/Planner/Documentation too).
            AgentTypeCategories = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Implementation"] = "coding",
                ["Review"] = "code-review"
            }
        };

        return options;
    }

    // ---------------- explicit valid model ----------------

    [Fact]
    public void ExplicitValidModel_RoutesAndPassesEveryValidation()
    {
        const string model = "deepseek/deepseek-v4-flash-0731";
        var options = OptionsWith(model);

        Assert.Empty(options.Validate());
        Assert.Empty(options.ValidateModelIdentifiers());
        Assert.True(ModelIdentifier.IsAcceptable(model));

        var selection = new ModelRouter(options).SelectModel(
            new ModelRoutingRequest("coding", TaskComplexity.Standard, 16_000, CostPreference.Balanced));

        Assert.Equal(model, selection.ModelId);
    }

    // ---------------- auto/... rejection ----------------

    [Theory]
    [InlineData("auto/coding:free")]
    [InlineData("auto/coding")]
    [InlineData("openrouter/auto")]
    [InlineData("Auto")]
    public void AutomaticRouterIdentifiers_AreRejected(string modelId)
    {
        Assert.True(ModelIdentifier.IsAutomaticRouter(modelId));
        Assert.NotNull(ModelIdentifier.RejectReason(modelId));
        Assert.False(ModelIdentifier.IsAcceptable(modelId));
    }

    [Theory]
    [InlineData("auto/coding:free")]
    [InlineData("openrouter/auto")]
    public void AutomaticRouterIdentifiers_AreRejectedByConfigurationValidation(string id)
    {
        var options = OptionsWith(id);

        var problems = options.ValidateModelIdentifiers();
        Assert.Contains(problems, p => p.Contains(id, StringComparison.Ordinal) && p.Contains("automatic", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(options.Validate(), p => p.Contains(id, StringComparison.Ordinal) && p.Contains("automatic", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("auto/coding:free")]
    [InlineData("openrouter/auto")]
    public void AutomaticRouterIdentifier_AsTheCategoryCandidate_NeverReachesARuntime(string id)
    {
        var options = OptionsWith(id);

        var exception = Assert.Throws<NoModelAvailableException>(() =>
            new ModelRouter(options).SelectModel(
                new ModelRoutingRequest("coding", TaskComplexity.Standard, 16_000, CostPreference.Balanced)));

        Assert.Contains("automatic", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------- unknown model ----------------

    [Fact]
    public void UnknownModelInACategory_IsReported_AndTheRouterFailsClosed()
    {
        var options = new ModelRoutingOptions
        {
            Models = { Model("real-model") },
            CategoryProfiles = { ["coding"] = new List<string> { "does-not-exist" } }
        };

        Assert.Contains(options.Validate(), p => p.Contains("does-not-exist", StringComparison.Ordinal));

        var exception = Assert.Throws<NoModelAvailableException>(() =>
            new ModelRouter(options).SelectModel(
                new ModelRoutingRequest("coding", TaskComplexity.Standard, 16_000, CostPreference.Balanced)));

        Assert.Contains("No available model", exception.Message, StringComparison.Ordinal);
    }

    // ---------------- malformed identifiers ----------------

    [Theory]
    [InlineData(" bad-model")]
    [InlineData("bad-model ")]
    [InlineData("bad model")]
    [InlineData("bad\tmodel")]
    [InlineData("bad//model")]
    [InlineData("/leading")]
    [InlineData("trailing/")]
    public void MalformedIdentifiers_AreRejected(string modelId)
    {
        Assert.NotNull(ModelIdentifier.RejectReason(modelId));
        Assert.False(ModelIdentifier.IsAcceptable(modelId));
    }

    [Fact]
    public void MalformedIdentifier_InTheCatalog_IsRejectedByValidation()
    {
        var options = OptionsWith("bad model");

        Assert.Contains(
            options.ValidateModelIdentifiers(),
            p => p.Contains("malformed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EmptyModelIsNotAnIdentifierDefect_AbsenceIsADifferentPolicy()
    {
        Assert.Null(ModelIdentifier.RejectReason(null));
        Assert.Null(ModelIdentifier.RejectReason("   "));
        Assert.Null(ModelIdentifier.RejectReason(string.Empty));
    }

    // ---------------- gateway fail-closed (unknown / disabled) ----------------

    private static ModelGatewayOptions GatewayOptions(string defaultGateway = "OpenRouter", bool enabled = true) => new()
    {
        DefaultGateway = defaultGateway,
        Gateways =
        {
            ["OpenRouter"] = new GatewayEntryOptions { Enabled = enabled, BaseUrl = "https://openrouter.ai/api/v1" }
        }
    };

    [Fact]
    public void UnknownGateway_FailsClosed()
    {
        var resolver = new ModelGatewayResolver(new EmptyGatewayRegistry(), GatewayOptions());

        var explicitRoute = resolver.Resolve("NotAGateway");
        Assert.False(explicitRoute.IsResolved);
        Assert.Contains("not registered", explicitRoute.FailureReason);

        var unrouted = resolver.Resolve((ModelRoute?)null);
        Assert.False(unrouted.IsResolved);
    }

    [Fact]
    public void DisabledGateway_FailsClosed()
    {
        var resolver = new ModelGatewayResolver(new EmptyGatewayRegistry(), GatewayOptions(enabled: false));

        var resolution = resolver.Resolve("OpenRouter");

        Assert.False(resolution.IsResolved);
        Assert.Contains("disabled", resolution.FailureReason);
    }

    private sealed class EmptyGatewayRegistry : IModelGatewayRegistry
    {
        public void Register(IModelGateway gateway) => throw new NotSupportedException();

        public bool TryGet(string gatewayKey, out IModelGateway? gateway)
        {
            gateway = null;
            return false;
        }

        public IModelGateway Get(string gatewayKey) => throw new KeyNotFoundException();

        public IReadOnlyList<string> Keys => Array.Empty<string>();

        public IReadOnlyCollection<IModelGateway> GetAll() => Array.Empty<IModelGateway>();
    }
}
