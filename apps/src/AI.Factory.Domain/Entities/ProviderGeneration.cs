namespace AI.Factory.Domain.Entities;

/// <summary>
/// Phase Q.3 — one authoritative provider generation linked to a Factory execution.
/// Captures the real upstream generation (OpenRouter <c>gen-…</c> id) produced by the
/// execution path that actually billed the run (currently: Hermes CLI inside its
/// container), correlated to the Factory-controlled execution context that launched it.
/// One <see cref="AgentRun"/> may own many generations; a generation belongs to at most
/// one benchmark identity. Unknown or incomplete usage/cost stays UNKNOWN (null),
/// never zero. Prompts, completions, secrets and raw request bodies are never stored here.
/// </summary>
public sealed class ProviderGeneration
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Certification epoch (e.g. PhaseQ-Certification-Epoch-2); null for non-benchmark runs.</summary>
    public string? CertificationEpoch { get; set; }

    /// <summary>Exact benchmark identity (e.g. Q-A-1); null for non-benchmark runs.</summary>
    public string? BenchmarkIdentity { get; set; }

    public Guid? WorkItemId { get; set; }

    public Guid AgentRunId { get; set; }

    /// <summary>Upstream generation id (OpenRouter <c>gen-…</c>). Unique across the ledger.</summary>
    public string GenerationId { get; set; } = string.Empty;

    public string? ModelId { get; set; }

    public string? Provider { get; set; }

    public DateTime RequestedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Authoritative billed cost in USD; null while UNKNOWN (pending, failed, or unusable).</summary>
    public decimal? AuthoritativeCostUsd { get; set; }

    public int? PromptTokens { get; set; }

    public int? CompletionTokens { get; set; }

    public int? TotalTokens { get; set; }

    public ProviderGenerationAccountingStatus AccountingStatus { get; set; }
        = ProviderGenerationAccountingStatus.PendingUsage;

    /// <summary>Short, redacted accounting note (status codes, no prompts/secrets/content).</summary>
    public string? AccountingNote { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
