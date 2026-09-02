using AI.Factory.Application.Abstractions;
using AI.Factory.Domain.Entities;

namespace AI.Factory.Application.Orchestration;

public sealed class ReviewContextBuilder : IReviewContextBuilder
{
    private const int ContextDocumentsPerCategory = 10;

    private readonly IContextProvider _contextProvider;
    private readonly IArtifactRepository _artifactRepository;
    private readonly IValidationRunRepository _validationRunRepository;

    public ReviewContextBuilder(
        IContextProvider contextProvider,
        IArtifactRepository artifactRepository,
        IValidationRunRepository validationRunRepository)
    {
        _contextProvider = contextProvider;
        _artifactRepository = artifactRepository;
        _validationRunRepository = validationRunRepository;
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

        return new ReviewContext(
            workItem.Id,
            workItem.ProjectId,
            JoinDocuments(requirements.Documents),
            workItem.AcceptanceCriteria,
            JoinDocuments(architecture.Documents),
            artifacts.Select(a => a.Path).ToList(),
            latestValidation?.TestOutput,
            latestValidation?.TestsSucceeded,
            latestValidation?.BuildOutput ?? string.Empty,
            latestValidation?.BuildSucceeded ?? false,
            JoinDocuments(taskHistory.Documents));
    }

    private static string JoinDocuments(IReadOnlyList<ContextDocument> documents)
    {
        return string.Join("\n\n", documents.Select(d => $"[{d.Source}]\n{d.Content}"));
    }
}
