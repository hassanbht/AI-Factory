using System.Text.RegularExpressions;

namespace AI.Factory.Application.Security;

/// <summary>
/// Centralized sanitization and redaction utility. Prevents secrets, authorization
/// tokens, connection strings, and internal local filesystem paths from leaking
/// into HTTP responses, audit logs, or public diagnostics.
/// </summary>
public static class SensitiveDataSanitizer
{
    private const string RedactedPlaceholder = "[REDACTED]";
    private const string RedactedPathPlaceholder = "[REDACTED_PATH]";

    private static readonly string[] SecretKeyFragments =
    {
        "key", "token", "secret", "password", "credential", "authorization", "connectionstring"
    };

    // Regex for Bearer tokens in headers/strings
    private static readonly Regex BearerTokenRegex = new(
        @"(Bearer\s+)[A-Za-z0-9_\-\.]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Regex for key-value secrets (e.g., apiKey=xyz, secret: "abc", key sk-xyz)
    private static readonly Regex KeyValueSecretRegex = new(
        @"(api[-_]?key|password|token|secret|credential|access[-_]?key)\s*([:=]|\s+)\s*[""']?([^""'\s,;]+)[""']?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Regex for raw API key tokens (e.g., sk-or-v1-..., sk-ant-..., sk-proj-...)
    private static readonly Regex RawApiKeyRegex = new(
        @"\b(sk-[a-zA-Z0-9_\-]{16,})\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Regex for Connection string parameters
    private static readonly Regex ConnectionStringRegex = new(
        @"(Data\s*Source|Server|User\s*ID|Password)=([^;]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Regex for Windows absolute paths (e.g., C:\foo\bar)
    private static readonly Regex WindowsPathRegex = new(
        @"[a-zA-Z]:\\(?:[^\\/:*?""<>|\r\n\s]+\\)*[^\\/:*?""<>|\r\n\s]*",
        RegexOptions.Compiled);

    // Regex for Unix absolute paths (/home/..., /etc/..., /Users/..., /tmp/...)
    private static readonly Regex UnixPathRegex = new(
        @"/(?:home|etc|var|tmp|root|Users|projects)/[^\s""',;]+",
        RegexOptions.Compiled);

    /// <summary>
    /// Checks whether a configuration or metadata key looks like it holds sensitive credentials.
    /// </summary>
    public static bool IsSensitiveKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        return SecretKeyFragments.Any(f => key.Contains(f, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Sanitizes free-form text by redacting bearer tokens, secret key-value pairs,
    /// raw API keys, connection strings, and local file system paths.
    /// </summary>
    public static string Sanitize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text ?? string.Empty;
        }

        var result = BearerTokenRegex.Replace(text, $"$1{RedactedPlaceholder}");
        result = KeyValueSecretRegex.Replace(result, $"$1$2{RedactedPlaceholder}");
        result = RawApiKeyRegex.Replace(result, RedactedPlaceholder);
        result = ConnectionStringRegex.Replace(result, $"$1={RedactedPlaceholder}");
        result = WindowsPathRegex.Replace(result, RedactedPathPlaceholder);
        result = UnixPathRegex.Replace(result, RedactedPathPlaceholder);

        return result;
    }

    /// <summary>
    /// Sanitizes a metadata dictionary by replacing sensitive values with [REDACTED].
    /// </summary>
    public static Dictionary<string, string> SanitizeMetadata(IReadOnlyDictionary<string, string>? metadata)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (metadata is null || metadata.Count == 0)
        {
            return result;
        }

        foreach (var (key, value) in metadata)
        {
            if (IsSensitiveKey(key))
            {
                result[key] = RedactedPlaceholder;
            }
            else
            {
                result[key] = Sanitize(value);
            }
        }

        return result;
    }
}
