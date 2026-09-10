using System.Text.Json;

namespace AI.Factory.Application.Agents;

/// <summary>
/// Default <see cref="IStructuredAgentResultParser"/>: finds the first balanced
/// JSON object in the raw text (LLMs routinely wrap it in prose or a fenced code
/// block), deserializes it leniently, then enforces the envelope's required
/// fields and supported version. Pure text/JSON processing — no provider or
/// network dependency.
/// </summary>
public sealed class StructuredAgentResultParser : IStructuredAgentResultParser
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public StructuredResultParseOutcome Parse(string? rawOutput)
    {
        if (string.IsNullOrWhiteSpace(rawOutput))
        {
            return StructuredResultParseOutcome.Invalid(
                StructuredResultParseFailureReason.Missing, "Agent produced no output.");
        }

        var jsonCandidate = ExtractJsonObject(rawOutput);

        if (jsonCandidate is null)
        {
            return StructuredResultParseOutcome.Invalid(
                StructuredResultParseFailureReason.Missing, "No JSON object found in agent output.");
        }

        StructuredResultEnvelope? envelope;

        try
        {
            envelope = JsonSerializer.Deserialize<StructuredResultEnvelope>(jsonCandidate, JsonOptions);
        }
        catch (JsonException ex)
        {
            return StructuredResultParseOutcome.Invalid(
                StructuredResultParseFailureReason.Malformed, $"Structured result JSON failed to parse: {ex.Message}");
        }

        if (envelope is null)
        {
            return StructuredResultParseOutcome.Invalid(
                StructuredResultParseFailureReason.Malformed, "Structured result JSON parsed to null.");
        }

        if (envelope.Version is null)
        {
            return StructuredResultParseOutcome.Invalid(
                StructuredResultParseFailureReason.Malformed, "Structured result is missing required field 'version'.");
        }

        if (envelope.Version.Value != StructuredAgentResult.CurrentVersion)
        {
            return StructuredResultParseOutcome.Invalid(
                StructuredResultParseFailureReason.UnsupportedVersion,
                $"Structured result declares version {envelope.Version.Value}, only {StructuredAgentResult.CurrentVersion} is supported.");
        }

        if (string.IsNullOrWhiteSpace(envelope.Status)
            || !Enum.TryParse<StructuredResultStatus>(envelope.Status, ignoreCase: true, out var status)
            || status == StructuredResultStatus.Invalid)
        {
            return StructuredResultParseOutcome.Invalid(
                StructuredResultParseFailureReason.Malformed, "Structured result has a missing or unrecognized 'status' value.");
        }

        var result = new StructuredAgentResult(
            envelope.Version.Value,
            status,
            envelope.Summary,
            envelope.Output,
            envelope.Data?.GetRawText(),
            (IReadOnlyList<string>?)envelope.Warnings ?? Array.Empty<string>(),
            (IReadOnlyList<string>?)envelope.Errors ?? Array.Empty<string>(),
            (IReadOnlyList<string>?)envelope.Artifacts ?? Array.Empty<string>(),
            envelope.Metadata);

        return StructuredResultParseOutcome.Valid(result);
    }

    /// <summary>
    /// Scans for the first `{` and returns the substring up to its matching
    /// `}`, respecting string literals (so a brace inside a quoted value never
    /// throws off the depth count). Returns null if no balanced object exists.
    /// </summary>
    private static string? ExtractJsonObject(string text)
    {
        var start = text.IndexOf('{');

        if (start < 0)
        {
            return null;
        }

        var depth = 0;
        var inString = false;
        var escaped = false;

        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];

            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (c == '\\')
                {
                    escaped = true;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (c == '"')
            {
                inString = true;
            }
            else if (c == '{')
            {
                depth++;
            }
            else if (c == '}')
            {
                depth--;

                if (depth == 0)
                {
                    return text[start..(i + 1)];
                }
            }
        }

        return null;
    }

    private sealed class StructuredResultEnvelope
    {
        public int? Version { get; set; }
        public string? Status { get; set; }
        public string? Summary { get; set; }
        public string? Output { get; set; }
        public JsonElement? Data { get; set; }
        public List<string>? Warnings { get; set; }
        public List<string>? Errors { get; set; }
        public List<string>? Artifacts { get; set; }
        public Dictionary<string, string>? Metadata { get; set; }
    }
}
