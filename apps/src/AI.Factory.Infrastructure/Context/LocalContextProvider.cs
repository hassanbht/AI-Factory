using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Orchestration;
using AI.Factory.Domain.Entities;
using AI.Factory.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AI.Factory.Infrastructure.Context;

/// <summary>
/// First IContextProvider implementation: local filesystem (per-project
/// candidate files under Project.RootPath) + the Factory's own SQLite
/// database (Requirement/WorkItem/AgentRun). No embeddings, no vector
/// search — relevance is a simple recency-rank heuristic. Designed to be
/// swapped for an OpenViking-backed provider later without any Application
/// code change.
/// </summary>
public sealed class LocalContextProvider : IContextProvider
{
    private const int MaxContentLength = 20_000;

    private static readonly string[] ArchitectureFileNames = { "architecture.md", "ARCHITECTURE.md" };
    private static readonly string[] CodingStandardsFileNames = { "coding-standards.md", "CODING_STANDARDS.md" };
    private static readonly string[] DecisionsFileNames = { "decisions.md", "DECISIONS.md" };

    private static readonly string[] SourceFileExtensions =
    {
        ".cs", ".ts", ".tsx", ".js", ".jsx", ".py", ".go", ".md", ".json", ".yaml", ".yml"
    };

    private static readonly string[] ExcludedDirectoryNames =
    {
        "bin", "obj", "node_modules", ".git", ".vs"
    };

    private readonly FactoryDbContext _db;
    private readonly IWorkspaceResolver _workspaceResolver;
    private readonly ILearningStore? _learningStore;

    /// <summary>
    /// Phase 18.17 — <paramref name="workspaceResolver"/> is the authoritative
    /// workspace resolution path shared with AgentOrchestrator, HermesAgentExecutor,
    /// and VerificationGate. Context queries now honor ProjectsBaseDirectory and
    /// AllowedRoot identically to agent execution — no second Path.GetFullPath
    /// resolution exists anywhere in the solution after this change.
    /// </summary>
    public LocalContextProvider(
        FactoryDbContext db,
        IWorkspaceResolver workspaceResolver,
        ILearningStore? learningStore = null)
    {
        _db = db;
        _workspaceResolver = workspaceResolver;
        _learningStore = learningStore;
    }

    public async Task<ContextResult> QueryAsync(
        ContextQuery query,
        CancellationToken cancellationToken = default)
    {
        return query.Category switch
        {
            ContextCategory.ProjectRequirements => await QueryRequirementsAsync(query, cancellationToken),
            ContextCategory.ArchitectureDocuments => await QueryProjectFileAsync(query, ArchitectureFileNames, cancellationToken),
            ContextCategory.CodingStandards => await QueryProjectFileAsync(query, CodingStandardsFileNames, cancellationToken),
            ContextCategory.ProjectDecisions => await QueryProjectFileAsync(query, DecisionsFileNames, cancellationToken),
            ContextCategory.RelevantSourceFiles => await QuerySourceFilesAsync(query, cancellationToken),
            ContextCategory.PreviousTaskOutputs => await QueryTaskOutputsAsync(query, cancellationToken),
            ContextCategory.PreviousAgentResults => await QueryAgentResultsAsync(query, cancellationToken),
            // No Build/Test loop exists yet (later phase) — no data source to read from.
            ContextCategory.TestFailures => new ContextResult(Array.Empty<ContextDocument>()),
            ContextCategory.LearnedLessons => await QueryLearnedLessonsAsync(query, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(query), query.Category, "Unknown context category.")
        };
    }

    private async Task<ContextResult> QueryLearnedLessonsAsync(
        ContextQuery query,
        CancellationToken cancellationToken)
    {
        if (_learningStore is null)
        {
            return new ContextResult(Array.Empty<ContextDocument>());
        }

        var lessons = await _learningStore.GetByProjectAsync(query.ProjectId, cancellationToken);

        var documents = lessons
            .OrderByDescending(l => l.Level)
            .ThenByDescending(l => l.Confidence)
            .Take(query.MaxResults)
            .Select(l => new ContextDocument(
                $"lesson:{l.Id}",
                Truncate($"Problem: {l.Problem}\n\nSolution: {l.Solution}\n\nEvidence: {l.Evidence}"),
                l.Confidence,
                new Dictionary<string, string>
                {
                    ["level"] = l.Level.ToString(),
                    ["taskType"] = l.TaskType
                }))
            .ToList();

        return new ContextResult(documents);
    }

    private async Task<ContextResult> QueryRequirementsAsync(
        ContextQuery query,
        CancellationToken cancellationToken)
    {
        var requirements = await _db.Requirements
            .Where(x => x.ProjectId == query.ProjectId)
            .OrderByDescending(x => x.UpdatedAtUtc)
            .Take(query.MaxResults)
            .ToListAsync(cancellationToken);

        var documents = requirements.Select(r => new ContextDocument(
            $"requirement:{r.Id}",
            Truncate($"{r.Title}\n\n{r.Description}" +
                (string.IsNullOrWhiteSpace(r.AcceptanceCriteria) ? string.Empty : $"\n\nAcceptance Criteria:\n{r.AcceptanceCriteria}")),
            1.0,
            new Dictionary<string, string>
            {
                ["title"] = r.Title,
                ["status"] = r.Status
            }));

        return new ContextResult(documents.ToList());
    }

    private async Task<ContextResult> QueryProjectFileAsync(
        ContextQuery query,
        string[] candidateFileNames,
        CancellationToken cancellationToken)
    {
        var project = await GetProjectOrThrow(query.ProjectId, cancellationToken);

        // Phase 18.17 — resolve through the shared IWorkspaceResolver (honors
        // ProjectsBaseDirectory + AllowedRoot) instead of raw Path.GetFullPath.
        // Returns empty on an unresolvable workspace (graceful degradation —
        // same soft-failure as the prior "directory doesn't exist" check).
        var workspace = _workspaceResolver.Resolve(project);

        if (!workspace.IsValid)
        {
            return new ContextResult(Array.Empty<ContextDocument>());
        }

        var rootFullPath = workspace.Path!;

        foreach (var fileName in candidateFileNames)
        {
            var candidatePath = Path.GetFullPath(Path.Combine(rootFullPath, fileName));

            if (!candidatePath.StartsWith(rootFullPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!File.Exists(candidatePath))
            {
                continue;
            }

            var content = await File.ReadAllTextAsync(candidatePath, cancellationToken);

            return new ContextResult(new List<ContextDocument>
            {
                new(
                    candidatePath,
                    Truncate(content),
                    1.0,
                    new Dictionary<string, string> { ["fileName"] = fileName })
            });
        }

        return new ContextResult(Array.Empty<ContextDocument>());
    }

    private async Task<ContextResult> QuerySourceFilesAsync(
        ContextQuery query,
        CancellationToken cancellationToken)
    {
        var project = await GetProjectOrThrow(query.ProjectId, cancellationToken);

        // Phase 18.17 — same single resolver used by QueryProjectFileAsync above.
        var workspace = _workspaceResolver.Resolve(project);

        if (!workspace.IsValid)
        {
            return new ContextResult(Array.Empty<ContextDocument>());
        }

        var rootFullPath = workspace.Path!;

        var files = Directory.EnumerateFiles(rootFullPath, "*", SearchOption.AllDirectories)
            .Where(path => SourceFileExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .Where(path => !ExcludedDirectoryNames.Any(excluded =>
                path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Contains(excluded, StringComparer.OrdinalIgnoreCase)))
            .Select(path => new FileInfo(path))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Take(query.MaxResults)
            .ToList();

        var documents = new List<ContextDocument>();

        for (var i = 0; i < files.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var file = files[i];
            var content = await File.ReadAllTextAsync(file.FullName, cancellationToken);
            var relativePath = Path.GetRelativePath(rootFullPath, file.FullName);

            documents.Add(new ContextDocument(
                relativePath,
                Truncate(content),
                ComputeRelevance(i, files.Count),
                new Dictionary<string, string>
                {
                    ["lastWriteTimeUtc"] = file.LastWriteTimeUtc.ToString("O")
                }));
        }

        return new ContextResult(documents);
    }

    private async Task<ContextResult> QueryTaskOutputsAsync(
        ContextQuery query,
        CancellationToken cancellationToken)
    {
        var workItems = await _db.WorkItems
            .Where(x => x.ProjectId == query.ProjectId)
            .Include(x => x.HermesExecutions)
            .OrderByDescending(x => x.UpdatedAtUtc)
            .Take(query.MaxResults)
            .ToListAsync(cancellationToken);

        var documents = new List<ContextDocument>();

        for (var i = 0; i < workItems.Count; i++)
        {
            var workItem = workItems[i];
            var latestRun = workItem.HermesExecutions
                .OrderByDescending(r => r.StartedAtUtc)
                .FirstOrDefault();

            if (latestRun is null)
            {
                continue;
            }

            documents.Add(new ContextDocument(
                $"work-item:{workItem.Id}",
                Truncate(latestRun.Response ?? "(no output)"),
                ComputeRelevance(i, workItems.Count),
                new Dictionary<string, string>
                {
                    ["title"] = workItem.Title,
                    ["status"] = workItem.Status
                }));
        }

        return new ContextResult(documents);
    }

    private async Task<ContextResult> QueryAgentResultsAsync(
        ContextQuery query,
        CancellationToken cancellationToken)
    {
        var runs = await _db.HermesExecutions
            .Where(r => _db.WorkItems.Any(w => w.Id == r.WorkItemId && w.ProjectId == query.ProjectId))
            .OrderByDescending(r => r.StartedAtUtc)
            .Take(query.MaxResults)
            .ToListAsync(cancellationToken);

        var documents = new List<ContextDocument>();

        for (var i = 0; i < runs.Count; i++)
        {
            var run = runs[i];

            documents.Add(new ContextDocument(
                $"agent-run:{run.Id}",
                Truncate($"Agent: {run.Agent}\nModel: {run.Model}\nSuccess: {run.Success}\n\n{run.Response}\n{run.Error}"),
                ComputeRelevance(i, runs.Count),
                new Dictionary<string, string>
                {
                    ["agent"] = run.Agent,
                    ["success"] = run.Success.ToString(),
                    ["attemptNumber"] = run.AttemptNumber.ToString()
                }));
        }

        return new ContextResult(documents);
    }

    private async Task<Project> GetProjectOrThrow(Guid projectId, CancellationToken cancellationToken)
    {
        var project = await _db.Projects.FirstOrDefaultAsync(x => x.Id == projectId, cancellationToken);

        if (project is null)
        {
            throw new InvalidOperationException($"Project '{projectId}' was not found.");
        }

        return project;
    }

    private static double ComputeRelevance(int index, int total)
    {
        return total <= 1
            ? 1.0
            : Math.Round(1.0 - index / (double)total, 2);
    }

    private static string Truncate(string text)
    {
        return text.Length > MaxContentLength
            ? text[..MaxContentLength] + "...[truncated]"
            : text;
    }
}
