using System.Text.Json;
using AI.Factory.Application.Abstractions;

namespace AI.Factory.Application.Orchestration;

/// <summary>Outcome of parsing a reviewer's raw output into a structured review payload.</summary>
public enum ReviewParseStatus
{
    /// <summary>Exactly one valid structured review payload was found and validated.</summary>
    Parsed,

    /// <summary>The reviewer produced no output at all.</summary>
    Missing,

    /// <summary>Output exists but carries no unambiguous, schema-valid review payload.</summary>
    Malformed
}

/// <summary>Result of <see cref="ReviewPayloadParser"/>; <paramref name="Reason"/> explains a non-Parsed status.</summary>
public sealed record ReviewParseResult(
    ReviewParseStatus Status,
    IReadOnlyList<ReviewFinding> Findings,
    string? Reason)
{
    public static ReviewParseResult Parsed(IReadOnlyList<ReviewFinding> findings) =>
        new(ReviewParseStatus.Parsed, findings, null);

    public static ReviewParseResult Missing(string reason) =>
        new(ReviewParseStatus.Missing, Array.Empty<ReviewFinding>(), reason);

    public static ReviewParseResult Malformed(string reason) =>
        new(ReviewParseStatus.Malformed, Array.Empty<ReviewFinding>(), reason);
}

/// <summary>
/// Phase P.1 — the strict, provider-neutral boundary between raw reviewer output and the
/// application-level review verdict.
///
/// A runtime's stdout may contain operational text (container start-up logs, progress lines)
/// before or after the model's structured response. The rules, in order:
/// <list type="number">
/// <item>If the output carries the explicit frame markers (exactly one begin and one end, in
/// order), ONLY the framed region is considered. A malformed frame is a malformed payload —
/// text outside the frame is never parsed and unrelated JSON outside it is never accepted.</item>
/// <item>Without a frame, the output is scanned for balanced JSON candidates (string- and
/// escape-aware). Exactly one schema-valid candidate is required: zero is malformed, more than
/// one is ambiguous and therefore malformed.</item>
/// <item>Schema validation is strict: every element must be an object with a parseable
/// severity and category. An entry is never silently dropped, and malformed output never
/// degrades to "no findings".</item>
/// </list>
/// The parser never assumes the entire raw stream is JSON, and never accepts a payload that
/// does not validate against the review schema.
/// </summary>
public static class ReviewPayloadParser
{
    /// <summary>Marker opening the machine-readable review response.</summary>
    public const string BeginMarker = "<<<FACTORY_REVIEW_RESULT>>>";

    /// <summary>Marker closing the machine-readable review response.</summary>
    public const string EndMarker = "<<<END_FACTORY_REVIEW_RESULT>>>";

    /// <summary>Upper bound on how much raw output a failure reason quotes (sanitized).</summary>
    public const int MaxQuotedOutput = 1200;

    /// <summary>Upper bound on JSON start positions examined before declaring output unusable.</summary>
    private const int MaxCandidateStarts = 512;

    /// <summary>
    /// The framed region of a review response, for the runtime-specific transport boundary:
    /// returns the text between exactly one begin/end marker pair (an optional single code fence
    /// inside the frame is tolerated), or the original output when no well-formed frame exists —
    /// so a malformed frame still reaches <see cref="Parse"/> and fails there visibly.
    /// </summary>
    public static string ExtractFramedRegion(string? output)
    {
        if (string.IsNullOrEmpty(output))
        {
            return output ?? string.Empty;
        }

        if (!TryFindFrame(output, out var inner, out _))
        {
            return output;
        }

        return TrimCodeFence(inner);
    }

    /// <summary>Parses raw reviewer output into findings, or explains why it cannot be trusted.</summary>
    public static ReviewParseResult Parse(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return ReviewParseResult.Missing("the reviewer produced no output at all.");
        }

        var beginCount = Count(output, BeginMarker);
        var endCount = Count(output, EndMarker);

        if (beginCount > 0 || endCount > 0)
        {
            if (!TryFindFrame(output, out var inner, out var frameError))
            {
                return ReviewParseResult.Malformed(
                    $"the review response frame is malformed ({frameError}); the payload cannot be trusted. "
                    + Quote(output));
            }

            var framed = TrimCodeFence(inner);
            if (string.IsNullOrWhiteSpace(framed))
            {
                return ReviewParseResult.Malformed(
                    $"the review response frame is present but empty. {Quote(output)}");
            }

            var framedCandidate = ParseCandidate(framed);
            return framedCandidate is null
                ? ReviewParseResult.Malformed(
                    "the framed review payload is not a schema-valid JSON findings document. " + Quote(framed))
                : ReviewParseResult.Parsed(framedCandidate);
        }

        // No frame: exactly one schema-valid JSON candidate may exist anywhere in the output.
        List<ReviewFinding>? accepted = null;
        var acceptedEnd = -1;
        var validCount = 0;

        var (candidates, exhausted) = FindJsonCandidates(output);

        foreach (var (start, end) in candidates)
        {
            if (start < acceptedEnd)
            {
                continue; // nested inside an already-accepted candidate
            }

            var parsed = ParseCandidate(output.Substring(start, end - start + 1));
            if (parsed is null)
            {
                continue;
            }

            validCount++;
            accepted = parsed;
            acceptedEnd = end;

            if (validCount > 1)
            {
                break;
            }
        }

        if (validCount == 1 && !exhausted)
        {
            return ReviewParseResult.Parsed(accepted!);
        }

        if (validCount > 1)
        {
            return ReviewParseResult.Malformed(
                "the reviewer output contains more than one structured review payload; the result is ambiguous. "
                + Quote(output));
        }

        return exhausted
            ? ReviewParseResult.Malformed(
                "the reviewer output is dominated by unstructured noise; no unambiguous review payload was located. "
                + Quote(output))
            : ReviewParseResult.Malformed(
                "no schema-valid structured review payload was found in the reviewer output. " + Quote(output));
    }

    /// <summary>
    /// Strict schema validation: an array of finding objects, or an object carrying a
    /// <c>findings</c> array. Returns null when anything at all fails validation.
    /// </summary>
    private static List<ReviewFinding>? ParseCandidate(string json)
    {
        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32
            });
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            JsonElement findingsElement;

            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                findingsElement = document.RootElement;
            }
            else if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("findings", out var wrapped)
                && wrapped.ValueKind == JsonValueKind.Array)
            {
                findingsElement = wrapped;
            }
            else
            {
                return null;
            }

            var findings = new List<ReviewFinding>();

            foreach (var element in findingsElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                if (!TryGetString(element, "severity", out var severityText)
                    || string.IsNullOrWhiteSpace(severityText)
                    || !Enum.TryParse<ReviewFindingSeverity>(severityText, true, out var severity))
                {
                    return null;
                }

                if (!TryGetString(element, "category", out var categoryText)
                    || string.IsNullOrWhiteSpace(categoryText)
                    || !Enum.TryParse<ReviewCategory>(categoryText, true, out var category))
                {
                    return null;
                }

                // Optional fields: present-with-a-wrong-type invalidates the payload;
                // absent is fine and never invented beyond the historical empty default.
                if (element.TryGetProperty("file", out var fileElement)
                    && fileElement.ValueKind is not (JsonValueKind.Null or JsonValueKind.String))
                {
                    return null;
                }

                if (element.TryGetProperty("line", out var lineElement)
                    && lineElement.ValueKind is not (JsonValueKind.Null or JsonValueKind.Number))
                {
                    return null;
                }

                int? line = lineElement.ValueKind == JsonValueKind.Number && lineElement.TryGetInt32(out var l)
                    ? l
                    : null;

                if (element.TryGetProperty("description", out var descElement)
                    && descElement.ValueKind is not (JsonValueKind.Null or JsonValueKind.String))
                {
                    return null;
                }

                if (element.TryGetProperty("recommendation", out var recElement)
                    && recElement.ValueKind is not (JsonValueKind.Null or JsonValueKind.String))
                {
                    return null;
                }

                findings.Add(new ReviewFinding(
                    severity,
                    category,
                    element.TryGetProperty("file", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null,
                    line,
                    element.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() ?? string.Empty : string.Empty,
                    element.TryGetProperty("recommendation", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() ?? string.Empty : string.Empty));
            }

            return findings;
        }
    }

    private static bool TryGetString(JsonElement element, string property, out string? value)
    {
        value = null;

        if (!element.TryGetProperty(property, out var propertyElement))
        {
            return false;
        }

        if (propertyElement.ValueKind == JsonValueKind.String)
        {
            value = propertyElement.GetString();
            return true;
        }

        return propertyElement.ValueKind == JsonValueKind.Null;
    }

    private static bool TryFindFrame(string output, out string inner, out string? error)
    {
        inner = string.Empty;
        error = null;

        var beginCount = Count(output, BeginMarker);
        var endCount = Count(output, EndMarker);

        if (beginCount != 1 || endCount != 1)
        {
            error = beginCount == 0 && endCount == 0
                ? "no frame present"
                : $"expected exactly one '{BeginMarker}' and one '{EndMarker}', found {beginCount}/{endCount}";
            return false;
        }

        var begin = output.IndexOf(BeginMarker, StringComparison.Ordinal);
        var end = output.IndexOf(EndMarker, StringComparison.Ordinal);

        if (end <= begin)
        {
            error = "the end marker precedes the begin marker";
            return false;
        }

        inner = output[(begin + BeginMarker.Length)..end];
        return true;
    }

    private static string TrimCodeFence(string text)
    {
        var trimmed = text.Trim();

        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var firstBreak = trimmed.IndexOf('\n');
        if (firstBreak < 0)
        {
            return trimmed;
        }

        var withoutOpening = trimmed[(firstBreak + 1)..];
        var closing = withoutOpening.LastIndexOf("```", StringComparison.Ordinal);
        return closing >= 0 ? withoutOpening[..closing].Trim() : trimmed;
    }

    /// <summary>Balanced top-level JSON candidates, bounded so pathological output cannot turn
    /// parsing into an unbounded scan. <c>Exhausted</c> reports that the bound was hit — the
    /// caller then treats the output as unusable rather than trusting a partial scan.</summary>
    private static (List<(int Start, int End)> Candidates, bool Exhausted) FindJsonCandidates(string text)
    {
        var candidates = new List<(int, int)>();
        var starts = 0;

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is not ('[' or '{'))
            {
                continue;
            }

            if (++starts > MaxCandidateStarts)
            {
                return (candidates, true);
            }

            var end = BalancedEnd(text, i);
            if (end > i)
            {
                candidates.Add((i, end));
            }
        }

        return (candidates, false);
    }

    private static int BalancedEnd(string text, int start)
    {
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

            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '[':
                case '{':
                    depth++;
                    break;
                case ']':
                    depth--;
                    if (depth == 0)
                    {
                        return i;
                    }

                    if (depth < 0)
                    {
                        return -1;
                    }

                    break;
                case '}':
                    depth--;
                    if (depth == 0)
                    {
                        return i;
                    }

                    if (depth < 0)
                    {
                        return -1;
                    }

                    break;
            }
        }

        return -1;
    }

    private static int Count(string text, string token)
    {
        var count = 0;
        var index = 0;

        while ((index = text.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += token.Length;
        }

        return count;
    }

    private static string Quote(string text)
    {
        var sanitized = Application.Security.SensitiveDataSanitizer.Sanitize(text).Trim();
        var singleLine = sanitized.ReplaceLineEndings(" ");

        if (singleLine.Length <= MaxQuotedOutput)
        {
            return $"Output: '{singleLine}'";
        }

        return $"Output (truncated): '{singleLine[..MaxQuotedOutput]}…'";
    }
}
