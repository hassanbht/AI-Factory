using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Orchestration;
using Xunit;

namespace AI.Factory.Application.Tests;

public sealed class ModelRouterTests
{
    private static ModelRoutingOptions MakeOptions()
    {
        return new ModelRoutingOptions
        {
            Models = new List<ModelCapability>
            {
                new()
                {
                    ModelId = "strong-a",
                    Provider = "openrouter",
                    ReasoningStrength = 5,
                    MaxContextTokens = 200_000,
                    CostTier = 4,
                    IsCodingOptimized = false,
                    IsAvailable = true
                },
                new()
                {
                    ModelId = "strong-b",
                    Provider = "openrouter",
                    ReasoningStrength = 4,
                    MaxContextTokens = 128_000,
                    CostTier = 3,
                    IsCodingOptimized = false,
                    IsAvailable = true
                },
                new()
                {
                    ModelId = "coding-a",
                    Provider = "openrouter",
                    ReasoningStrength = 4,
                    MaxContextTokens = 128_000,
                    CostTier = 3,
                    IsCodingOptimized = true,
                    IsAvailable = true
                },
                new()
                {
                    ModelId = "cheap-a",
                    Provider = "openrouter",
                    ReasoningStrength = 2,
                    MaxContextTokens = 16_000,
                    CostTier = 1,
                    IsCodingOptimized = false,
                    IsAvailable = true
                },
                new()
                {
                    ModelId = "unavailable-a",
                    Provider = "openrouter",
                    ReasoningStrength = 5,
                    MaxContextTokens = 200_000,
                    CostTier = 1,
                    IsCodingOptimized = false,
                    IsAvailable = false
                }
            },
            CategoryProfiles = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["coding"] = new List<string> { "coding-a" },
                ["documentation"] = new List<string> { "cheap-a" },
                ["architecture"] = new List<string> { "strong-a", "strong-b" },
                ["fallback-test"] = new List<string> { "unavailable-a", "strong-b" },
                ["small-context-only"] = new List<string> { "cheap-a" }
            }
        };
    }

    [Fact]
    public void SelectModel_ReturnsConfiguredModel_ForKnownCategory()
    {
        var router = new ModelRouter(MakeOptions());

        var selection = router.SelectModel(new ModelRoutingRequest(
            "coding", TaskComplexity.Standard, 10_000, CostPreference.Balanced));

        Assert.Equal("coding-a", selection.ModelId);
        Assert.Equal("openrouter", selection.Provider);
    }

    [Fact]
    public void SelectModel_Throws_ForUnknownTaskCategory()
    {
        var router = new ModelRouter(MakeOptions());

        Assert.Throws<NoModelAvailableException>(() =>
            router.SelectModel(new ModelRoutingRequest(
                "unknown-category", TaskComplexity.Standard, 1_000, CostPreference.Balanced)));
    }

    [Fact]
    public void SelectModel_SkipsUnavailableModel_FallsBackToNextCandidate()
    {
        var router = new ModelRouter(MakeOptions());

        var selection = router.SelectModel(new ModelRoutingRequest(
            "fallback-test", TaskComplexity.Standard, 10_000, CostPreference.Balanced));

        Assert.Equal("strong-b", selection.ModelId);
    }

    [Fact]
    public void SelectModel_Throws_WhenContextSizeExceedsAllCandidates()
    {
        var router = new ModelRouter(MakeOptions());

        Assert.Throws<NoModelAvailableException>(() =>
            router.SelectModel(new ModelRoutingRequest(
                "small-context-only", TaskComplexity.Standard, 1_000_000, CostPreference.Balanced)));
    }

    [Fact]
    public void SelectModel_Throws_WhenComplexityExceedsAllCandidatesReasoning()
    {
        var router = new ModelRouter(MakeOptions());

        Assert.Throws<NoModelAvailableException>(() =>
            router.SelectModel(new ModelRoutingRequest(
                "documentation", TaskComplexity.Critical, 1_000, CostPreference.Balanced)));
    }

    [Fact]
    public void SelectModel_CostPreferenceCheapest_PicksLowestCostTier()
    {
        var router = new ModelRouter(MakeOptions());

        var selection = router.SelectModel(new ModelRoutingRequest(
            "architecture", TaskComplexity.Standard, 10_000, CostPreference.Cheapest));

        Assert.Equal("strong-b", selection.ModelId);
    }

    [Fact]
    public void SelectModel_CostPreferenceBestQuality_PicksHighestReasoningStrength()
    {
        var router = new ModelRouter(MakeOptions());

        var selection = router.SelectModel(new ModelRoutingRequest(
            "architecture", TaskComplexity.Standard, 10_000, CostPreference.BestQuality));

        Assert.Equal("strong-a", selection.ModelId);
    }

    [Fact]
    public void SelectModel_CostPreferenceBalanced_RespectsConfiguredOrder()
    {
        var router = new ModelRouter(MakeOptions());

        var selection = router.SelectModel(new ModelRoutingRequest(
            "architecture", TaskComplexity.Standard, 10_000, CostPreference.Balanced));

        Assert.Equal("strong-a", selection.ModelId);
    }
}
