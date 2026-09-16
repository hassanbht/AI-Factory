using System.Text.Json;
using AI.Factory.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace AI.Factory.Application.Orchestration;

public sealed class AgentCodeReviewer : ICodeReviewer
{
    private const int EstimatedContextTokens = 16_000;
    private const string ReviewCategoryKey = "code-review";
    private const string ReviewAgentName = "reviewer";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IAgentExecutor _agentExecutor;
    private readonly IModelRouter _modelRouter;
    private readonly ILogger<AgentCodeReviewer> _logger;

    public AgentCodeReviewer(
        IAgentExecutor agentExecutor,
        IModelRouter modelRouter,
        ILogger<AgentCodeReviewer> logger)
    {
        _agentExecutor = agentExecutor;
        _modelRouter = modelRouter;
        _logger = logger;
    }

    public async Task<ReviewResult> ReviewAsync(
        ReviewContext context,
        CancellationToken cancellationToken = default)
    {
        var modelSelection = _modelRouter.SelectModel(new ModelRoutingRequest(
            ReviewCategoryKey, TaskComplexity.Complex, EstimatedContextTokens, CostPreference.BestQuality));

        _logger.LogInformation(
            "Independent review starting for WorkItem {WorkItemId} using model {ModelId}",
            context.WorkItemId, modelSelection.ModelId);

        var prompt = BuildPrompt(context);

        // WorkingDirectory must be a real filesystem path: since Phase 18.14 it
        // IS the directory bind-mounted into the agent container, and a
        // non-existent one fails the execution outright. This used to pass
        // ProjectId.ToString(), so every real review failed with "Resolved
        // workspace '<guid>' no longer exists" — which surfaces as a Critical
        // finding, blocks completion, and burns the WorkItem's entire repair
        // budget (observed end-to-end in the Phase 18.20 live run). Empty is the
        // executor's documented "no resolved workspace" path: it provisions a
        // fresh isolated scratch directory instead. That is also the correct
        // least-privilege choice here — the review prompt already carries the
        // requirements, changed files and build/test output, so the reviewer has
        // no reason to be handed the Project's real workspace.
        var result = await _agentExecutor.ExecuteAsync(
            new AgentExecutionRequest(ReviewAgentName, modelSelection.ModelId, prompt, string.Empty),
            cancellationToken);

        if (!result.Success)
        {
            _logger.LogWarning(
                "Independent review could not be performed for WorkItem {WorkItemId}: {Error}",
                context.WorkItemId, result.Error);

            var blockingFinding = new ReviewFinding(
                ReviewFindingSeverity.Critical,
                ReviewCategory.Correctness,
                null,
                null,
                $"Independent review could not be performed: {result.Error}",
                "Investigate the reviewer agent failure and re-run the review before completing this task.");

            return new ReviewResult(context.WorkItemId, true, new[] { blockingFinding });
        }

        var findings = ParseFindings(result.Output);
        var blocks = findings.Any(f => f.Severity is ReviewFindingSeverity.Critical or ReviewFindingSeverity.High);

        _logger.LogInformation(
            "Independent review completed for WorkItem {WorkItemId}: {FindingCount} finding(s), blocks completion = {Blocks}",
            context.WorkItemId, findings.Count, blocks);

        return new ReviewResult(context.WorkItemId, blocks, findings);
    }

    private static string BuildPrompt(ReviewContext context)
    {
        return
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
    }

    private IReadOnlyList<ReviewFinding> ParseFindings(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return Array.Empty<ReviewFinding>();
        }

        try
        {
            var dtos = JsonSerializer.Deserialize<List<ReviewFindingDto>>(output, JsonOptions);

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
            _logger.LogWarning("Reviewer output could not be parsed as structured findings — degrading to a single Info finding.");

            return new[]
            {
                new ReviewFinding(
                    ReviewFindingSeverity.Info,
                    ReviewCategory.Maintainability,
                    null,
                    null,
                    $"Reviewer output could not be parsed as structured findings: {output}",
                    "Review the raw agent output manually.")
            };
        }
    }

    private sealed class ReviewFindingDto
    {
        public string? Severity { get; set; }

        public string? Category { get; set; }

        public string? File { get; set; }

        public int? Line { get; set; }

        public string? Description { get; set; }

        public string? Recommendation { get; set; }
    }
}
