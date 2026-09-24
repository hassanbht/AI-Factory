using System.Text.Json;
using AI.Factory.Domain.Enums;
using AI.Factory.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace AI.Factory.Worker.Tests.Fixtures;

/// <summary>
/// Phase D — reads back, from the live run's own isolated database, what the
/// Factory persisted for every AgentRun of a Project: identity, routing,
/// status, failure category, the Phase C adapter metadata and the artifact
/// rows recorded for the run's WorkItem, plus the Factory-owned verification
/// and review records. Observation only: nothing here drives the lifecycle.
///
/// The one assertion is the Phase C invariant itself: every AgentRun that an
/// adapter actually executed to success carries <c>ExecutionAdapter=Hermes</c>
/// and a non-empty <c>ExitReason</c> in its persisted structured result.
/// </summary>
public static class LiveExecutionEvidence
{
    public static async Task ReportAsync(
        LiveWorkerHost live, Guid projectId, ITestOutputHelper output, CancellationToken cancellationToken)
    {
        await using var scope = live.Host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FactoryDbContext>();

        var runs = await db.AgentRuns
            .Where(r => r.ProjectId == projectId)
            .OrderBy(r => r.CreatedAtUtc)
            .ToListAsync(cancellationToken);

        var artifacts = await db.Artifacts
            .Where(a => a.ProjectId == projectId)
            .ToListAsync(cancellationToken);

        output.WriteLine("");
        output.WriteLine("PHASE-D-EVIDENCE AgentRuns");
        output.WriteLine("| AgentRun | WorkItem | Attempt | Provider | Model | Status | Failure | ExecutionAdapter | ExitReason | DurationS |");
        output.WriteLine("|---|---|---|---|---|---|---|---|---|---|");

        foreach (var run in runs)
        {
            var (adapter, exitReason) = ReadAdapterMetadata(run.StructuredResult);
            var duration = run.StartedAtUtc is { } s && run.CompletedAtUtc is { } c
                ? ((c - s).TotalSeconds).ToString("F0")
                : "-";

            output.WriteLine(
                $"| {run.Id} | {run.WorkItemId?.ToString() ?? "-"} | {run.Attempt} | {run.Provider ?? "-"} | {run.Model ?? "-"} | " +
                $"{run.Status} | {run.FailureCategory?.ToString() ?? "-"} | {adapter ?? "-"} | {exitReason ?? "-"} | {duration} |");
        }

        output.WriteLine("");
        output.WriteLine("PHASE-D-EVIDENCE Artifacts");
        output.WriteLine("| WorkItem | ProjectId | Rows | DistinctPaths | SamplePaths |");
        output.WriteLine("|---|---|---|---|---|");

        foreach (var group in artifacts.GroupBy(a => a.WorkItemId))
        {
            var paths = group.Select(a => a.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var sample = string.Join(", ", paths
                .Where(p => !p.Contains("/obj/") && !p.Contains("\\obj\\") && !p.Contains("/.git/") && !p.Contains("\\.git\\"))
                .Take(6));

            output.WriteLine($"| {group.Key?.ToString() ?? "-"} | {projectId} | {group.Count()} | {paths.Count} | {sample} |");
        }

        var workItemIds = runs.Where(r => r.WorkItemId.HasValue).Select(r => r.WorkItemId!.Value).Distinct().ToList();

        var validations = await db.ValidationRuns
            .Where(v => workItemIds.Contains(v.WorkItemId))
            .OrderBy(v => v.CreatedAtUtc)
            .ToListAsync(cancellationToken);

        output.WriteLine("");
        output.WriteLine("PHASE-D-EVIDENCE Verification (ValidationRuns)");

        foreach (var v in validations)
        {
            output.WriteLine(
                $"  {v.CreatedAtUtc:HH:mm:ss} WorkItem={v.WorkItemId} Success={v.Success} Build={v.BuildSucceeded} " +
                $"Tests={v.TestsSucceeded?.ToString() ?? "-"} Failures={Truncate(v.FailuresJson)}");
        }

        var reviews = await db.ReviewRuns
            .Where(r => workItemIds.Contains(r.WorkItemId))
            .OrderBy(r => r.CreatedAtUtc)
            .ToListAsync(cancellationToken);

        output.WriteLine("");
        output.WriteLine("PHASE-D-EVIDENCE Review (ReviewRuns)");

        foreach (var r in reviews)
        {
            output.WriteLine(
                $"  {r.CreatedAtUtc:HH:mm:ss} WorkItem={r.WorkItemId} BlocksCompletion={r.BlocksCompletion} Findings={Truncate(r.FindingsJson)}");
        }

        output.WriteLine("");

        foreach (var run in runs.Where(r => r.Status == AgentRunStatus.Succeeded))
        {
            var (adapter, exitReason) = ReadAdapterMetadata(run.StructuredResult);
            Assert.Equal("Hermes", adapter);
            Assert.False(string.IsNullOrWhiteSpace(exitReason), $"AgentRun {run.Id} has no persisted ExitReason.");
        }
    }

    private static (string? Adapter, string? ExitReason) ReadAdapterMetadata(string? structuredResult)
    {
        if (string.IsNullOrWhiteSpace(structuredResult))
        {
            return (null, null);
        }

        using var document = JsonDocument.Parse(structuredResult);

        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!string.Equals(property.Name, "Metadata", StringComparison.OrdinalIgnoreCase)
                || property.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? adapter = null;
            string? exitReason = null;

            foreach (var entry in property.Value.EnumerateObject())
            {
                if (string.Equals(entry.Name, "ExecutionAdapter", StringComparison.OrdinalIgnoreCase))
                {
                    adapter = entry.Value.ToString();
                }
                else if (string.Equals(entry.Name, "ExitReason", StringComparison.OrdinalIgnoreCase))
                {
                    exitReason = entry.Value.ToString();
                }
            }

            return (adapter, exitReason);
        }

        return (null, null);
    }

    private static string Truncate(string? text) =>
        string.IsNullOrEmpty(text) ? "-" : text.Length <= 300 ? text.ReplaceLineEndings(" ") : text[..300].ReplaceLineEndings(" ") + "…";
}
