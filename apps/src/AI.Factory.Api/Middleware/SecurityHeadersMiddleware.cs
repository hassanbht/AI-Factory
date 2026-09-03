using AI.Factory.Application.Security;

namespace AI.Factory.Api.Middleware;

/// <summary>
/// Applies production-ready HTTP security headers to all responses and removes
/// information-disclosing server banner headers.
/// </summary>
public sealed class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;
    private readonly SecurityOptions _options;

    public SecurityHeadersMiddleware(RequestDelegate next, SecurityOptions options)
    {
        _next = next;
        _options = options;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;

            if (!headers.ContainsKey("X-Content-Type-Options"))
            {
                headers["X-Content-Type-Options"] = "nosniff";
            }

            if (!headers.ContainsKey("X-Frame-Options") && !string.IsNullOrWhiteSpace(_options.Headers.FrameOptions))
            {
                headers["X-Frame-Options"] = _options.Headers.FrameOptions;
            }

            if (!headers.ContainsKey("Referrer-Policy") && !string.IsNullOrWhiteSpace(_options.Headers.ReferrerPolicy))
            {
                headers["Referrer-Policy"] = _options.Headers.ReferrerPolicy;
            }

            if (!headers.ContainsKey("Permissions-Policy") && !string.IsNullOrWhiteSpace(_options.Headers.PermissionsPolicy))
            {
                headers["Permissions-Policy"] = _options.Headers.PermissionsPolicy;
            }

            if (_options.Headers.EnableContentSecurityPolicy &&
                !headers.ContainsKey("Content-Security-Policy") &&
                !string.IsNullOrWhiteSpace(_options.Headers.ContentSecurityPolicy))
            {
                // Do not apply strict API CSP to OpenAPI / Swagger UI endpoints so interactive UI works
                var path = context.Request.Path.Value ?? string.Empty;
                var isOpenApi = path.StartsWith("/openapi", StringComparison.OrdinalIgnoreCase) ||
                                path.StartsWith("/swagger", StringComparison.OrdinalIgnoreCase);

                if (!isOpenApi)
                {
                    headers["Content-Security-Policy"] = _options.Headers.ContentSecurityPolicy;
                }
            }

            // Remove server identity information
            headers.Remove("Server");
            headers.Remove("X-Powered-By");

            return Task.CompletedTask;
        });

        await _next(context);
    }
}
