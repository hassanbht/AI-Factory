using System.Globalization;
using System.Text.RegularExpressions;
using AI.Factory.Application.Abstractions.Gateway;
using AI.Factory.Application.Security;

namespace AI.Factory.Application.Gateway;

/// <summary>
/// Phase J — the single HTTP-status to <see cref="ModelGatewayFailureReason"/> mapping.
/// Used by the gateway implementations (status observed directly on the HTTP response)
/// and by execution adapters (status the runtime reports in its own failure text, e.g.
/// Hermes' <c>"Billing or credits exhausted: HTTP 402: ..."</c>). Never guesses: text
/// without an explicit HTTP status yields no gateway reason.
/// </summary>
public static partial class ModelGatewayFailureClassifier
{
    /// <summary>Upper bound for an upstream error message kept for diagnosis.</summary>
    public const int MaxUpstreamMessageLength = 500;

    public static ModelGatewayFailureReason FromHttpStatus(int statusCode) => statusCode switch
    {
        401 => ModelGatewayFailureReason.Authentication,
        402 => ModelGatewayFailureReason.Billing,
        403 => ModelGatewayFailureReason.Authorization,
        404 => ModelGatewayFailureReason.ModelUnavailable,
        429 => ModelGatewayFailureReason.RateLimited,
        408 or 504 => ModelGatewayFailureReason.Timeout,
        400 or 422 => ModelGatewayFailureReason.InvalidRequest,
        502 or 503 => ModelGatewayFailureReason.GatewayUnavailable,
        >= 500 => ModelGatewayFailureReason.ProviderUnavailable,
        _ => ModelGatewayFailureReason.Unknown
    };

    /// <summary>
    /// Gateway failure reason from a runtime's failure text, or null when the text carries no
    /// explicit <c>HTTP nnn</c> error status (the reason is then NOT AVAILABLE, never inferred).
    /// The last such status wins: runtimes print their terminal failure summary last.
    /// </summary>
    public static ModelGatewayFailureReason? FromRuntimeMessage(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return null;
        }

        var matches = HttpStatusPattern().Matches(message);
        if (matches.Count == 0)
        {
            return null;
        }

        var status = int.Parse(matches[^1].Groups[1].Value, CultureInfo.InvariantCulture);
        return status >= 400 ? FromHttpStatus(status) : null;
    }

    /// <summary>
    /// Provider error bodies are retained for diagnosis, but never raw: secrets and local
    /// paths are redacted (<see cref="SensitiveDataSanitizer"/>) and the size is bounded.
    /// </summary>
    /// <param name="keepTail">
    /// Keep the end instead of the start — for runtime output whose failure summary comes last
    /// (Hermes prints container start-up lines before its final response).
    /// </param>
    public static string SanitizeUpstreamMessage(string? message, bool keepTail = false)
    {
        var sanitized = SensitiveDataSanitizer.Sanitize(message).Trim();

        if (sanitized.Length <= MaxUpstreamMessageLength)
        {
            return sanitized;
        }

        return keepTail
            ? "[truncated]..." + sanitized[^MaxUpstreamMessageLength..]
            : sanitized[..MaxUpstreamMessageLength] + "...[truncated]";
    }

    [GeneratedRegex(@"\bHTTP ([0-9]{3})\b")]
    private static partial Regex HttpStatusPattern();
}
