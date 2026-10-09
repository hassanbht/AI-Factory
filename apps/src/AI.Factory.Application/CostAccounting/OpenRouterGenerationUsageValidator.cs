using System.Text.Json;

namespace AI.Factory.Application.CostAccounting;

/// <summary>
/// Phase Q.3 — validates one authoritative OpenRouter generation lookup
/// (<c>GET /generation?id=gen-…</c> → <c>data.total_cost</c> + usage).
/// Pure validation: no HTTP here. Any missing, malformed, mismatched or
/// unusable record leaves the generation uncertified (UNKNOWN, never zero).
/// </summary>
public static class OpenRouterGenerationUsageValidator
{
    public sealed record ValidatedUsage(
        string GenerationId,
        string? Model,
        int? PromptTokens,
        int? CompletionTokens,
        int? TotalTokens,
        decimal TotalCostUsd,
        bool IsTransientFailure,
        string? Note);

    public sealed record ValidationResult(bool Ok, ValidatedUsage? Usage, string? Error, bool IsTransient);

    /// <summary>HTTP statuses the generation lookup may return; everything else fails closed.</summary>
    public static bool IsTransientStatus(int statusCode) =>
        statusCode == 429 || (statusCode >= 500 && statusCode <= 599);

    public static ValidationResult Validate(
        string requestedGenerationId,
        int httpStatusCode,
        string? responseBody)
    {
        if (!OpenRouterGenerationCapture.IsWellFormedGenerationId(requestedGenerationId))
        {
            return new ValidationResult(false, null, "Requested generation id is malformed.", false);
        }

        if (httpStatusCode == 429 || (httpStatusCode >= 500 && httpStatusCode <= 599))
        {
            return new ValidationResult(false, null, $"Generation lookup transiently unavailable (HTTP {httpStatusCode}).", true);
        }

        if (httpStatusCode is 401 or 402 or 404)
        {
            var terminal = httpStatusCode == 404 ? false : false;
            return new ValidationResult(
                false, null,
                $"Generation lookup returned HTTP {httpStatusCode}; authoritative cost unavailable.",
                httpStatusCode == 404 ? false : terminal);
        }

        if (httpStatusCode != 200)
        {
            return new ValidationResult(false, null, $"Generation lookup returned unexpected HTTP {httpStatusCode}.", false);
        }

        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return new ValidationResult(false, null, "Generation lookup returned an empty body.", true);
        }

        JsonDocument? document = null;
        try
        {
            document = JsonDocument.Parse(responseBody);
        }
        catch (JsonException)
        {
            return new ValidationResult(false, null, "Generation lookup body is not valid JSON.", false);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Object)
            {
                return new ValidationResult(false, null, "Generation lookup envelope is missing 'data'.", false);
            }

            var returnedId = ReadString(data, "id");
            if (!string.Equals(returnedId, requestedGenerationId, StringComparison.Ordinal))
            {
                return new ValidationResult(false, null, "Returned generation id does not match the requested id.", false);
            }

            var model = ReadString(data, "model");
            var prompt = ReadPositiveInt(data, "tokens_prompt") ?? ReadPositiveInt(data, "native_tokens_prompt");
            var completion = ReadPositiveInt(data, "tokens_completion") ?? ReadPositiveInt(data, "native_tokens_completion");
            var total = ReadPositiveInt(data, "tokens_prompt") is { } p && ReadPositiveInt(data, "tokens_completion") is { } c
                ? p + c
                : (int?)null;

            var cost = ReadCost(data, "total_cost");
            if (cost is null)
            {
                return new ValidationResult(false, null, "Authoritative 'total_cost' is missing, null, or unusable.", true);
            }

            return new ValidationResult(
                true,
                new ValidatedUsage(requestedGenerationId, model, prompt, completion, total, cost.Value, false, null),
                null,
                false);
        }
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? (string.IsNullOrWhiteSpace(value.GetString()) ? null : value.GetString()!.Trim())
            : null;

    private static int? ReadPositiveInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var parsed) && parsed > 0 ? parsed : null;

    private static decimal? ReadCost(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        decimal parsed;
        if (value.ValueKind == JsonValueKind.Number)
        {
            if (!value.TryGetDecimal(out parsed))
            {
                return null;
            }
        }
        else if (value.ValueKind == JsonValueKind.String)
        {
            if (!decimal.TryParse(
                    value.GetString(),
                    System.Globalization.NumberStyles.Number,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out parsed))
            {
                return null;
            }
        }
        else
        {
            return null;
        }

        if (parsed < 0 || parsed > 10_000m)
        {
            return null;
        }

        return decimal.Round(parsed, 8, MidpointRounding.ToZero);
    }
}
