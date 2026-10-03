using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using AI.Factory.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace AI.Factory.Infrastructure.Agents;

/// <summary>
/// Phase 18.29.1 — <see cref="IProviderModelCatalog"/> over the OpenAI-compatible
/// <c>GET {BaseUrl}/models</c> endpoint, which OpenRouter and every other
/// OpenAI-compatible gateway in this repository's provider catalog implement.
///
/// No credential is used or required: the model catalog is public on OpenRouter,
/// and keeping this call unauthenticated means the preflight cannot be the reason
/// a missing secret surfaces as a confusing "model invalid" error. Any transport
/// or protocol problem is reported as an OUTAGE, never as an invalid model id —
/// this class is not allowed to make a configuration look broken because a network
/// was down.
/// </summary>
public sealed class OpenAiCompatibleModelCatalog : IProviderModelCatalog
{
    /// <summary>Explicit rather than relying on the ambient default, so a future framework default change cannot silently break id matching.</summary>
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// A factory, not a client: this type is registered as a singleton, and holding
    /// one pooled <c>HttpClient</c> for the process lifetime is the documented way
    /// to end up with stale DNS. The client is created per call instead — this runs
    /// once at startup, so there is nothing to optimise away. Infrastructure takes
    /// the delegate rather than <c>IHttpClientFactory</c> itself because this
    /// project intentionally does not reference Microsoft.Extensions.Http; the
    /// composition roots own that.
    /// </summary>
    private readonly Func<HttpClient> _httpClientFactory;

    private readonly ILogger<OpenAiCompatibleModelCatalog> _logger;
    private readonly string _baseUrl;

    public OpenAiCompatibleModelCatalog(
        string provider,
        string baseUrl,
        Func<HttpClient> httpClientFactory,
        ILogger<OpenAiCompatibleModelCatalog> logger)
    {
        Provider = provider;
        _baseUrl = baseUrl.TrimEnd('/');
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public string Provider { get; }

    public async Task<ProviderModelCatalogResult> GetAvailableModelIdsAsync(
        CancellationToken cancellationToken = default)
    {
        var requestUri = $"{_baseUrl}/models";

        try
        {
            var client = _httpClientFactory();

            using var response = await client.GetAsync(requestUri, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return ProviderModelCatalogResult.Unavailable(
                    $"GET {requestUri} returned HTTP {(int)response.StatusCode}.");
            }

            var payload = await response.Content.ReadFromJsonAsync<ModelsResponse>(
                SerializerOptions, cancellationToken);

            if (payload?.Data is null)
            {
                return ProviderModelCatalogResult.Unavailable(
                    $"GET {requestUri} returned a body with no 'data' array.");
            }

            var ids = payload.Data
                .Select(m => m.Id)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id!)
                .ToList();

            if (ids.Count == 0)
            {
                // An empty catalog is indistinguishable from a broken one, and
                // treating it as authoritative would mark every configured model
                // unservable and refuse to start over a provider-side glitch.
                return ProviderModelCatalogResult.Unavailable(
                    $"GET {requestUri} returned an empty model list.");
            }

            _logger.LogInformation(
                "ProviderModelCatalogLoaded Provider={Provider} ModelCount={ModelCount}", Provider, ids.Count);

            return ProviderModelCatalogResult.Available(ids);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            _logger.LogWarning(
                ex, "ProviderModelCatalogUnavailable Provider={Provider} Uri={Uri}", Provider, requestUri);

            return ProviderModelCatalogResult.Unavailable($"GET {requestUri} failed: {ex.Message}");
        }
    }

    private sealed record ModelsResponse(IReadOnlyList<ModelEntry>? Data);

    private sealed record ModelEntry(string? Id);
}
