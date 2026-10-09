using System.Text.Json;

namespace AI.Factory.Application.CostAccounting;

public static partial class OpenRouterGenerationCapture
{
    /// <summary>
    /// Extracts candidate generation ids from response headers + JSON/SSE body.
    /// Returns at most one record per distinct, well-formed <c>gen-…</c> id.
    /// Malformed frames and missing terminal usage are skipped (never guessed);
    /// callers treat "no captured generation" as UNKNOWN, never zero.
    /// </summary>
    public static IReadOnlyList<CapturedGeneration> CaptureFromResponse(
        IReadOnlyDictionary<string, string>? headers,
        string? body,
        bool isStreaming)
    {
        var found = new Dictionary<string, CapturedGeneration>(StringComparer.Ordinal);
        var headerId = ExtractHeaderGenerationId(headers);
        if (headerId is not null && !found.ContainsKey(headerId))
        {
            found[headerId] = new CapturedGeneration(headerId, null, null, null, null, null);
        }

        if (string.IsNullOrEmpty(body))
        {
            return ReturnBounded(found);
        }

        if (isStreaming)
        {
            foreach (var generation in CaptureFromSse(body))
            {
                if (!found.ContainsKey(generation.GenerationId))
                {
                    found[generation.GenerationId] = generation;
                }
                else if (found[generation.GenerationId].TotalTokens is null && generation.TotalTokens is not null)
                {
                    found[generation.GenerationId] = generation;
                }

                if (found.Count >= MaxGenerationsPerResponse)
                {
                    break;
                }
            }
        }
        else
        {
            foreach (var generation in CaptureFromJson(body))
            {
                if (!found.ContainsKey(generation.GenerationId))
                {
                    found[generation.GenerationId] = generation;
                }

                if (found.Count >= MaxGenerationsPerResponse)
                {
                    break;
                }
            }
        }

        return ReturnBounded(found);
    }

    private static string? ExtractHeaderGenerationId(IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is null)
        {
            return null;
        }

        foreach (var (key, value) in headers)
        {
            if (string.Equals(key, GenerationIdHeaderName, StringComparison.OrdinalIgnoreCase)
                && IsWellFormedGenerationId(value?.Trim()))
            {
                return value!.Trim();
            }
        }

        return null;
    }

    private static IReadOnlyList<CapturedGeneration> ReturnBounded(Dictionary<string, CapturedGeneration> found)
    {
        if (found.Count <= MaxGenerationsPerResponse)
        {
            return found.Values.ToList();
        }

        return found.Values.Take(MaxGenerationsPerResponse).ToList();
    }
}
