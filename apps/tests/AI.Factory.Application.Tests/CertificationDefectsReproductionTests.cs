using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Abstractions.Execution;
using AI.Factory.Application.Orchestration;
using AI.Factory.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AI.Factory.Application.Tests;

/// <summary>
/// Phase P.1 — Deterministic reproduction of the two certification defects:
/// DEFECT A: Reviewer has no usable source content, and startup logs cause real findings to be discarded as non-blocking Info.
/// DEFECT B: Unexpected workspace files (such as U+F00D or stray files) survive while the WorkItem completes.
/// </summary>
public sealed class CertificationDefectsReproductionTests
{
    private sealed class CaptureDispatcher : IAgentExecutionDispatcher
    {
        private readonly string _output;

        public CaptureDispatcher(string output) => _output = output;

        public AgentExecutionSpec? CapturedSpec { get; private set; }

        public Task<ExecutionOutcome> DispatchAsync(
            AgentExecutionSpec spec,
            CancellationToken cancellationToken = default)
        {
            CapturedSpec = spec;
            return Task.FromResult(new ExecutionOutcome(
                ExecutionOutcomeStatus.Succeeded,
                _output,
                null,
                null,
                10,
                null,
                Array.Empty<string>(),
                Array.Empty<string>(),
                spec.Model,
                "exit:0"));
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
                    ModelId = "reviewer-model",
                    Provider = "openrouter",
                    ReasoningStrength = 5,
                    MaxContextTokens = 200_000,
                    CostTier = 1,
                    IsCodingOptimized = false,
                    IsAvailable = true
                }
            },
            CategoryProfiles = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["code-review"] = new List<string> { "reviewer-model" }
            }
        };

        return new ModelRouter(options);
    }

    [Fact]
    public async Task DefectA_Resolved_StartupLogsDoNotDiscardFindings_AndReviewBundleProvidesSourceDiff()
    {
        // Hermes output with s6 container startup logs before real JSON findings:
        var outputWithStartupLog =
            """
            [s6-init] making user provided files executable...
            [s6-init] ensuring user provided files are executable...
            s6-rc: info: service s6rc-oneshot-runner: starting
            [
              {
                "severity": "Critical",
                "category": "Correctness",
                "file": "src/Calculator.cs",
                "line": 10,
                "description": "Implementation has critical flaw",
                "recommendation": "Fix logic"
              }
            ]
            """;

        var dispatcher = new CaptureDispatcher(outputWithStartupLog);
        var reviewer = new AgentCodeReviewer(dispatcher, MakeModelRouter(), NullLogger<AgentCodeReviewer>.Instance);

        var bundle = new ReviewBundle(
            ChangedFiles: new[] { "src/Calculator.cs", "src/NewHelper.cs" },
            Changes: new[]
            {
                new ReviewFileChange("src/Calculator.cs", ReviewChangeKind.Modified, Content: null),
                new ReviewFileChange("src/NewHelper.cs", ReviewChangeKind.Added, Content: "public static int Multiply(int a, int b) => a * b;")
            },
            UnifiedDiff: "--- a/src/Calculator.cs\n+++ b/src/Calculator.cs\n@@ -1 +1 @@\n-public class Calc {}\n+public class Calc { public int Multiply(int a, int b) => a * b; }");

        var context = new ReviewContext(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Requirements text",
            "Acceptance criteria",
            "Architecture text",
            new[] { "src/Calculator.cs", "src/NewHelper.cs" },
            "Tests succeeded",
            true,
            "Build succeeded",
            true,
            "History",
            Bundle: bundle);

        var result = await reviewer.ReviewAsync(context);

        // VERIFICATION OF DEFECT A RESOLUTION:
        // 1. The prompt contains actual unified diff AND newly created file contents:
        Assert.NotNull(dispatcher.CapturedSpec);
        Assert.Contains("Unified Diff / Source Changes:", dispatcher.CapturedSpec!.Instructions!);
        Assert.Contains("Multiply", dispatcher.CapturedSpec.Instructions!);
        Assert.Contains("=== File: src/NewHelper.cs (Added) ===", dispatcher.CapturedSpec.Instructions!);

        // 2. The reviewer's Critical finding was NOT discarded: startup logs were stripped and findings parsed cleanly!
        Assert.True(result.BlocksCompletion);
        Assert.Equal(ReviewExecutionStatus.SucceededWithFindings, result.Status);
        var singleFinding = Assert.Single(result.Findings);
        Assert.Equal(ReviewFindingSeverity.Critical, singleFinding.Severity);
        Assert.Equal("Implementation has critical flaw", singleFinding.Description);
    }

    [Fact]
    public void DefectB_Resolved_UnexpectedWorkspaceFiles_AreFlaggedWithLosslessUnicode()
    {
        var strayUnicodeFile = new WorkspaceFileChange("\uF00D", WorkspaceChangeKind.Added);
        var strayOrdinaryFile = new WorkspaceFileChange("unexpected_tool.py", WorkspaceChangeKind.Added);
        var allowedFile = new WorkspaceFileChange("src/Calculator.cs", WorkspaceChangeKind.Added);

        // VERIFICATION OF DEFECT B RESOLUTION:
        // Unexpected additions are flagged under positive allowlist, with lossless unicode diagnostic:
        var violationsUnicode = WorkspaceIntegrityPolicy.Evaluate(new[] { strayUnicodeFile });
        var violationsOrdinary = WorkspaceIntegrityPolicy.Evaluate(new[] { strayOrdinaryFile });
        var violationsAllowed = WorkspaceIntegrityPolicy.Evaluate(new[] { allowedFile });

        var unicodeViolation = Assert.Single(violationsUnicode);
        Assert.Contains("U+F00D", unicodeViolation);

        var ordinaryViolation = Assert.Single(violationsOrdinary);
        Assert.Contains("unexpected_tool.py", ordinaryViolation);

        Assert.Empty(violationsAllowed);
    }
}
