using AI.Factory.Application.Abstractions;
using AI.Factory.Domain.Entities;

namespace AI.Factory.Application.Orchestration;

public sealed class ReviewContextBuilder : IReviewContextBuilder
{
    private const int ContextDocumentsPerCategory = 10;

    /// <summary>
    /// Phase 18.29.1 — an upper bound on how many changed-file paths reach the review
    /// prompt.
    ///
    /// FileSystemArtifactRecorder records every file in the workspace, and the review
    /// prompt is delivered to Hermes as a `docker run ... -z &lt;prompt&gt;` ARGUMENT.
    /// A workspace that picked up a vendored toolchain therefore produced a command
    /// line past the OS limit, and the whole review stage failed with "Failed to start
    /// process 'docker': Argument list too long" — the independent review could not be
    /// performed at all, which is a far worse outcome than reviewing a truncated list.
    ///
    /// 200 is well clear of any real changeset while keeping the command line bounded.
    /// The truncation is stated in the prompt rather than hidden, so the reviewer knows
    /// it is not seeing everything.
    /// </summary>
    public const int MaxChangedFilesInPrompt = 200;

    private readonly IContextProvider _contextProvider;
    private readonly IArtifactRepository _artifactRepository;
    private readonly IValidationRunRepository _validationRunRepository;
    private readonly IWorkspaceResolver? _workspaceResolver;
    private readonly IReviewBundleBuilder? _reviewBundleBuilder;
    private readonly IProjectRepository? _projectRepository;

    public ReviewContextBuilder(
        IContextProvider contextProvider,
        IArtifactRepository artifactRepository,
        IValidationRunRepository validationRunRepository,
        IWorkspaceResolver? workspaceResolver = null,
        IReviewBundleBuilder? reviewBundleBuilder = null,
        IProjectRepository? projectRepository = null)
    {
        _contextProvider = contextProvider;
        _artifactRepository = artifactRepository;
        _validationRunRepository = validationRunRepository;
        _workspaceResolver = workspaceResolver;
        _reviewBundleBuilder = reviewBundleBuilder;
        _projectRepository = projectRepository;
    }

    public async Task<ReviewContext> BuildAsync(
        WorkItem workItem,
        CancellationToken cancellationToken = default)
    {
        var requirements = await _contextProvider.QueryAsync(
            new ContextQuery(workItem.ProjectId, ContextCategory.ProjectRequirements, ContextDocumentsPerCategory),
            cancellationToken);

        var architecture = await _contextProvider.QueryAsync(
            new ContextQuery(workItem.ProjectId, ContextCategory.ArchitectureDocuments, ContextDocumentsPerCategory),
            cancellationToken);

        var taskHistory = await _contextProvider.QueryAsync(
            new ContextQuery(workItem.ProjectId, ContextCategory.PreviousAgentResults, ContextDocumentsPerCategory),
            cancellationToken);

        var artifacts = await _artifactRepository.GetByWorkItemIdAsync(workItem.Id, cancellationToken);
        var latestValidation = await _validationRunRepository.GetLatestAsync(workItem.Id, cancellationToken);

        ReviewBundle? bundle = null;
        if (_reviewBundleBuilder is not null && _workspaceResolver is not null)
        {
            var project = _projectRepository is not null ? await _projectRepository.GetByIdAsync(workItem.ProjectId, cancellationToken) : null;
            if (project is not null)
            {
                var workspace = _workspaceResolver.Resolve(project);
                if (workspace.IsValid && !string.IsNullOrWhiteSpace(workspace.Path))
                {
                    bundle = await _reviewBundleBuilder.BuildBundleAsync(workspace.Path, null, cancellationToken);
                }
            }
        }

        var changedFiles = bundle?.ChangedFiles is { Count: > 0 }
            ? bundle.ChangedFiles
            : artifacts.Select(a => a.Path).ToList();

        return new ReviewContext(
            workItem.Id,
            workItem.ProjectId,
            JoinDocuments(requirements.Documents),
            workItem.AcceptanceCriteria,
            JoinDocuments(architecture.Documents),
            BoundChangedFiles(changedFiles),
            latestValidation?.TestOutput,
            latestValidation?.TestsSucceeded,
            latestValidation?.BuildOutput ?? string.Empty,
            latestValidation?.BuildSucceeded ?? false,
            JoinDocuments(taskHistory.Documents),
            bundle);
    }

    /// <summary>
    /// Caps the changed-file list at <see cref="MaxChangedFilesInPrompt"/>, appending an
    /// explicit marker so the reviewer is told the list was truncated rather than being
    /// left to assume it is complete. See that constant for why the bound exists.
    /// </summary>
    private static IReadOnlyList<string> BoundChangedFiles(IReadOnlyList<string> paths)
    {
        if (paths.Count <= MaxChangedFilesInPrompt)
        {
            return paths;
        }

        return paths
            .Take(MaxChangedFilesInPrompt)
            .Append($"… and {paths.Count - MaxChangedFilesInPrompt} more file(s) not listed.")
            .ToList();
    }

    private static string JoinDocuments(IReadOnlyList<ContextDocument> documents)
    {
        return string.Join("\n\n", documents.Select(d => $"[{d.Source}]\n{d.Content}"));
    }
}
