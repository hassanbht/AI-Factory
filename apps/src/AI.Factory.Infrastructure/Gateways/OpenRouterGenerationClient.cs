using AI.Factory.Application.Abstractions;
using AI.Factory.Application.CostAccounting;
using Microsoft.Extensions.Logging;

namespace AI.Factory.Infrastructure.Gateways;

/// <summary>
/// Phase Q.3 — authoritative OpenRouter generation lookup over HTTP.
/// Lives in Infrastructure (Application owns no HTTP). Sends only the
/// generation id lookup (no prompts/completions); redacts auth in diagnostics.
/// An injected <see cref="HttpClient"/> (mocked in tests) performs the call;
/// production wiring stays disabled until the Q.4 canary is separately authorized.
/// </summary>
public sealed class OpenRouterGenerationClient : IOpenRouterGenerationClient
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly Func<string?> _apiKey;
    private readonly ILogger<OpenRouterGenerationClient> _logger;

    public OpenRouterGenerationClient(
        HttpClient http,
        string baseUrl,
        Func<string?> apiKey,
        ILogger<OpenRouterGenerationClient> logger)
    {
        _http = http;
        _baseUrl = string.IsNullOrWhiteSpace(baseUrl) ? OpenRouterGateway.DefaultBaseUrl : baseUrl.TrimEnd('/');
        _apiKey = apiKey;
        _logger = logger;
    }

    public async Task<OpenRouterGenerationLookupResult> GetGenerationAsync(
        string generationId,
        CancellationToken cancellationToken = default)
    {
        if (!OpenRouterGenerationCapture.IsWellFormedGenerationId(generationId))
        {
            return new OpenRouterGenerationLookupResult(generationId, 0, null, false);
        }

        var key = _apiKey();
        if (string.IsNullOrWhiteSpace(key))
        {
            _logger.LogWarning("OpenRouter generation lookup refused: no API key resolved.");
            return new OpenRouterGenerationLookupResult(generationId, 0, null, false);
        }

        var url = _baseUrl + "/generation?id=" + Uri.EscapeDataString(generationId);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer [REDACTED]");

        HttpResponseMessage response;
        try
        {
            using var live = new HttpRequestMessage(HttpMethod.Get, url);
            live.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
            foreach (var header in request.Headers.Where(h => h.Key != "Authorization"))
            {
                live.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            response = await _http.SendAsync(live, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("OpenRouter generation lookup transport failed ({ExceptionType}).", ex.GetType().Name);
            return new OpenRouterGenerationLookupResult(generationId, 0, null, true);
        }

        using (response)
        {
            string? body = null;
            try
            {
                body = await response.Content.ReadAsStringAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("OpenRouter generation lookup body read failed ({ExceptionType}).", ex.GetType().Name);
            }

            if (body is not null && body.Length > 64 * 1024)
            {
                body = body[..(64 * 1024)];
            }

            return new OpenRouterGenerationLookupResult(generationId, (int)response.StatusCode, body, true);
        }
    }
}
