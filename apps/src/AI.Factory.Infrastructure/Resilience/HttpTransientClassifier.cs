using System.Net;

namespace AI.Factory.Infrastructure.Resilience;

/// <summary>
/// Classifies an exception from an HTTP call as retry-worthy. Deliberately
/// narrow: only the 5 documented transient status codes, a connection-level
/// failure (no status code at all — DNS/refused/reset), or a client-side
/// request timeout. Everything else — including 401/403 (auth), 400/422
/// (validation/invalid payload), and any other 4xx (business rule failure)
/// — is never retried, since retrying those can only repeat the same
/// failure or, worse, hammer a server that already rejected the request.
/// </summary>
public static class HttpTransientClassifier
{
    private static readonly HttpStatusCode[] TransientStatusCodes =
    {
        (HttpStatusCode)429, // TooManyRequests
        HttpStatusCode.InternalServerError,
        HttpStatusCode.BadGateway,
        HttpStatusCode.ServiceUnavailable,
        HttpStatusCode.GatewayTimeout
    };

    public static bool IsTransient(Exception ex)
    {
        return ex switch
        {
            HttpRequestException { StatusCode: not null } httpEx => TransientStatusCodes.Contains(httpEx.StatusCode.Value),
            HttpRequestException { StatusCode: null } => true, // no response reached the client at all
            TimeoutException => true,
            TaskCanceledException => true, // HttpClient's own request-timeout signal, not caller cancellation (filtered separately by RetryExecutor)
            _ => false
        };
    }
}
