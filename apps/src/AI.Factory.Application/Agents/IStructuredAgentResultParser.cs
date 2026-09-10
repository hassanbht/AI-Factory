namespace AI.Factory.Application.Agents;

/// <summary>
/// Extracts and validates a <see cref="StructuredAgentResult"/> envelope from raw
/// agent output text. Provider-neutral by construction — operates on plain text,
/// never on a specific LLM's API shape. Never trusts a result simply because it
/// parses as JSON: required fields and a supported <see cref="StructuredAgentResult.Version"/>
/// are mandatory, per Phase 18.1.8 §4.
/// </summary>
public interface IStructuredAgentResultParser
{
    StructuredResultParseOutcome Parse(string? rawOutput);
}

public enum StructuredResultParseFailureReason
{
    /// <summary>No output text at all, or nothing that looks like a JSON object.</summary>
    Missing,

    /// <summary>A JSON object was found but failed to parse, or lacked a required field.</summary>
    Malformed,

    /// <summary>Parsed cleanly, but <see cref="StructuredAgentResult.Version"/> is not one this Factory understands.</summary>
    UnsupportedVersion
}

/// <summary>
/// <paramref name="Result"/> is non-null only when <paramref name="IsValid"/> is true.
/// <paramref name="FailureReason"/>/<paramref name="Message"/> are populated only when it is false.
/// </summary>
public sealed record StructuredResultParseOutcome(
    bool IsValid,
    StructuredAgentResult? Result,
    StructuredResultParseFailureReason? FailureReason,
    string? Message)
{
    public static StructuredResultParseOutcome Valid(StructuredAgentResult result) =>
        new(true, result, null, null);

    public static StructuredResultParseOutcome Invalid(StructuredResultParseFailureReason reason, string message) =>
        new(false, null, reason, message);
}
