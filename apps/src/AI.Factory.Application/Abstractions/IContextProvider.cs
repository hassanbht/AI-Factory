namespace AI.Factory.Application.Abstractions;

public interface IContextProvider
{
    Task<ContextResult> QueryAsync(
        ContextQuery query,
        CancellationToken cancellationToken = default);
}

public enum ContextCategory
{
    ProjectRequirements,
    ArchitectureDocuments,
    CodingStandards,
    PreviousTaskOutputs,
    RelevantSourceFiles,
    TestFailures,
    PreviousAgentResults,
    ProjectDecisions,
    LearnedLessons
}

public sealed record ContextQuery(
    Guid ProjectId,
    ContextCategory Category,
    int MaxResults = 20);

public sealed record ContextDocument(
    string Source,
    string Content,
    double Relevance,
    IReadOnlyDictionary<string, string> Metadata);

public sealed record ContextResult(
    IReadOnlyList<ContextDocument> Documents);
