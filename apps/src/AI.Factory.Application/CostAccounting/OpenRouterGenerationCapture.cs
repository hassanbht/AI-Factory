namespace AI.Factory.Application.CostAccounting;

/// <summary>
/// Phase Q.3 — parses provider-side protocol capture into ledger-grade
/// generation records. No HTTP here: callers supply what the Factory-owned
/// capture path observed (headers, body frames). Never logs or returns model
/// content; only ids, model names, token counts and cost.
/// </summary>
public static partial class OpenRouterGenerationCapture
{
    public const string GenerationIdHeaderName = "X-Generation-Id";
    public const string GenerationIdPrefix = "gen-";

    /// <summary>Maximum generations parsed from one response (fail closed above it).</summary>
    public const int MaxGenerationsPerResponse = 32;

    /// <summary>Maximum bytes of one SSE frame examined (content beyond it is ignored, never stored).</summary>
    public const int MaxFrameBytes = 64 * 1024;

    /// <summary>Maximum SSE frames examined per response (fail closed above it).</summary>
    public const int MaxFramesPerResponse = 4096;

    public sealed record CapturedGeneration(
        string GenerationId,
        string? Model,
        int? PromptTokens,
        int? CompletionTokens,
        int? TotalTokens,
        string? StreamUsageSource);

    public static bool IsWellFormedGenerationId(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.StartsWith(GenerationIdPrefix, StringComparison.Ordinal)
        && value.Length is >= 8 and <= 128
        && value.Skip(GenerationIdPrefix.Length).All(c => char.IsLetterOrDigit(c) || c == '-' || c == '_');
}
