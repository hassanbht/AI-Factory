using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Abstractions.Execution;
using AI.Factory.Application.Orchestration;
using AI.Factory.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AI.Factory.Application.Tests.Review;

/// <summary>
/// Phase P.1 — Review Boundary Integrity Tests.
/// Validates Groups A, B, and C:
/// Group A: Review Input (modified/added/deleted file content, unified diff, empty workspace resilience).
/// Group B: Review Parsing (startup logs, framed payload, valid/empty findings, malformed payload, missing result, timeout, cancel, process failure).
/// Group C: Review Lifecycle (reviewer failure cannot become success, blocking finding blocks acceptance, informational finding does not block, malformed review cannot become informational success).
/// </summary>
public sealed class ReviewBoundaryIntegrityTests
{
    private sealed class CaptureDispatcher : IAgentExecutionDispatcher
    {
        private readonly ExecutionOutcome _outcome;

        public CaptureDispatcher(ExecutionOutcome outcome) => _outcome = outcome;

        public AgentExecutionSpec? CapturedSpec { get; private set; }

        public Task<ExecutionOutcome> DispatchAsync(AgentExecutionSpec spec, CancellationToken cancellationToken = default)
        {
            CapturedSpec = spec;
            return Task.FromResult(_outcome);
        }
    }

    private static ModelRouter CreateModelRouter()
    {
        var options = new ModelRoutingOptions
        {
            Models = new List<ModelCapability>
            {
                new()
                {
                    ModelId = "review-model",
                    Provider = "openrouter",
                    ReasoningStrength = 5,
                    MaxContextTokens = 128000,
                    CostTier = 2,
                    IsCodingOptimized = false,
                    IsAvailable = true
                }
            },
            CategoryProfiles = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["code-review"] = new List<string> { "review-model" }
            }
        };

        return new ModelRouter(options);
    }

    private static ExecutionOutcome SuccessOutcome(string? output) => new(
        ExecutionOutcomeStatus.Succeeded,
        output,
        null,
        null,
        50,
        null,
        Array.Empty<string>(),
        Array.Empty<string>(),
        new ModelRoute("review-model", null),
        "exit:0");

    private static ExecutionOutcome FailureOutcome(ExecutionOutcomeStatus status, string error, FailureCategory category, string exitReason) => new(
        status,
        null,
        error,
        category,
        50,
        null,
        Array.Empty<string>(),
        Array.Empty<string>(),
        new ModelRoute("review-model", null),
        exitReason);

    #region Group A: Review Input

    [Fact]
    public async Task ReviewInput_UnifiedDiffAndNewlyCreatedContent_AreInjectedIntoPrompt()
    {
        var bundle = new ReviewBundle(
            ChangedFiles: new[] { "src/Services/PaymentService.cs", "tests/PaymentServiceTests.cs" },
            Changes: new[]
            {
                new ReviewFileChange("src/Services/PaymentService.cs", ReviewChangeKind.Modified, null, "@@ -1 +1 @@"),
                new ReviewFileChange("tests/PaymentServiceTests.cs", ReviewChangeKind.Added, "public class PaymentServiceTests { [Fact] public void Test1() {} }")
            },
            UnifiedDiff: "diff --git a/src/Services/PaymentService.cs b/src/Services/PaymentService.cs\n+public void Charge() {}");

        var context = new ReviewContext(
            WorkItemId: Guid.NewGuid(),
            ProjectId: Guid.NewGuid(),
            RequirementsText: "Process charges",
            AcceptanceCriteria: "Charge succeeded",
            ArchitectureText: "Service Layer",
            ChangedFiles: bundle.ChangedFiles,
            TestOutput: "Pass",
            TestsSucceeded: true,
            BuildOutput: "OK",
            BuildSucceeded: true,
            TaskHistoryText: "History",
            Bundle: bundle);

        var dispatcher = new CaptureDispatcher(SuccessOutcome("[]"));
        var reviewer = new AgentCodeReviewer(dispatcher, CreateModelRouter(), NullLogger<AgentCodeReviewer>.Instance);

        var result = await reviewer.ReviewAsync(context);

        Assert.NotNull(dispatcher.CapturedSpec);
        var prompt = dispatcher.CapturedSpec!.Instructions!;
        Assert.Contains("diff --git a/src/Services/PaymentService.cs", prompt);
        Assert.Contains("public class PaymentServiceTests { [Fact] public void Test1() {} }", prompt);
        Assert.Contains("PaymentService.cs (Modified)", prompt);
        Assert.Contains("PaymentServiceTests.cs (Added)", prompt);
        Assert.False(result.BlocksCompletion);
    }

    [Fact]
    public async Task ReviewInput_DeletedFileIsRepresentedInPrompt()
    {
        var bundle = new ReviewBundle(
            ChangedFiles: new[] { "src/LegacyHelper.cs" },
            Changes: new[]
            {
                new ReviewFileChange("src/LegacyHelper.cs", ReviewChangeKind.Deleted)
            },
            UnifiedDiff: "diff --git a/src/LegacyHelper.cs b/dev/null\n-public class LegacyHelper {}");

        var context = new ReviewContext(
            WorkItemId: Guid.NewGuid(),
            ProjectId: Guid.NewGuid(),
            RequirementsText: "Remove deprecated helper",
            AcceptanceCriteria: "LegacyHelper deleted",
            ArchitectureText: "Clean Architecture",
            ChangedFiles: bundle.ChangedFiles,
            TestOutput: "All tests pass",
            TestsSucceeded: true,
            BuildOutput: "Build succeeded",
            BuildSucceeded: true,
            TaskHistoryText: "Deleted legacy class",
            Bundle: bundle);

        var dispatcher = new CaptureDispatcher(SuccessOutcome("[]"));
        var reviewer = new AgentCodeReviewer(dispatcher, CreateModelRouter(), NullLogger<AgentCodeReviewer>.Instance);

        await reviewer.ReviewAsync(context);

        Assert.NotNull(dispatcher.CapturedSpec);
        var prompt = dispatcher.CapturedSpec!.Instructions!;
        Assert.Contains("LegacyHelper.cs (Deleted)", prompt);
        Assert.Contains("-public class LegacyHelper {}", prompt);
    }

    [Fact]
    public async Task ReviewInput_EmptyWorkspace_DoesNotCauseMissingContextReview_WhenBundleExists()
    {
        var bundle = new ReviewBundle(
            ChangedFiles: new[] { "src/App.cs" },
            Changes: new[]
            {
                new ReviewFileChange("src/App.cs", ReviewChangeKind.Added, "public static void Main() {}")
            },
            UnifiedDiff: "+public static void Main() {}");

        var context = new ReviewContext(
            WorkItemId: Guid.NewGuid(),
            ProjectId: Guid.NewGuid(),
            RequirementsText: "Entry point",
            AcceptanceCriteria: "Runs",
            ArchitectureText: "Program",
            ChangedFiles: bundle.ChangedFiles,
            TestOutput: "Pass",
            TestsSucceeded: true,
            BuildOutput: "Build 0 errors",
            BuildSucceeded: true,
            TaskHistoryText: "Added Main",
            Bundle: bundle);

        var dispatcher = new CaptureDispatcher(SuccessOutcome("[]"));
        var reviewer = new AgentCodeReviewer(dispatcher, CreateModelRouter(), NullLogger<AgentCodeReviewer>.Instance);

        var result = await reviewer.ReviewAsync(context);

        // Scratch workspace path is empty (independent from implementation workspace):
        Assert.True(string.IsNullOrEmpty(dispatcher.CapturedSpec!.Workspace.Path));
        // Yet reviewer receives the code:
        Assert.Contains("public static void Main() {}", dispatcher.CapturedSpec.Instructions!);
        Assert.False(result.BlocksCompletion);
    }

    #endregion

    #region Group B: Review Parsing

    [Fact]
    public async Task ReviewParsing_StartupLogsPrecedingValidJsonArray_ParsesFindingsCleanly()
    {
        var rawOutput =
            """
            [s6-init] making user provided files executable...
            [s6-init] ensuring user provided files are executable...
            s6-rc: info: service s6rc-oneshot-runner: starting
            [
              {"severity":"Medium","category":"Maintainability","file":"src/Calc.cs","line":5,"description":"Refactor method","recommendation":"Shorten"}
            ]
            """;

        var dispatcher = new CaptureDispatcher(SuccessOutcome(rawOutput));
        var reviewer = new AgentCodeReviewer(dispatcher, CreateModelRouter(), NullLogger<AgentCodeReviewer>.Instance);

        var result = await reviewer.ReviewAsync(CreateContext());

        Assert.Equal(ReviewExecutionStatus.SucceededWithFindings, result.Status);
        Assert.False(result.BlocksCompletion); // Medium finding does not block
        Assert.Single(result.Findings);
        Assert.Equal(ReviewFindingSeverity.Medium, result.Findings[0].Severity);
        Assert.Equal("Refactor method", result.Findings[0].Description);
    }

    [Fact]
    public async Task ReviewParsing_StartupLogsPrecedingFramedPayload_ExtractsFramedJson()
    {
        var rawOutput =
            """
            s6-rc: info: service s6rc-oneshot-runner: starting
            Some container diagnostic text.
            <!-- REVIEW_START -->
            [
              {"severity":"High","category":"Security","file":"src/Auth.cs","line":20,"description":"Insecure hashing","recommendation":"Use Argon2"}
            ]
            <!-- REVIEW_END -->
            Operational shutdown log.
            """;

        var dispatcher = new CaptureDispatcher(SuccessOutcome(rawOutput));
        var reviewer = new AgentCodeReviewer(dispatcher, CreateModelRouter(), NullLogger<AgentCodeReviewer>.Instance);

        var result = await reviewer.ReviewAsync(CreateContext());

        Assert.Equal(ReviewExecutionStatus.SucceededWithFindings, result.Status);
        Assert.True(result.BlocksCompletion); // High finding blocks completion
        Assert.Single(result.Findings);
        Assert.Equal(ReviewFindingSeverity.High, result.Findings[0].Severity);
        Assert.Equal(ReviewCategory.Security, result.Findings[0].Category);
    }

    [Fact]
    public async Task ReviewParsing_ValidEmptyFindingsArray_ReturnsSucceededWithNoFindings()
    {
        var dispatcher = new CaptureDispatcher(SuccessOutcome("[]"));
        var reviewer = new AgentCodeReviewer(dispatcher, CreateModelRouter(), NullLogger<AgentCodeReviewer>.Instance);

        var result = await reviewer.ReviewAsync(CreateContext());

        Assert.Equal(ReviewExecutionStatus.SucceededWithNoFindings, result.Status);
        Assert.False(result.BlocksCompletion);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task ReviewParsing_MalformedPayload_NeverBecomesInformationalSuccess_BlocksCompletion()
    {
        var dispatcher = new CaptureDispatcher(SuccessOutcome("Everything looks fantastic! No changes required."));
        var reviewer = new AgentCodeReviewer(dispatcher, CreateModelRouter(), NullLogger<AgentCodeReviewer>.Instance);

        var result = await reviewer.ReviewAsync(CreateContext());

        Assert.Equal(ReviewExecutionStatus.MalformedPayload, result.Status);
        Assert.True(result.BlocksCompletion);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(ReviewFindingSeverity.Critical, finding.Severity);
        Assert.Contains("malformed", finding.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReviewParsing_MissingResult_EmptyOutput_BlocksCompletion()
    {
        var dispatcher = new CaptureDispatcher(SuccessOutcome("   "));
        var reviewer = new AgentCodeReviewer(dispatcher, CreateModelRouter(), NullLogger<AgentCodeReviewer>.Instance);

        var result = await reviewer.ReviewAsync(CreateContext());

        Assert.Equal(ReviewExecutionStatus.MissingResult, result.Status);
        Assert.True(result.BlocksCompletion);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(ReviewFindingSeverity.Critical, finding.Severity);
        Assert.Contains("missing", finding.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReviewParsing_ReviewerTimeout_ProducesCriticalFindingAndBlocksCompletion()
    {
        var dispatcher = new CaptureDispatcher(FailureOutcome(
            ExecutionOutcomeStatus.TimedOut, "Execution exceeded timeout of 300s", FailureCategory.Timeout, "timeout"));
        var reviewer = new AgentCodeReviewer(dispatcher, CreateModelRouter(), NullLogger<AgentCodeReviewer>.Instance);

        var result = await reviewer.ReviewAsync(CreateContext());

        Assert.Equal(ReviewExecutionStatus.TimedOut, result.Status);
        Assert.True(result.BlocksCompletion);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(ReviewFindingSeverity.Critical, finding.Severity);
        Assert.Contains("timed out", finding.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReviewParsing_ReviewerCancellation_ProducesCriticalFindingAndBlocksCompletion()
    {
        var dispatcher = new CaptureDispatcher(FailureOutcome(
            ExecutionOutcomeStatus.Cancelled, "Operation was cancelled", FailureCategory.Cancelled, "cancelled"));
        var reviewer = new AgentCodeReviewer(dispatcher, CreateModelRouter(), NullLogger<AgentCodeReviewer>.Instance);

        var result = await reviewer.ReviewAsync(CreateContext());

        Assert.Equal(ReviewExecutionStatus.Cancelled, result.Status);
        Assert.True(result.BlocksCompletion);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(ReviewFindingSeverity.Critical, finding.Severity);
        Assert.Contains("cancelled", finding.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReviewParsing_ReviewerProcessFailure_ProducesCriticalFindingAndBlocksCompletion()
    {
        var dispatcher = new CaptureDispatcher(FailureOutcome(
            ExecutionOutcomeStatus.Failed, "Container launch failed: docker exited 127", FailureCategory.Infrastructure, "exit:127"));
        var reviewer = new AgentCodeReviewer(dispatcher, CreateModelRouter(), NullLogger<AgentCodeReviewer>.Instance);

        var result = await reviewer.ReviewAsync(CreateContext());

        Assert.Equal(ReviewExecutionStatus.ReviewerFailure, result.Status);
        Assert.True(result.BlocksCompletion);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(ReviewFindingSeverity.Critical, finding.Severity);
        Assert.Contains("could not be performed", finding.Description, StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region Group C: Review Lifecycle

    [Fact]
    public async Task ReviewLifecycle_ReviewerFailure_CanNeverBecomeSuccessfulReview()
    {
        var dispatcher = new CaptureDispatcher(FailureOutcome(
            ExecutionOutcomeStatus.Failed, "Fatal agent crash", FailureCategory.Agent, "exit:1"));
        var reviewer = new AgentCodeReviewer(dispatcher, CreateModelRouter(), NullLogger<AgentCodeReviewer>.Instance);

        var result = await reviewer.ReviewAsync(CreateContext());

        Assert.NotEqual(ReviewExecutionStatus.SucceededWithNoFindings, result.Status);
        Assert.NotEqual(ReviewExecutionStatus.SucceededWithFindings, result.Status);
        Assert.True(result.BlocksCompletion);
    }

    [Fact]
    public async Task ReviewLifecycle_BlockingFinding_BlocksCompletion()
    {
        var json = """[{"severity":"Critical","category":"Correctness","description":"Deadlock in queue","recommendation":"Use ConcurrentQueue"}]""";
        var dispatcher = new CaptureDispatcher(SuccessOutcome(json));
        var reviewer = new AgentCodeReviewer(dispatcher, CreateModelRouter(), NullLogger<AgentCodeReviewer>.Instance);

        var result = await reviewer.ReviewAsync(CreateContext());

        Assert.True(result.BlocksCompletion);
        Assert.Equal(ReviewFindingSeverity.Critical, result.Findings[0].Severity);
    }

    [Fact]
    public async Task ReviewLifecycle_InformationalFinding_DoesNotBlockCompletion()
    {
        var json = """[{"severity":"Info","category":"Maintainability","description":"Variable name could be shorter","recommendation":"Rename"}]""";
        var dispatcher = new CaptureDispatcher(SuccessOutcome(json));
        var reviewer = new AgentCodeReviewer(dispatcher, CreateModelRouter(), NullLogger<AgentCodeReviewer>.Instance);

        var result = await reviewer.ReviewAsync(CreateContext());

        Assert.False(result.BlocksCompletion);
        Assert.Equal(ReviewFindingSeverity.Info, result.Findings[0].Severity);
    }

    [Fact]
    public async Task ReviewLifecycle_MalformedReview_CannotSilentlyBecomeInformationalSuccess()
    {
        var invalidJson = "[{\"severity\":\"UnknownSeverity\",\"category\":\"Correctness\",\"description\":\"Oops\"}]";
        var dispatcher = new CaptureDispatcher(SuccessOutcome(invalidJson));
        var reviewer = new AgentCodeReviewer(dispatcher, CreateModelRouter(), NullLogger<AgentCodeReviewer>.Instance);

        var result = await reviewer.ReviewAsync(CreateContext());

        Assert.True(result.BlocksCompletion);
        Assert.Equal(ReviewExecutionStatus.MalformedPayload, result.Status);
        Assert.Equal(ReviewFindingSeverity.Critical, result.Findings[0].Severity);
    }

    #endregion

    private static ReviewContext CreateContext() => new(
        WorkItemId: Guid.NewGuid(),
        ProjectId: Guid.NewGuid(),
        RequirementsText: "Requirements",
        AcceptanceCriteria: "Acceptance",
        ArchitectureText: "Architecture",
        ChangedFiles: new[] { "src/File.cs" },
        TestOutput: "Tests passed",
        TestsSucceeded: true,
        BuildOutput: "Build succeeded",
        BuildSucceeded: true,
        TaskHistoryText: "History");
}
