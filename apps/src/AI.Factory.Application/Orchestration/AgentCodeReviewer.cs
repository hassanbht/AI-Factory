using System.Text.Json;
using System.Text.RegularExpressions;
using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Abstractions.Execution;
using Microsoft.Extensions.Logging;

namespace AI.Factory.Application.Orchestration;

public sealed class AgentCodeReviewer : ICodeReviewer
{
    private const int EstimatedContextTokens = 16_000;
    private const string ReviewCategoryKey = "code-review";
    private const string ReviewAgentName = "reviewer";
    private const int MaxDiffPromptBytes = 64 * 1024;
    private const int MaxAddedFilePromptBytes = 32 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly Regex FramedPattern = new(
        @"<!--\s*REVIEW_START\s*-->([\s\S]*?)<!--\s*REVIEW_END\s*-->",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex FencedPattern = new(
        @"```(?:json)?\s*(\[\s*[\s\S]*?\s*\])\s*```",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly IAgentExecutionDispatcher _dispatcher;
    private readonly IModelRouter _modelRouter;
    private readonly ILogger<AgentCodeReviewer> _logger;

    public AgentCodeReviewer(
        IAgentExecutionDispatcher dispatcher,
        IModelRouter modelRouter,
        ILogger<AgentCodeReviewer> logger)
    {
        _dispatcher = dispatcher;
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
            "Independent review starting for WorkItem {WorkItemId} using model {ModelId}. ChangesCount={ChangesCount}, DiffBytes={DiffBytes}, AddedFilesWithContent={AddedFiles}",
            context.WorkItemId,
            modelSelection.ModelId,
            context.Bundle?.Changes.Count ?? 0,
            context.Bundle?.UnifiedDiff?.Length ?? 0,
            context.Bundle is { Changes: { } changesList } ? changesList.Count(c => c.Kind == ReviewChangeKind.Added && !string.IsNullOrWhiteSpace(c.Content)) : 0);

        var prompt = BuildPrompt(context);

        // Scratch workspace: the review prompt carries the requirements,
        // unified diff, source content for added files, and build/test output,
        // so the reviewer has no need to touch the Project's live workspace.
        var spec = new AgentExecutionSpec(
            AgentRunId: Guid.NewGuid(),
            CorrelationId: Guid.NewGuid(),
            ProjectId: context.ProjectId,
            WorkItemId: context.WorkItemId,
            Attempt: 1,
            Iteration: 1,
            Agent: new ExecutionAgent(Guid.Empty, ReviewAgentName, "Review", string.Empty),
            Instructions: prompt,
            Workspace: new ExecutionWorkspace(string.Empty),
            Model: new ModelRoute(modelSelection.ModelId, null));

        var outcome = await _dispatcher.DispatchAsync(spec, cancellationToken);

        if (outcome.Status == ExecutionOutcomeStatus.TimedOut)
        {
            _logger.LogWarning(
                "Independent review timed out for WorkItem {WorkItemId}: {Error}",
                context.WorkItemId, outcome.ErrorMessage);

            var blockingFinding = new ReviewFinding(
                ReviewFindingSeverity.Critical,
                ReviewCategory.Correctness,
                null,
                null,
                $"Independent review timed out: {outcome.ErrorMessage}",
                "Investigate the reviewer agent timeout and re-run the review before completing this task.");

            return new ReviewResult(context.WorkItemId, true, new[] { blockingFinding }, ReviewExecutionStatus.TimedOut, outcome.ErrorMessage);
        }

        if (outcome.Status == ExecutionOutcomeStatus.Cancelled)
        {
            _logger.LogWarning(
                "Independent review was cancelled for WorkItem {WorkItemId}: {Error}",
                context.WorkItemId, outcome.ErrorMessage);

            var blockingFinding = new ReviewFinding(
                ReviewFindingSeverity.Critical,
                ReviewCategory.Correctness,
                null,
                null,
                $"Independent review was cancelled: {outcome.ErrorMessage}",
                "Investigate the reviewer cancellation and re-run the review before completing this task.");

            return new ReviewResult(context.WorkItemId, true, new[] { blockingFinding }, ReviewExecutionStatus.Cancelled, outcome.ErrorMessage);
        }

        if (outcome.Status != ExecutionOutcomeStatus.Succeeded)
        {
            _logger.LogWarning(
                "Independent review could not be performed for WorkItem {WorkItemId}: {Error}",
                context.WorkItemId, outcome.ErrorMessage);

            var blockingFinding = new ReviewFinding(
                ReviewFindingSeverity.Critical,
                ReviewCategory.Correctness,
                null,
                null,
                $"Independent review could not be performed: {outcome.ErrorMessage}",
                "Investigate the reviewer agent failure and re-run the review before completing this task.");

            return new ReviewResult(context.WorkItemId, true, new[] { blockingFinding }, ReviewExecutionStatus.ReviewerFailure, outcome.ErrorMessage);
        }

        var (status, findings, parseError) = ParseReviewPayload(outcome.Output);

        if (status == ReviewExecutionStatus.MissingResult)
        {
            _logger.LogWarning(
                "Independent review produced no structured findings for WorkItem {WorkItemId}",
                context.WorkItemId);

            var blockingFinding = new ReviewFinding(
                ReviewFindingSeverity.Critical,
                ReviewCategory.Correctness,
                null,
                null,
                "Independent review output was missing or contained no review result.",
                "Verify reviewer configuration and prompt, and re-run the review.");

            return new ReviewResult(context.WorkItemId, true, new[] { blockingFinding }, ReviewExecutionStatus.MissingResult, "Missing review result");
        }

        if (status == ReviewExecutionStatus.MalformedPayload)
        {
            _logger.LogWarning(
                "Independent review returned malformed output for WorkItem {WorkItemId}: {Error}",
                context.WorkItemId, parseError);

            var blockingFinding = new ReviewFinding(
                ReviewFindingSeverity.Critical,
                ReviewCategory.Correctness,
                null,
                null,
                $"Independent review output was malformed and could not be parsed: {parseError}",
                "Ensure the reviewer model adheres to the required JSON findings schema.");

            return new ReviewResult(context.WorkItemId, true, new[] { blockingFinding }, ReviewExecutionStatus.MalformedPayload, parseError);
        }

        var blocks = findings.Any(f => f.Severity is ReviewFindingSeverity.Critical or ReviewFindingSeverity.High);

        _logger.LogInformation(
            "Independent review completed for WorkItem {WorkItemId}: {FindingCount} finding(s), blocks completion = {Blocks}",
            context.WorkItemId, findings.Count, blocks);

        return new ReviewResult(context.WorkItemId, blocks, findings, status);
    }

    private static string BuildPrompt(ReviewContext context)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("You are an independent code reviewer. You did not write this implementation.");
        sb.AppendLine("Evaluate correctness, requirements compliance, architecture, security, error");
        sb.AppendLine("handling, test coverage, maintainability, and unnecessary complexity.");
        sb.AppendLine();
        sb.AppendLine("Requirements:");
        sb.AppendLine(context.RequirementsText);
        sb.AppendLine();
        sb.AppendLine("Acceptance Criteria:");
        sb.AppendLine(context.AcceptanceCriteria);
        sb.AppendLine();
        sb.AppendLine("Architecture:");
        sb.AppendLine(context.ArchitectureText);
        sb.AppendLine();

        sb.AppendLine("Changed files:");
        if (context.Bundle?.Changes is { Count: > 0 } changes)
        {
            foreach (var change in changes)
            {
                sb.AppendLine($"- {change.Path} ({change.Kind})");
            }
        }
        else
        {
            sb.AppendLine(string.Join("\n", context.ChangedFiles));
        }
        sb.AppendLine();

        if (context.Bundle is not null)
        {
            if (!string.IsNullOrWhiteSpace(context.Bundle.UnifiedDiff))
            {
                var diff = context.Bundle.UnifiedDiff.Length > MaxDiffPromptBytes
                    ? context.Bundle.UnifiedDiff[..MaxDiffPromptBytes] + "\n...[truncated diff]"
                    : context.Bundle.UnifiedDiff;

                sb.AppendLine("Unified Diff / Source Changes:");
                sb.AppendLine("```diff");
                sb.AppendLine(diff);
                sb.AppendLine("```");
                sb.AppendLine();
            }

            var addedFiles = context.Bundle.Changes
                .Where(c => c.Kind == ReviewChangeKind.Added && !string.IsNullOrWhiteSpace(c.Content))
                .ToList();

            if (addedFiles.Count > 0)
            {
                sb.AppendLine("Newly Created Files Content:");
                foreach (var added in addedFiles)
                {
                    var content = added.Content!.Length > MaxAddedFilePromptBytes
                        ? added.Content[..MaxAddedFilePromptBytes] + "\n...[truncated content]"
                        : added.Content;

                    sb.AppendLine($"=== File: {added.Path} (Added) ===");
                    sb.AppendLine(content);
                    sb.AppendLine();
                }
            }
        }

        sb.AppendLine($"Build succeeded: {context.BuildSucceeded}");
        sb.AppendLine("Build output:");
        sb.AppendLine(context.BuildOutput);
        sb.AppendLine();
        sb.AppendLine($"Tests succeeded: {context.TestsSucceeded}");
        sb.AppendLine("Test output:");
        sb.AppendLine(context.TestOutput);
        sb.AppendLine();
        sb.AppendLine("Task history:");
        sb.AppendLine(context.TaskHistoryText);
        sb.AppendLine();
        sb.AppendLine("Respond with ONLY a JSON array of findings enclosed between <!-- REVIEW_START --> and <!-- REVIEW_END -->.");
        sb.AppendLine("Each finding must have fields:");
        sb.AppendLine("severity (Critical|High|Medium|Low|Info), category (Correctness|RequirementsCompliance|Architecture|Security|ErrorHandling|TestCoverage|Maintainability|UnnecessaryComplexity),");
        sb.AppendLine("file (nullable string), line (nullable integer), description (non-empty string), recommendation (string).");
        sb.AppendLine();
        sb.AppendLine("Example:");
        sb.AppendLine("<!-- REVIEW_START -->");
        sb.AppendLine("[");
        sb.AppendLine("  {\"severity\":\"Critical\",\"category\":\"Correctness\",\"file\":\"src/File.cs\",\"line\":10,\"description\":\"issue description\",\"recommendation\":\"suggested fix\"}");
        sb.AppendLine("]");
        sb.AppendLine("<!-- REVIEW_END -->");
        sb.AppendLine();
        sb.AppendLine("If there are NO findings, return exactly:");
        sb.AppendLine("<!-- REVIEW_START -->");
        sb.AppendLine("[]");
        sb.AppendLine("<!-- REVIEW_END -->");

        return sb.ToString();
    }

    private static (ReviewExecutionStatus Status, IReadOnlyList<ReviewFinding> Findings, string? Error) ParseReviewPayload(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return (ReviewExecutionStatus.MissingResult, Array.Empty<ReviewFinding>(), "Output is empty");
        }

        var jsonCandidate = ExtractJsonCandidate(output);
        if (string.IsNullOrWhiteSpace(jsonCandidate))
        {
            return (ReviewExecutionStatus.MalformedPayload, Array.Empty<ReviewFinding>(), "No structured JSON findings array found in reviewer output");
        }

        List<ReviewFindingDto>? dtos;
        try
        {
            dtos = JsonSerializer.Deserialize<List<ReviewFindingDto>>(jsonCandidate, JsonOptions);
        }
        catch (JsonException ex)
        {
            return (ReviewExecutionStatus.MalformedPayload, Array.Empty<ReviewFinding>(), $"JSON parse error: {ex.Message}");
        }

        if (dtos is null)
        {
            return (ReviewExecutionStatus.MalformedPayload, Array.Empty<ReviewFinding>(), "Deserialized findings list is null");
        }

        if (dtos.Count == 0)
        {
            return (ReviewExecutionStatus.SucceededWithNoFindings, Array.Empty<ReviewFinding>(), null);
        }

        var findings = new List<ReviewFinding>();

        for (var i = 0; i < dtos.Count; i++)
        {
            var dto = dtos[i];
            if (string.IsNullOrWhiteSpace(dto.Severity) || !Enum.TryParse<ReviewFindingSeverity>(dto.Severity, true, out var severity))
            {
                return (ReviewExecutionStatus.MalformedPayload, Array.Empty<ReviewFinding>(),
                    $"Finding #{i + 1} has invalid or missing severity: '{dto.Severity}'");
            }

            if (string.IsNullOrWhiteSpace(dto.Category) || !Enum.TryParse<ReviewCategory>(dto.Category, true, out var category))
            {
                return (ReviewExecutionStatus.MalformedPayload, Array.Empty<ReviewFinding>(),
                    $"Finding #{i + 1} has invalid or missing category: '{dto.Category}'");
            }

            if (string.IsNullOrWhiteSpace(dto.Description))
            {
                return (ReviewExecutionStatus.MalformedPayload, Array.Empty<ReviewFinding>(),
                    $"Finding #{i + 1} has empty description");
            }

            findings.Add(new ReviewFinding(
                severity,
                category,
                dto.File,
                dto.Line,
                dto.Description.Trim(),
                dto.Recommendation?.Trim() ?? string.Empty));
        }

        return (ReviewExecutionStatus.SucceededWithFindings, findings, null);
    }

    private static string? ExtractJsonCandidate(string output)
    {
        // 1. Look for framed payload: <!-- REVIEW_START --> ... <!-- REVIEW_END -->
        var framedMatch = FramedPattern.Match(output);
        if (framedMatch.Success)
        {
            var candidate = framedMatch.Groups[1].Value.Trim();
            if (candidate.StartsWith('[') && candidate.EndsWith(']'))
            {
                return candidate;
            }
        }

        // 2. Look for markdown code fence: ```json ... ``` or ``` ... ```
        var fencedMatch = FencedPattern.Match(output);
        if (fencedMatch.Success)
        {
            return fencedMatch.Groups[1].Value.Trim();
        }

        // 3. Look for balanced JSON array [ ... ] (ignoring operational log tags like [s6-init])
        for (var i = 0; i < output.Length; i++)
        {
            if (output[i] == '[')
            {
                var nextIndex = i + 1;
                while (nextIndex < output.Length && char.IsWhiteSpace(output[nextIndex]))
                {
                    nextIndex++;
                }

                if (nextIndex < output.Length && (output[nextIndex] == '{' || output[nextIndex] == ']'))
                {
                    var lastEnd = output.LastIndexOf(']');
                    if (lastEnd > i)
                    {
                        return output.Substring(i, lastEnd - i + 1).Trim();
                    }
                }
            }
        }

        return null;
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
