namespace AI.Factory.Application.Orchestration;

/// <summary>
/// Phase P.1 — fail-closed rules for model identifiers.
///
/// Model selection in the Factory must be explicit, deterministic and reject anything that
/// delegates the choice to someone else. An "auto" identifier (an automatic model router such
/// as <c>auto/coding</c> or <c>openrouter/auto</c>) is exactly such a delegation: the Factory
/// cannot know which model actually ran, so it is rejected everywhere a model id is accepted.
/// A malformed identifier (whitespace, control characters, empty path segments, leading or
/// trailing separators) is rejected for the same reason: routing must be deterministic.
/// </summary>
public static class ModelIdentifier
{
    /// <summary>True for any identifier that delegates model choice to an automatic router:
    /// a path segment named "auto" (e.g. <c>auto/coding:free</c>, <c>openrouter/auto</c>) or the bare word "auto".</summary>
    public static bool IsAutomaticRouter(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return false;
        }

        var trimmed = modelId.Trim();

        return trimmed.Equals("auto", StringComparison.OrdinalIgnoreCase)
            || trimmed.Split('/').Any(segment => segment.Equals("auto", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Null when this (non-empty) identifier may be used; otherwise the fail-closed rejection
    /// reason. Empty/whitespace input returns null: the ABSENCE of a model is a separate policy
    /// (some runtimes resolve their configured DefaultModel), never an identifier defect.
    /// </summary>
    public static string? RejectReason(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return null;
        }

        if (IsAutomaticRouter(modelId))
        {
            return $"model id '{modelId}' is an automatic router id. The Factory requires an explicit "
                + "model; auto/... identifiers are rejected (fail-closed) until a future phase "
                + "explicitly defines and authorizes them.";
        }

        if (!string.Equals(modelId, modelId.Trim(), StringComparison.Ordinal)
            || modelId.Any(char.IsWhiteSpace)
            || modelId.Any(char.IsControl)
            || modelId.StartsWith('/')
            || modelId.EndsWith('/')
            || modelId.Contains("//", StringComparison.Ordinal))
        {
            return $"model id '{modelId}' is malformed (whitespace, control characters or empty path segments are not allowed).";
        }

        return null;
    }

    /// <summary>True when the identifier is an explicit model id the Factory may route with.</summary>
    public static bool IsAcceptable(string? modelId) => RejectReason(modelId) is null;
}
