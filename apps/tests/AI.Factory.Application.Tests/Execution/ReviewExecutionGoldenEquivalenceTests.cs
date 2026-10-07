using System.Text.Json;
using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Abstractions.Execution;
using AI.Factory.Application.Execution;
using AI.Factory.Application.Orchestration;
using AI.Factory.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AI.Factory.Application.Tests.Execution;

/// <summary>
/// Phase H §12 — Golden Equivalence Tests.
/// Proves bit-for-bit behavioral equivalence between the legacy AgentCodeReviewer
/// (IAgentExecutor -> HermesAgentExecutor) and the migrated AgentCodeReviewer
/// (IAgentExecutionDispatcher -> AdapterDispatchingAgentRuntime -> IExecutionAdapter).
/// For identical deterministic outputs, the Review verdict, findings, severities,
/// categories, and blocking behavior must match exactly.
/// </summary>
public sealed class ReviewExecutionGoldenEquivalenceTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    #region Legacy Reference Implementation

    /// <summary>
    /// Exact representation of the pre-migration AgentCodeReviewer logic
    /// before Phase H replaced IAgentExecutor with IAgentExecutionDispatcher.
    /// </summary>
    private sealed class LegacyAgentCodeReviewer
    {
        private const int EstimatedContextTokens = 16_000;
        private const string ReviewCategoryKey = "code-review";
        private const string ReviewAgentName = "reviewer";

        private readonly IAgentExecutor _agentExecutor;
        private readonly IModelRouter _modelRouter;

        public LegacyAgentCodeReviewer(IAgentExecutor agentExecutor, IModelRouter modelRouter)
        {
            _agentExecutor = agentExecutor;
            _modelRouter = modelRouter;
        }

        public async Task<ReviewResult> ReviewAsync(ReviewContext context, CancellationToken cancellationToken = default)
        {
            var modelSelection = _modelRouter.SelectModel(new ModelRoutingRequest(
                ReviewCategoryKey, TaskComplexity.Complex, EstimatedContextTokens, CostPreference.BestQuality));

            var prompt = BuildPrompt(context);
            var result = await _agentExecutor.ExecuteAsync(
                new AgentExecutionRequest(ReviewAgentName, modelSelection.ModelId, prompt, string.Empty),
                cancellationToken);

            if (!result.Success)
            {
                var isTimeout = result.TimedOut || result.Error?.Contains("timeout", StringComparison.OrdinalIgnoreCase) == true || result.Error?.Contains("deadline", StringComparison.OrdinalIgnoreCase) == true;
                var isCancelled = result.Error?.Contains("cancell", StringComparison.OrdinalIgnoreCase) == true;

                var prefix = isTimeout
                    ? "Independent review timed out"
                    : isCancelled
                        ? "Independent review was cancelled"
                        : "Independent review could not be performed";

                var rec = isTimeout
                    ? "Investigate the reviewer agent timeout and re-run the review before completing this task."
                    : isCancelled
                        ? "Investigate the reviewer cancellation and re-run the review before completing this task."
                        : "Investigate the reviewer agent failure and re-run the review before completing this task.";

                var blockingFinding = new ReviewFinding(
                    ReviewFindingSeverity.Critical,
                    ReviewCategory.Correctness,
                    null,
                    null,
                    $"{prefix}: {result.Error}",
                    rec);

                return new ReviewResult(context.WorkItemId, true, new[] { blockingFinding });
            }

            var findings = ParseFindings(result.Output);
            var blocks = findings.Any(f => f.Severity is ReviewFindingSeverity.Critical or ReviewFindingSeverity.High);
            return new ReviewResult(context.WorkItemId, blocks, findings);
        }

        private static string BuildPrompt(ReviewContext context) =>
            $"""
            You are an independent code reviewer. You did not write this implementation.
            Evaluate correctness, requirements compliance, architecture, security, error
            handling, test coverage, maintainability, and unnecessary complexity.

            Requirements:
            {context.RequirementsText}

            Acceptance Criteria:
            {context.AcceptanceCriteria}

            Architecture:
            {context.ArchitectureText}

            Changed files:
            {string.Join("\n", context.ChangedFiles)}

            Build succeeded: {context.BuildSucceeded}
            Build output:
            {context.BuildOutput}

            Tests succeeded: {context.TestsSucceeded}
            Test output:
            {context.TestOutput}

            Task history:
            {context.TaskHistoryText}

            Respond with ONLY a JSON array of findings, each with fields:
            severity (Critical|High|Medium|Low|Info), category
            (Correctness|RequirementsCompliance|Architecture|Security|ErrorHandling|TestCoverage|Maintainability|UnnecessaryComplexity),
            file (nullable), line (nullable integer), description, recommendation.
            Return an empty array [] if there are no findings.
            """;

        private static IReadOnlyList<ReviewFinding> ParseFindings(string? output)
        {
            if (string.IsNullOrWhiteSpace(output))
            {
                return Array.Empty<ReviewFinding>();
            }

            try
            {
                var dtos = JsonSerializer.Deserialize<List<FindingDto>>(output, JsonOptions);
                if (dtos is null)
                {
                    return Array.Empty<ReviewFinding>();
                }

                var findings = new List<ReviewFinding>();
                foreach (var dto in dtos)
                {
                    if (dto.Severity is null || dto.Category is null)
                    {
                        continue;
                    }

                    if (!Enum.TryParse<ReviewFindingSeverity>(dto.Severity, true, out var severity))
                    {
                        continue;
                    }

                    if (!Enum.TryParse<ReviewCategory>(dto.Category, true, out var category))
                    {
                        continue;
                    }

                    findings.Add(new ReviewFinding(
                        severity, category, dto.File, dto.Line, dto.Description ?? string.Empty, dto.Recommendation ?? string.Empty));
                }

                return findings;
            }
            catch (JsonException)
            {
                return new[]
                {
                    new ReviewFinding(
                        ReviewFindingSeverity.Critical,
                        ReviewCategory.Correctness,
                        null,
                        null,
                        "Independent review output was malformed and could not be parsed: No structured JSON findings array found in reviewer output",
                        "Ensure the reviewer model adheres to the required JSON findings schema.")
                };
            }
        }

        private sealed class FindingDto
        {
            public string? Severity { get; set; }
            public string? Category { get; set; }
            public string? File { get; set; }
            public int? Line { get; set; }
            public string? Description { get; set; }
            public string? Recommendation { get; set; }
        }
    }

    #endregion

    #region Fakes & Harness

    private sealed class DelegatingAgentExecutor : IAgentExecutor
    {
        private readonly Func<AgentExecutionRequest, AgentExecutionResult> _handler;

        public DelegatingAgentExecutor(Func<AgentExecutionRequest, AgentExecutionResult> handler)
        {
            _handler = handler;
        }

        public Task<AgentExecutionResult> ExecuteAsync(AgentExecutionRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(_handler(request));
    }

    private sealed class TestExecutionAdapter : IExecutionAdapter
    {
        private readonly Func<AgentExecutionSpec, ExecutionOutcome> _handler;

        public TestExecutionAdapter(string key, Func<AgentExecutionSpec, ExecutionOutcome> handler)
        {
            Key = key;
            _handler = handler;
        }

        public string Key { get; }
        public ExecutionCapabilities Capabilities => ExecutionCapabilities.FactoryRoutedModel;

        public Task<ExecutionOutcome> ExecuteAsync(AgentExecutionSpec spec, CancellationToken cancellationToken = default) =>
            Task.FromResult(_handler(spec));
    }

    private static ModelRouter CreateModelRouter()
    {
        var options = new ModelRoutingOptions
        {
            Models = new List<ModelCapability>
            {
                new()
                {
                    ModelId = "golden-review-model",
                    Provider = "golden-provider",
                    ReasoningStrength = 5,
                    MaxContextTokens = 128_000,
                    CostTier = 3,
                    IsCodingOptimized = false,
                    IsAvailable = true
                }
            },
            CategoryProfiles = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["code-review"] = new List<string> { "golden-review-model" }
            }
        };

        return new ModelRouter(options);
    }

    private static ReviewContext CreateContext() => new(
        WorkItemId: Guid.Parse("11111111-1111-1111-1111-111111111111"),
        ProjectId: Guid.Parse("22222222-2222-2222-2222-222222222222"),
        RequirementsText: "Implement secure user authentication.",
        AcceptanceCriteria: "Passwords must be hashed with BCrypt. Unit tests must cover all branches.",
        ArchitectureText: "Clean Architecture; Api -> Application -> Infrastructure.",
        ChangedFiles: new[] { "src/Auth/UserService.cs", "tests/Auth/UserServiceTests.cs" },
        TestOutput: "Passed: 14, Failed: 0",
        TestsSucceeded: true,
        BuildOutput: "Build succeeded. 0 Warning(s). 0 Error(s).",
        BuildSucceeded: true,
        TaskHistoryText: "Initial scaffolding created.");

    private static (LegacyAgentCodeReviewer legacy, AgentCodeReviewer migrated) CreateReviewerPair(
        Func<string?, (AgentExecutionResult legacyResult, ExecutionOutcome migratedOutcome)> scenarioFactory,
        string? payload)
    {
        var (legacyResult, migratedOutcome) = scenarioFactory(payload);

        var modelRouter = CreateModelRouter();

        // 1. Legacy reviewer
        var legacyExecutor = new DelegatingAgentExecutor(_ => legacyResult);
        var legacy = new LegacyAgentCodeReviewer(legacyExecutor, modelRouter);

        // 2. Migrated reviewer with full AdapterDispatchingAgentRuntime pipeline
        var options = new ExecutionOptions
        {
            DefaultProfile = "Hermes",
            Profiles =
            {
                ["Hermes"] = new ExecutionProfileOptions { Adapter = "Hermes" }
            },
            AgentTypeProfiles =
            {
                ["Review"] = "Hermes"
            }
        };
        options.Validate();

        var adapter = new TestExecutionAdapter("Hermes", _ => migratedOutcome);
        var registry = new ExecutionAdapterRegistry(new[] { adapter });
        var resolver = new ExecutionProfileResolver(options, registry);
        var runtime = new AdapterDispatchingAgentRuntime(
            resolver,
            new AI.Factory.Application.Abstractions.FailureClassifier(),
            NullLogger<AdapterDispatchingAgentRuntime>.Instance);

        var migrated = new AgentCodeReviewer(
            runtime,
            modelRouter,
            NullLogger<AgentCodeReviewer>.Instance);

        return (legacy, migrated);
    }

    private static void AssertEquivalence(ReviewResult legacyResult, ReviewResult migratedResult)
    {
        Assert.Equal(legacyResult.WorkItemId, migratedResult.WorkItemId);
        Assert.Equal(legacyResult.BlocksCompletion, migratedResult.BlocksCompletion);
        Assert.Equal(legacyResult.Findings.Count, migratedResult.Findings.Count);

        for (var i = 0; i < legacyResult.Findings.Count; i++)
        {
            var l = legacyResult.Findings[i];
            var m = migratedResult.Findings[i];

            Assert.Equal(l.Severity, m.Severity);
            Assert.Equal(l.Category, m.Category);
            Assert.Equal(l.File, m.File);
            Assert.Equal(l.Line, m.Line);
            Assert.Equal(l.Description, m.Description);
            Assert.Equal(l.Recommendation, m.Recommendation);
        }
    }

    #endregion

    #region Equivalence Test Scenarios

    [Fact]
    public async Task GoldenEquivalence_EmptyFindingsArray_EquivalentNonBlocking()
    {
        var payload = "[]";
        var (legacy, migrated) = CreateReviewerPair(p => (
            new AgentExecutionResult(true, p, null, 10),
            new ExecutionOutcome(
                ExecutionOutcomeStatus.Succeeded,
                p,
                null,
                null,
                10,
                null,
                Array.Empty<string>(),
                Array.Empty<string>(),
                new ModelRoute("golden-review-model", null),
                "exit:0")), payload);

        var context = CreateContext();
        var legacyResult = await legacy.ReviewAsync(context);
        var migratedResult = await migrated.ReviewAsync(context);

        AssertEquivalence(legacyResult, migratedResult);
        Assert.False(migratedResult.BlocksCompletion);
        Assert.Empty(migratedResult.Findings);
    }

    [Fact]
    public async Task GoldenEquivalence_HighAndCriticalFindings_EquivalentBlocking()
    {
        var payload = """
            [
              {"severity":"Critical","category":"Security","file":"src/Auth/UserService.cs","line":45,"description":"Hardcoded master password found.","recommendation":"Remove and use environment variable."},
              {"severity":"High","category":"Correctness","file":"src/Auth/UserService.cs","line":92,"description":"NullReferenceException when token is expired.","recommendation":"Add null check on token payload."}
            ]
            """;

        var (legacy, migrated) = CreateReviewerPair(p => (
            new AgentExecutionResult(true, p, null, 25),
            new ExecutionOutcome(
                ExecutionOutcomeStatus.Succeeded,
                p,
                null,
                null,
                25,
                null,
                Array.Empty<string>(),
                Array.Empty<string>(),
                new ModelRoute("golden-review-model", null),
                "exit:0")), payload);

        var context = CreateContext();
        var legacyResult = await legacy.ReviewAsync(context);
        var migratedResult = await migrated.ReviewAsync(context);

        AssertEquivalence(legacyResult, migratedResult);
        Assert.True(migratedResult.BlocksCompletion);
        Assert.Equal(2, migratedResult.Findings.Count);
        Assert.Equal(ReviewFindingSeverity.Critical, migratedResult.Findings[0].Severity);
        Assert.Equal(ReviewFindingSeverity.High, migratedResult.Findings[1].Severity);
    }

    [Fact]
    public async Task GoldenEquivalence_LowAndInfoFindingsOnly_EquivalentNonBlocking()
    {
        var payload = """
            [
              {"severity":"Low","category":"Maintainability","file":"tests/Auth/UserServiceTests.cs","line":12,"description":"Consider using descriptive variable names.","recommendation":"Rename x to userResponse."},
              {"severity":"Info","category":"TestCoverage","file":null,"line":null,"description":"Test coverage is at 94%.","recommendation":"Consider adding boundary tests."}
            ]
            """;

        var (legacy, migrated) = CreateReviewerPair(p => (
            new AgentExecutionResult(true, p, null, 15),
            new ExecutionOutcome(
                ExecutionOutcomeStatus.Succeeded,
                p,
                null,
                null,
                15,
                null,
                Array.Empty<string>(),
                Array.Empty<string>(),
                new ModelRoute("golden-review-model", null),
                "exit:0")), payload);

        var context = CreateContext();
        var legacyResult = await legacy.ReviewAsync(context);
        var migratedResult = await migrated.ReviewAsync(context);

        AssertEquivalence(legacyResult, migratedResult);
        Assert.False(migratedResult.BlocksCompletion);
        Assert.Equal(2, migratedResult.Findings.Count);
    }

    [Fact]
    public async Task GoldenEquivalence_MalformedJsonOutput_EquivalentBlockingCriticalFinding()
    {
        var payload = "I evaluated the code and think it looks great! Here are my suggestions: none.";

        var (legacy, migrated) = CreateReviewerPair(p => (
            new AgentExecutionResult(true, p, null, 12),
            new ExecutionOutcome(
                ExecutionOutcomeStatus.Succeeded,
                p,
                null,
                null,
                12,
                null,
                Array.Empty<string>(),
                Array.Empty<string>(),
                new ModelRoute("golden-review-model", null),
                "exit:0")), payload);

        var context = CreateContext();
        var legacyResult = await legacy.ReviewAsync(context);
        var migratedResult = await migrated.ReviewAsync(context);

        AssertEquivalence(legacyResult, migratedResult);
        Assert.True(migratedResult.BlocksCompletion);
        Assert.Single(migratedResult.Findings);
        Assert.Equal(ReviewFindingSeverity.Critical, migratedResult.Findings[0].Severity);
        Assert.Equal(ReviewCategory.Correctness, migratedResult.Findings[0].Category);
        Assert.Contains("malformed", migratedResult.Findings[0].Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GoldenEquivalence_AgentFailure_EquivalentBlockingCriticalFinding()
    {
        var errorMessage = "Agent container exited with code 1: OutOfMemoryException";

        var (legacy, migrated) = CreateReviewerPair(_ => (
            new AgentExecutionResult(false, null, errorMessage, 50),
            new ExecutionOutcome(
                ExecutionOutcomeStatus.Failed,
                null,
                errorMessage,
                FailureCategory.Infrastructure,
                50,
                null,
                Array.Empty<string>(),
                Array.Empty<string>(),
                new ModelRoute("golden-review-model", null),
                "exit:1")), null);

        var context = CreateContext();
        var legacyResult = await legacy.ReviewAsync(context);
        var migratedResult = await migrated.ReviewAsync(context);

        AssertEquivalence(legacyResult, migratedResult);
        Assert.True(migratedResult.BlocksCompletion);
        Assert.Single(migratedResult.Findings);
        Assert.Equal(ReviewFindingSeverity.Critical, migratedResult.Findings[0].Severity);
        Assert.Equal(ReviewCategory.Correctness, migratedResult.Findings[0].Category);
        Assert.Contains(errorMessage, migratedResult.Findings[0].Description);
    }

    [Fact]
    public async Task GoldenEquivalence_ExecutionTimeout_EquivalentBlockingCriticalFinding()
    {
        var timeoutMessage = "Execution exceeded authoritative deadline of 300s.";

        var (legacy, migrated) = CreateReviewerPair(_ => (
            new AgentExecutionResult(false, null, timeoutMessage, 300_000),
            new ExecutionOutcome(
                ExecutionOutcomeStatus.TimedOut,
                null,
                timeoutMessage,
                FailureCategory.Timeout,
                300_000,
                null,
                Array.Empty<string>(),
                Array.Empty<string>(),
                new ModelRoute("golden-review-model", null),
                "timeout")), null);

        var context = CreateContext();
        var legacyResult = await legacy.ReviewAsync(context);
        var migratedResult = await migrated.ReviewAsync(context);

        AssertEquivalence(legacyResult, migratedResult);
        Assert.True(migratedResult.BlocksCompletion);
        Assert.Single(migratedResult.Findings);
        Assert.Equal(ReviewFindingSeverity.Critical, migratedResult.Findings[0].Severity);
        Assert.Contains(timeoutMessage, migratedResult.Findings[0].Description);
    }

    [Fact]
    public async Task GoldenEquivalence_ExecutionCancellation_EquivalentBlockingCriticalFinding()
    {
        var cancellationMessage = "Review execution was cancelled by user.";

        var (legacy, migrated) = CreateReviewerPair(_ => (
            new AgentExecutionResult(false, null, cancellationMessage, 1500),
            new ExecutionOutcome(
                ExecutionOutcomeStatus.Cancelled,
                null,
                cancellationMessage,
                FailureCategory.Cancelled,
                1500,
                null,
                Array.Empty<string>(),
                Array.Empty<string>(),
                new ModelRoute("golden-review-model", null),
                "cancelled")), null);

        var context = CreateContext();
        var legacyResult = await legacy.ReviewAsync(context);
        var migratedResult = await migrated.ReviewAsync(context);

        AssertEquivalence(legacyResult, migratedResult);
        Assert.True(migratedResult.BlocksCompletion);
        Assert.Single(migratedResult.Findings);
        Assert.Equal(ReviewFindingSeverity.Critical, migratedResult.Findings[0].Severity);
        Assert.Contains(cancellationMessage, migratedResult.Findings[0].Description);
    }

    #endregion
}
