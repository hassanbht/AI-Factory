using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Orchestration;
using Xunit;

namespace AI.Factory.Application.Tests;

/// <summary>
/// Phase 18.21 §13/§14/§15/§24 — role-based model routing and deterministic
/// configuration validation.
/// </summary>
public sealed class ModelRoutingConfigurationTests
{
    private static ModelCapability Model(string id, int reasoning = 4) => new()
    {
        ModelId = id,
        Provider = "openrouter",
        ReasoningStrength = reasoning,
        MaxContextTokens = 128_000,
        CostTier = 1,
        IsCodingOptimized = true,
        IsAvailable = true
    };

    /// <summary>A catalog where every role deliberately points at a different model.</summary>
    private static ModelRoutingOptions RolesWithDistinctModels() => new()
    {
        Models =
        {
            Model("impl-model"),
            Model("repair-model"),
            Model("review-model", reasoning: 5),
            Model("planner-model", reasoning: 5),
            Model("doc-model")
        },
        CategoryProfiles =
        {
            ["coding"] = new List<string> { "impl-model" },
            ["debugging"] = new List<string> { "repair-model" },
            ["code-review"] = new List<string> { "review-model" },
            ["planning"] = new List<string> { "planner-model" },
            ["documentation"] = new List<string> { "doc-model" }
        }
    };

    private static string SelectFor(ModelRoutingOptions options, string agentType)
    {
        var category = options.AgentTypeCategories[agentType];
        return new ModelRouter(options)
            .SelectModel(new ModelRoutingRequest(category, TaskComplexity.Standard, 16_000, CostPreference.Balanced))
            .ModelId;
    }

    [Theory]
    [InlineData("Implementation", "impl-model")]
    [InlineData("Repair", "repair-model")]
    [InlineData("Review", "review-model")]
    [InlineData("Planner", "planner-model")]
    [InlineData("Documentation", "doc-model")]
    public void EachRole_SelectsItsOwnConfiguredModel(string agentType, string expectedModelId)
    {
        // §14 — the whole point: Repair can be pointed at a different (e.g.
        // faster) model than Implementation without touching any code.
        Assert.Equal(expectedModelId, SelectFor(RolesWithDistinctModels(), agentType));
    }

    [Fact]
    public void DefaultRoleMapping_MatchesTheShippedCategoryNames()
    {
        var options = new ModelRoutingOptions();

        Assert.Equal("coding", options.AgentTypeCategories["Implementation"]);
        Assert.Equal("debugging", options.AgentTypeCategories["Repair"]);
        Assert.Equal("code-review", options.AgentTypeCategories["Review"]);
        Assert.Equal("planning", options.AgentTypeCategories["Planner"]);
        Assert.Equal("documentation", options.AgentTypeCategories["Documentation"]);
    }

    [Fact]
    public void RolesMayShareOneModel_WhichIsTheShippedDefault()
    {
        // §14 — "Do not require them to be different."
        var options = new ModelRoutingOptions
        {
            Models = { Model("one-model-for-everything") },
            CategoryProfiles =
            {
                ["coding"] = new List<string> { "one-model-for-everything" },
                ["debugging"] = new List<string> { "one-model-for-everything" },
                ["code-review"] = new List<string> { "one-model-for-everything" },
                ["planning"] = new List<string> { "one-model-for-everything" },
                ["testing"] = new List<string> { "one-model-for-everything" },
                ["documentation"] = new List<string> { "one-model-for-everything" }
            }
        };

        Assert.Equal("one-model-for-everything", SelectFor(options, "Implementation"));
        Assert.Equal("one-model-for-everything", SelectFor(options, "Repair"));
        Assert.Equal("one-model-for-everything", SelectFor(options, "Documentation"));
        Assert.Empty(options.Validate());
    }

    [Fact]
    public void Validate_PlaceholderModelIds_AreReported()
    {
        // §15 — exactly the catalog that shipped before this phase and that
        // silently broke the live review stage in Phase 18.20.
        var options = new ModelRoutingOptions
        {
            Models = { Model("independent-strong-model-a"), Model("coding-optimized-model-a") },
            CategoryProfiles =
            {
                ["code-review"] = new List<string> { "independent-strong-model-a" },
                ["coding"] = new List<string> { "coding-optimized-model-a" }
            }
        };

        var problems = options.Validate();

        Assert.Contains(problems, p =>
            p.Contains("independent-strong-model-a", StringComparison.Ordinal)
            && p.Contains("placeholder", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(problems, p =>
            p.Contains("coding-optimized-model-a", StringComparison.Ordinal)
            && p.Contains("placeholder", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_EmptyModelId_IsReported()
    {
        var options = new ModelRoutingOptions { Models = { Model("  ") } };

        Assert.Contains(options.Validate(), p => p.Contains("empty ModelId", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_CategoryReferencingUnknownModel_IsReported()
    {
        var options = new ModelRoutingOptions
        {
            Models = { Model("real-model") },
            CategoryProfiles = { ["coding"] = new List<string> { "does-not-exist" } }
        };

        Assert.Contains(options.Validate(), p => p.Contains("does-not-exist", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_EmptyCategoryProfile_IsReported()
    {
        var options = new ModelRoutingOptions
        {
            Models = { Model("real-model") },
            CategoryProfiles = { ["coding"] = new List<string>() }
        };

        Assert.Contains(options.Validate(), p => p.Contains("no candidate models", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RoleMappedToMissingCategory_IsReported()
    {
        var options = new ModelRoutingOptions
        {
            Models = { Model("real-model") },
            CategoryProfiles = { ["coding"] = new List<string> { "real-model" } }
        };

        // Repair maps to "debugging" by default, which has no profile here.
        Assert.Contains(options.Validate(), p =>
            p.Contains("Repair", StringComparison.Ordinal) && p.Contains("debugging", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_CoherentCatalog_ReportsNothing()
    {
        var options = RolesWithDistinctModels();
        options.AgentTypeCategories.Remove("Test");

        Assert.Empty(options.Validate());
    }
}
