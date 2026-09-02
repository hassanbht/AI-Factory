using AI.Factory.Application.Abstractions;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;

namespace AI.Factory.Application.Learning;

/// <summary>
/// Governs what may be recorded as learned knowledge. The system must not
/// blindly learn from every agent output: lessons without supporting
/// evidence are refused outright, and a lesson is only ever marked
/// Verified when it is backed by a fully-validated EvaluationResult
/// (real build/test/review/approval evidence) — never from an LLM's
/// unverified say-so.
/// </summary>
public sealed class LearningService
{
    private readonly ILearningStore _store;

    public LearningService(ILearningStore store)
    {
        _store = store;
    }

    public async Task<Lesson?> RecordLessonAsync(
        Guid projectId,
        string taskType,
        string problem,
        string solution,
        string evidence,
        double confidence,
        string source,
        EvaluationResult? evaluation,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(evidence))
        {
            // Only store lessons when evidence supports them.
            return null;
        }

        var level = evaluation switch
        {
            { IsFullyValidated: true } => KnowledgeLevel.Verified,
            not null => KnowledgeLevel.Inferred,
            null => KnowledgeLevel.Observed
        };

        var lesson = new Lesson
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            TaskType = taskType,
            Problem = problem,
            Solution = solution,
            Evidence = evidence,
            Confidence = confidence,
            Source = source,
            Level = level,
            CreatedAtUtc = DateTime.UtcNow
        };

        return await _store.RecordAsync(lesson, cancellationToken);
    }

    public Task<IReadOnlyList<Lesson>> GetByProjectAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        return _store.GetByProjectAsync(projectId, cancellationToken);
    }
}
