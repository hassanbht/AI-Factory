using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Orchestration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AI.Factory.Application.Tests;

public sealed class AgentCodeReviewerTests
{
    private sealed class FakeAgentExecutor : IAgentExecutor
    {
        private readonly Func<AgentExecutionRequest, AgentExecutionResult> _respond;

        public FakeAgentExecutor(Func<AgentExecutionRequest, AgentExecutionResult> respond)
        {
            _respond = respond;
        }

        public AgentExecutionRequest? LastRequest { get; private set; }

        public Task<AgentExecutionResult> ExecuteAsync(
            AgentExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(_respond(request));
        }
    }

    private static ModelRouter MakeModelRouter()
    {
        var options = new ModelRoutingOptions
        {
            Models = new List<ModelCapability>
            {
                new()
                {
                    ModelId = "independent-review-model",
                    Provider = "test-provider",
                    ReasoningStrength = 5,
                    MaxContextTokens = 200_000,
                    CostTier = 4,
                    IsCodingOptimized = false,
                    IsAvailable = true
                }
            },
            CategoryProfiles = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["code-review"] = new List<string> { "independent-review-model" }
            }
        };

        return new ModelRouter(options);
    }

    private static ReviewContext MakeContext()
    {
        return new ReviewContext(
            Guid.NewGuid(), Guid.NewGuid(),
            "req text", "acceptance", "arch text",
            new[] { "file1.cs" }, "test output", true, "build output", true, "history");
    }

    [Fact]
    public async Task ReviewAsync_NeverPassesAnIdentifierAsTheWorkingDirectory()
    {
        // Phase 18.20 — regression lock for a defect only a live run could
        // surface: AgentExecutionRequest.WorkingDirectory is a real filesystem
        // path (the agent container's bind mount) since Phase 18.14, but this
        // reviewer passed ProjectId.ToString(). Against real Hermes every
        // review then failed with "Resolved workspace '<guid>' no longer
        // exists", which became a Critical finding, blocked completion, and
        // exhausted the WorkItem's repair budget — so no WorkItem could ever
        // reach Completed. Empty selects the executor's fresh isolated scratch
        // workspace, which is all a review prompt needs.
        var executor = new FakeAgentExecutor(_ => new AgentExecutionResult(true, "[]", null, 5));
        var reviewer = new AgentCodeReviewer(executor, MakeModelRouter(), NullLogger<AgentCodeReviewer>.Instance);
        var context = MakeContext();

        await reviewer.ReviewAsync(context);

        Assert.NotNull(executor.LastRequest);
        Assert.True(
            string.IsNullOrEmpty(executor.LastRequest!.WorkingDirectory),
            $"Reviewer passed '{executor.LastRequest.WorkingDirectory}' as WorkingDirectory — it must be a real path or empty, never an identifier.");
        Assert.False(Guid.TryParse(executor.LastRequest.WorkingDirectory, out _));
    }

    [Fact]
    public async Task ReviewAsync_UsesIndependentAgentNameAndModel()
    {
        var executor = new FakeAgentExecutor(_ =>
            new AgentExecutionResult(true, "[]", null, 5));

        var reviewer = new AgentCodeReviewer(executor, MakeModelRouter(), NullLogger<AgentCodeReviewer>.Instance);

        await reviewer.ReviewAsync(MakeContext());

        Assert.Equal("reviewer", executor.LastRequest!.Agent);
        Assert.Equal("independent-review-model", executor.LastRequest.Model);
    }

    [Fact]
    public async Task ReviewAsync_ParsesStructuredFindings()
    {
        var json = """
            [
              {"severity":"High","category":"Security","file":"Auth.cs","line":42,"description":"SQL injection risk","recommendation":"Use parameterized queries"}
            ]
            """;

        var executor = new FakeAgentExecutor(_ => new AgentExecutionResult(true, json, null, 5));
        var reviewer = new AgentCodeReviewer(executor, MakeModelRouter(), NullLogger<AgentCodeReviewer>.Instance);

        var result = await reviewer.ReviewAsync(MakeContext());

        Assert.True(result.BlocksCompletion);
        Assert.Single(result.Findings);
        Assert.Equal(ReviewFindingSeverity.High, result.Findings[0].Severity);
        Assert.Equal(ReviewCategory.Security, result.Findings[0].Category);
        Assert.Equal("Auth.cs", result.Findings[0].File);
        Assert.Equal(42, result.Findings[0].Line);
    }

    [Fact]
    public async Task ReviewAsync_DoesNotBlock_ForLowAndInfoFindingsOnly()
    {
        var json = """[{"severity":"Low","category":"Maintainability","description":"minor style issue","recommendation":"consider renaming"}]""";

        var executor = new FakeAgentExecutor(_ => new AgentExecutionResult(true, json, null, 5));
        var reviewer = new AgentCodeReviewer(executor, MakeModelRouter(), NullLogger<AgentCodeReviewer>.Instance);

        var result = await reviewer.ReviewAsync(MakeContext());

        Assert.False(result.BlocksCompletion);
        Assert.Single(result.Findings);
    }

    [Fact]
    public async Task ReviewAsync_ReturnsEmptyFindings_ForEmptyJsonArray()
    {
        var executor = new FakeAgentExecutor(_ => new AgentExecutionResult(true, "[]", null, 5));
        var reviewer = new AgentCodeReviewer(executor, MakeModelRouter(), NullLogger<AgentCodeReviewer>.Instance);

        var result = await reviewer.ReviewAsync(MakeContext());

        Assert.False(result.BlocksCompletion);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task ReviewAsync_DegradesToInfoFinding_WhenOutputIsNotValidJson()
    {
        var executor = new FakeAgentExecutor(_ =>
            new AgentExecutionResult(true, "this is not json at all", null, 5));

        var reviewer = new AgentCodeReviewer(executor, MakeModelRouter(), NullLogger<AgentCodeReviewer>.Instance);

        var result = await reviewer.ReviewAsync(MakeContext());

        Assert.False(result.BlocksCompletion);
        Assert.Single(result.Findings);
        Assert.Equal(ReviewFindingSeverity.Info, result.Findings[0].Severity);
        Assert.Contains("could not be parsed", result.Findings[0].Description);
    }

    [Fact]
    public async Task ReviewAsync_ReturnsBlockingCriticalFinding_WhenReviewerAgentFails()
    {
        var executor = new FakeAgentExecutor(_ =>
            new AgentExecutionResult(false, null, "docker not available", 5));

        var reviewer = new AgentCodeReviewer(executor, MakeModelRouter(), NullLogger<AgentCodeReviewer>.Instance);

        var result = await reviewer.ReviewAsync(MakeContext());

        Assert.True(result.BlocksCompletion);
        Assert.Single(result.Findings);
        Assert.Equal(ReviewFindingSeverity.Critical, result.Findings[0].Severity);
        Assert.Contains("docker not available", result.Findings[0].Description);
    }
}
