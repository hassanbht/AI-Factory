using AI.Factory.Domain.Entities;

namespace AI.Factory.Application.Abstractions;

/// <summary>
/// Phase Q.3 — durable ledger for authoritative provider generations.
/// Every captured generation is correlated to its Factory execution context
/// (epoch, benchmark identity, WorkItem, AgentRun). Generation ids are globally
/// unique; one generation is never silently assigned to multiple benchmark identities.
/// Unknown cost stays UNKNOWN (null), never zero.
/// </summary>
public interface IProviderGenerationLedger
{
    /// <summary>
    /// Records one observed upstream generation. Idempotent on <paramref name="generation"/>.GenerationId:
    /// a repeated record with the same generation id returns the existing row. A generation id already
    /// bound to a different AgentRun or benchmark identity is rejected (no silent reassignment).
    /// </summary>
    Task<ProviderGeneration> RecordGenerationAsync(
        ProviderGeneration generation,
        CancellationToken cancellationToken = default);

    Task<ProviderGeneration?> GetByGenerationIdAsync(
        string generationId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ProviderGeneration>> ListByAgentRunAsync(
        Guid agentRunId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies an authoritative usage outcome to one generation. Idempotent: repeating the same
    /// authoritative record leaves cumulative spend unchanged.
    /// </summary>
    Task<ProviderGeneration> ApplyAuthoritativeUsageAsync(
        string generationId,
        ProviderGenerationUsageOutcome outcome,
        CancellationToken cancellationToken = default);

    /// <summary>All generations whose accounting is not yet terminally resolved.</summary>
    Task<IReadOnlyList<ProviderGeneration>> ListUnresolvedAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sum of authoritative verified cost for one certification epoch. Returns null when any
    /// generation in the epoch is unresolved (callers must treat the total as UNKNOWN then).
    /// </summary>
    Task<decimal?> SumVerifiedCostForEpochAsync(
        string certificationEpoch,
        CancellationToken cancellationToken = default);
}

/// <summary>Validated authoritative usage outcome for one generation (never inferred).</summary>
public sealed record ProviderGenerationUsageOutcome(
    string RequestedGenerationId,
    string? ReturnedGenerationId,
    string? Model,
    int? PromptTokens,
    int? CompletionTokens,
    int? TotalTokens,
    decimal? TotalCostUsd,
    ProviderGenerationAccountingStatus AccountingStatus,
    string? Note = null);
