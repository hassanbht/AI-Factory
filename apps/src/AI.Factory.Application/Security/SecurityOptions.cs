namespace AI.Factory.Application.Security;

public sealed class SecurityOptions
{
    public const string SectionName = "Security";

    public OperationalEndpointsOptions OperationalEndpoints { get; set; } = new();

    public RateLimitingOptions RateLimiting { get; set; } = new();

    public RequestLimitsOptions RequestLimits { get; set; } = new();

    public SecurityHeadersOptions Headers { get; set; } = new();

    public CorsSecurityOptions Cors { get; set; } = new();

    public OpenApiSecurityOptions OpenApi { get; set; } = new();

    public HttpsSecurityOptions Https { get; set; } = new();

    public ForwardedHeadersSecurityOptions ForwardedHeaders { get; set; } = new();

    public void Validate()
    {
        if (OperationalEndpoints.RequireAccessKey)
        {
            if (string.IsNullOrWhiteSpace(OperationalEndpoints.AccessKey))
            {
                throw new InvalidOperationException(
                    "Security:OperationalEndpoints:RequireAccessKey is enabled but AccessKey is not configured.");
            }

            if (OperationalEndpoints.AccessKey.Trim().Length < 16)
            {
                throw new InvalidOperationException(
                    "Security:OperationalEndpoints:AccessKey must be at least 16 characters long.");
            }
        }

        if (RequestLimits.MaxRequestBodySizeBytes <= 0)
        {
            throw new InvalidOperationException(
                "Security:RequestLimits:MaxRequestBodySizeBytes must be greater than zero.");
        }

        if (RateLimiting.Enabled)
        {
            ValidatePolicy("General", RateLimiting.General);
            ValidatePolicy("Operational", RateLimiting.Operational);
            ValidatePolicy("Expensive", RateLimiting.Expensive);
        }

        if (Cors.Enabled && Cors.AllowedOrigins is not null)
        {
            foreach (var origin in Cors.AllowedOrigins)
            {
                if (string.IsNullOrWhiteSpace(origin) || !Uri.TryCreate(origin, UriKind.Absolute, out var uri) || !uri.IsWellFormedOriginalString())
                {
                    throw new InvalidOperationException(
                        $"Security:Cors:AllowedOrigins contains an invalid absolute URI: '{origin}'.");
                }
            }
        }
    }

    private static void ValidatePolicy(string name, RateLimitPolicyOptions policy)
    {
        if (policy.PermitLimit <= 0)
        {
            throw new InvalidOperationException(
                $"Security:RateLimiting:{name}:PermitLimit must be greater than zero.");
        }

        if (policy.WindowSeconds <= 0)
        {
            throw new InvalidOperationException(
                $"Security:RateLimiting:{name}:WindowSeconds must be greater than zero.");
        }

        if (policy.QueueLimit < 0)
        {
            throw new InvalidOperationException(
                $"Security:RateLimiting:{name}:QueueLimit cannot be negative.");
        }
    }
}

public sealed class OperationalEndpointsOptions
{
    public bool RequireAccessKey { get; set; } = false;

    public string AccessKey { get; set; } = string.Empty;

    public string HeaderName { get; set; } = "X-Operations-Key";
}

public sealed class RateLimitingOptions
{
    public bool Enabled { get; set; } = true;

    public RateLimitPolicyOptions General { get; set; } = new() { PermitLimit = 100, WindowSeconds = 60 };

    public RateLimitPolicyOptions Operational { get; set; } = new() { PermitLimit = 50, WindowSeconds = 60 };

    public RateLimitPolicyOptions Expensive { get; set; } = new() { PermitLimit = 10, WindowSeconds = 60 };
}

public sealed class RateLimitPolicyOptions
{
    public int PermitLimit { get; set; } = 100;

    public int WindowSeconds { get; set; } = 60;

    public int QueueLimit { get; set; } = 0;
}

public sealed class RequestLimitsOptions
{
    public long MaxRequestBodySizeBytes { get; set; } = 256 * 1024; // 256 KB
}

public sealed class SecurityHeadersOptions
{
    public bool EnableContentSecurityPolicy { get; set; } = true;

    public string ContentSecurityPolicy { get; set; } = "default-src 'none'; frame-ancestors 'none'";

    public string FrameOptions { get; set; } = "DENY";

    public string ReferrerPolicy { get; set; } = "no-referrer";

    public string PermissionsPolicy { get; set; } =
        "accelerometer=(), camera=(), geolocation=(), gyroscope=(), magnetometer=(), microphone=(), payment=(), usb=()";
}

public sealed class CorsSecurityOptions
{
    public bool Enabled { get; set; } = false;

    public string[] AllowedOrigins { get; set; } = Array.Empty<string>();
}

public sealed class OpenApiSecurityOptions
{
    public bool? Enabled { get; set; }
}

public sealed class HttpsSecurityOptions
{
    public bool RedirectHttpToHttps { get; set; } = false;

    public bool EnableHstsInProduction { get; set; } = true;
}

public sealed class ForwardedHeadersSecurityOptions
{
    public bool Enabled { get; set; } = false;
}
