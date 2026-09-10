using System.Text.Json;
using System.Text.Json.Serialization;

namespace AI.Factory.Application.Agents;

/// <summary>
/// Versioned, structured envelope for an agent's own self-reported outcome —
/// separate from and never a driver of <see cref="AI.Factory.Domain.Enums.AgentRunStatus"/>,
/// which stays governed purely by the runtime-level success/failure/cancellation/
/// timeout classification it already had before this contract existed. This
/// envelope carries what the AGENT itself said about its own execution, once
/// that self-report has been parsed and validated — untrusted LLM output,
/// never auto-authorizing anything (Phase 18.1.8 §14).
/// </summary>
public sealed record StructuredAgentResult(
    int Version,
    StructuredResultStatus Status,
    string? Summary,
    string? Output,
    string? Data,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Artifacts,
    IReadOnlyDictionary<string, string>? Metadata)
{
    /// <summary>The only envelope version this Factory currently understands. A result declaring any other version is UnsupportedVersion, never guessed at.</summary>
    public const int CurrentVersion = 1;

    /// <summary>
    /// The single, shared serialization contract for this type — string enum
    /// values (human-readable in the persisted JSON column and consistent
    /// with what the parser accepts from agent output), case-insensitive
    /// property matching. Every serialize/deserialize of a StructuredAgentResult
    /// anywhere in the codebase (orchestrator persistence, API read-back,
    /// tests) must go through this, not a locally-invented options instance.
    /// </summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };
}

/// <summary>The agent's own self-reported outcome — distinct from AgentRunStatus.</summary>
public enum StructuredResultStatus
{
    Succeeded,
    Failed,
    Cancelled,
    TimedOut,

    /// <summary>The envelope itself could not be trusted (malformed/missing/unsupported version) — never a status an agent reports about itself.</summary>
    Invalid
}
