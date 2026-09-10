namespace AI.Factory.Application.Abstractions;

/// <summary>
/// Deterministically selects a configured AI provider/model for a
/// capability-tagged request and executes it, applying bounded fallback
/// across capability-compatible candidates on eligible transient failures.
/// Orchestration code depends on this instead of any concrete provider.
/// </summary>
public interface IAiModelRouter
{
    Task<AiCompletionResult> ExecuteAsync(
        AiCompletionRequest request,
        CancellationToken cancellationToken = default);
}
