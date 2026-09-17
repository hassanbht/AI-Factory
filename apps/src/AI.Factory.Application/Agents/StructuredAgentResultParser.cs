using System.Text.Json;

namespace AI.Factory.Application.Agents;

/// <summary>
/// Default <see cref="IStructuredAgentResultParser"/>: collects every balanced
/// JSON object in the raw text (LLMs routinely wrap it in prose or a fenced code
/// block), deserializes them leniently, then enforces the envelope's required
/// fields and supported version. Pure text/JSON processing — no provider or
/// network dependency.
///
/// Phase 18.23 §2 — candidates are evaluated newest-first and the agent's LAST
/// complete, valid envelope wins. That is a deterministic selection rule, not a
/// repair: nothing is inferred, invented or merged across candidates, and an
/// agent that never finished a valid envelope still fails closed. It exists
/// because a real live Planner run was observed abandoning a half-written
/// object, writing prose ("Let me produce the complete and correct version:"),
/// and only then emitting the finished envelope — under the previous
/// first-object rule the unclosed prefix swallowed everything after it and the
/// whole run was reported as having produced no structured result at all.
/// </summary>
public sealed class StructuredAgentResultParser : IStructuredAgentResultParser
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// How many of the newest balanced objects are deserialized before the scan
    /// gives up. A real envelope is always among the last few closed; this only
    /// bounds the work a pathological, brace-heavy blob can cause.
    /// </summary>
    private const int MaxEvaluatedCandidates = 64;

    public StructuredResultParseOutcome Parse(string? rawOutput)
    {
        if (string.IsNullOrWhiteSpace(rawOutput))
        {
            return StructuredResultParseOutcome.Invalid(
                StructuredResultParseFailureReason.Missing, "Agent produced no output.");
        }

        var candidates = ExtractJsonObjects(rawOutput);

        if (candidates.Count == 0)
        {
            return StructuredResultParseOutcome.Invalid(
                StructuredResultParseFailureReason.Missing, "No JSON object found in agent output.");
        }

        // Newest-first: the last complete envelope the agent wrote is its final
        // answer. Every earlier candidate (a nested object, an abandoned first
        // attempt) is skipped whole — never merged into the result.
        StructuredResultParseOutcome? newestFailure = null;
        var evaluated = 0;

        for (var i = candidates.Count - 1; i >= 0 && evaluated < MaxEvaluatedCandidates; i--, evaluated++)
        {
            var outcome = ParseCandidate(candidates[i]);

            if (outcome.IsValid)
            {
                return outcome;
            }

            // When nothing validates, report the most recently closed
            // candidate — the largest object the agent actually finished, and
            // so the most informative diagnosis.
            newestFailure ??= outcome;
        }

        return newestFailure!;
    }

    private static StructuredResultParseOutcome ParseCandidate(string jsonCandidate)
    {
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
            ReadMetadata(envelope.Metadata));

        return StructuredResultParseOutcome.Valid(result);
    }

    /// <summary>
    /// Phase 18.23 — <c>metadata</c> is a free-form, non-authoritative side
    /// channel: nothing in the Factory reads a decision out of it. Binding it
    /// as a string map meant an agent writing
    /// <c>"metadata":{"files_changed":["a.cs"]}</c> — observed live — threw away
    /// the whole envelope, including the <c>data</c> the Factory does care
    /// about. A non-string value is now kept as its raw JSON text instead:
    /// nothing is dropped, nothing is invented, and no required field's
    /// validation is relaxed (version, status and the rest are enforced exactly
    /// as before, above).
    /// </summary>
    private static IReadOnlyDictionary<string, string>? ReadMetadata(Dictionary<string, JsonElement>? metadata)
    {
        if (metadata is null)
        {
            return null;
        }

        return metadata.ToDictionary(
            entry => entry.Key,
            entry => entry.Value.ValueKind == JsonValueKind.String
                ? entry.Value.GetString() ?? string.Empty
                : entry.Value.GetRawText());
    }

    /// <summary>
    /// Every balanced <c>{...}</c> region in the text, ordered by where its
    /// closing brace was reached, respecting string literals (so a brace inside
    /// a quoted value never throws off the depth count). An unclosed <c>{</c>
    /// simply yields no candidate of its own instead of swallowing everything
    /// after it — that swallowing is the boundary defect Phase 18.23 §24
    /// traced live. Nested objects are collected too; they are cheap to reject
    /// in <see cref="ParseCandidate"/> and keep this a single linear pass with
    /// no guessing about which brace "looks like" the envelope.
    /// </summary>
    private static List<string> ExtractJsonObjects(string text)
    {
        var candidates = new List<string>();
        var openPositions = new Stack<int>();
        var inString = false;
        var escaped = false;

        for (var i = 0; i < text.Length; i++)
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
                openPositions.Push(i);
            }
            else if (c == '}' && openPositions.Count > 0)
            {
                candidates.Add(text[openPositions.Pop()..(i + 1)]);
            }
        }

        return candidates;
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
        public Dictionary<string, JsonElement>? Metadata { get; set; }
    }
}
