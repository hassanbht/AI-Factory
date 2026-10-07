using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Abstractions.Execution;
using AI.Factory.Application.Orchestration;
using AI.Factory.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AI.Factory.Application.Tests;

public sealed class AgentCodeReviewerTests
{
    private sealed class FakeAgentExecutionDispatcher : IAgentExecutionDispatcher
    {
        private readonly Func<AgentExecutionSpec, ExecutionOutcome> _respond;

        public FakeAgentExecutionDispatcher(Func<AgentExecutionSpec, ExecutionOutcome> respond)
        {
            _respond = respond;
        }

        public AgentExecutionSpec? LastSpec { get; private set; }

        public Task<ExecutionOutcome> DispatchAsync(
            AgentExecutionSpec spec,
            CancellationToken cancellationToken = default)
        {
            LastSpec = spec;
            return Task.FromResult(_respond(spec));
        }
    }

    private static ExecutionOutcome Outcome(
        ExecutionOutcomeStatus status,
        string? output,
        string? errorMessage = null,
        FailureCategory? category = null) => new(
            status,
            output,
            errorMessage,
            category,
            DurationMs: 5,
            Usage: null,
            ArtifactReferences: Array.Empty<string>(),
            ToolCallNames: Array.Empty<string>(),
            Model: new ModelRoute("independent-review-model", null),
            ExitReason: status == ExecutionOutcomeStatus.Succeeded ? "exit:0" : "failed");

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
        // Phase 18.20 / Phase H — regression lock: AgentExecutionSpec.Workspace.Path
        // is the directory bind-mounted into the agent container. Empty selects
        // the executor's fresh isolated scratch workspace, which is all a review
        // prompt needs.
        var dispatcher = new FakeAgentExecutionDispatcher(_ => Outcome(ExecutionOutcomeStatus.Succeeded, "[]"));
        var reviewer = new AgentCodeReviewer(dispatcher, MakeModelRouter(), NullLogger<AgentCodeReviewer>.Instance);
        var context = MakeContext();

        await reviewer.ReviewAsync(context);

        Assert.NotNull(dispatcher.LastSpec);
        Assert.True(
            string.IsNullOrEmpty(dispatcher.LastSpec!.Workspace.Path),
            $"Reviewer passed '{dispatcher.LastSpec.Workspace.Path}' as Workspace.Path — it must be empty or a real path, never an identifier.");
        Assert.False(Guid.TryParse(dispatcher.LastSpec.Workspace.Path, out _));
    }

    [Fact]
    public async Task ReviewAsync_UsesIndependentAgentNameAndModel()
    {
        var dispatcher = new FakeAgentExecutionDispatcher(_ => Outcome(ExecutionOutcomeStatus.Succeeded, "[]"));
        var reviewer = new AgentCodeReviewer(dispatcher, MakeModelRouter(), NullLogger<AgentCodeReviewer>.Instance);

        await reviewer.ReviewAsync(MakeContext());

        Assert.NotNull(dispatcher.LastSpec);
        Assert.Equal("reviewer", dispatcher.LastSpec!.Agent.Name);
        Assert.Equal("Review", dispatcher.LastSpec.Agent.AgentType);
        Assert.Equal("independent-review-model", dispatcher.LastSpec.Model.ModelId);
    }

    [Fact]
    public async Task ReviewAsync_ParsesStructuredFindings()
    {
        var json = """
            [
              {"severity":"High","category":"Security","file":"Auth.cs","line":42,"description":"SQL injection risk","recommendation":"Use parameterized queries"}
            ]
            """;

        var dispatcher = new FakeAgentExecutionDispatcher(_ => Outcome(ExecutionOutcomeStatus.Succeeded, json));
        var reviewer = new AgentCodeReviewer(dispatcher, MakeModelRouter(), NullLogger<AgentCodeReviewer>.Instance);

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

        var dispatcher = new FakeAgentExecutionDispatcher(_ => Outcome(ExecutionOutcomeStatus.Succeeded, json));
        var reviewer = new AgentCodeReviewer(dispatcher, MakeModelRouter(), NullLogger<AgentCodeReviewer>.Instance);

        var result = await reviewer.ReviewAsync(MakeContext());

        Assert.False(result.BlocksCompletion);
        Assert.Single(result.Findings);
    }

    [Fact]
    public async Task ReviewAsync_ReturnsEmptyFindings_ForEmptyJsonArray()
    {
        var dispatcher = new FakeAgentExecutionDispatcher(_ => Outcome(ExecutionOutcomeStatus.Succeeded, "[]"));
        var reviewer = new AgentCodeReviewer(dispatcher, MakeModelRouter(), NullLogger<AgentCodeReviewer>.Instance);

        var result = await reviewer.ReviewAsync(MakeContext());

        Assert.False(result.BlocksCompletion);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task ReviewAsync_ReturnsBlockingCriticalFinding_WhenOutputIsMalformed()
    {
        var dispatcher = new FakeAgentExecutionDispatcher(_ => Outcome(ExecutionOutcomeStatus.Succeeded, "this is not json at all"));
        var reviewer = new AgentCodeReviewer(dispatcher, MakeModelRouter(), NullLogger<AgentCodeReviewer>.Instance);

        var result = await reviewer.ReviewAsync(MakeContext());

        Assert.True(result.BlocksCompletion);
        Assert.Equal(ReviewExecutionStatus.MalformedPayload, result.Status);
        Assert.Single(result.Findings);
        Assert.Equal(ReviewFindingSeverity.Critical, result.Findings[0].Severity);
        Assert.Contains("malformed", result.Findings[0].Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReviewAsync_ReturnsBlockingCriticalFinding_WhenReviewerAgentFails()
    {
        var dispatcher = new FakeAgentExecutionDispatcher(_ => Outcome(
            ExecutionOutcomeStatus.Failed,
            null,
            "docker not available",
            FailureCategory.Infrastructure));

        var reviewer = new AgentCodeReviewer(dispatcher, MakeModelRouter(), NullLogger<AgentCodeReviewer>.Instance);

        var result = await reviewer.ReviewAsync(MakeContext());

        Assert.True(result.BlocksCompletion);
        Assert.Single(result.Findings);
        Assert.Equal(ReviewFindingSeverity.Critical, result.Findings[0].Severity);
        Assert.Contains("docker not available", result.Findings[0].Description);
    }
}
