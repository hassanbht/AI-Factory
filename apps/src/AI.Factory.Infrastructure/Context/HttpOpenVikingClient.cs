using System.Net.Http.Headers;
using System.Net.Http.Json;
using AI.Factory.Infrastructure.Resilience;
using Microsoft.Extensions.Logging.Abstractions;

namespace AI.Factory.Infrastructure.Context;

public sealed class HttpOpenVikingClient : IOpenVikingClient
{
    private readonly HttpClient _httpClient;
    private readonly OpenVikingOptions _options;
    private readonly RetryExecutor _retryExecutor;

    public HttpOpenVikingClient(HttpClient httpClient, OpenVikingOptions options, RetryExecutor? retryExecutor = null)
    {
        _httpClient = httpClient;
        _options = options;
        // No retry unless explicitly configured — keeps every existing
        // single-request-server test/caller unaffected; Program.cs always
        // supplies the real multi-attempt policy in production.
        _retryExecutor = retryExecutor
            ?? new RetryExecutor(new RetryOptions { MaxAttempts = 1 }, NullLogger<RetryExecutor>.Instance);
    }

    public Task<OpenVikingQueryResponseDto> QueryAsync(
        OpenVikingQueryRequestDto request,
        CancellationToken cancellationToken = default)
    {
        return _retryExecutor.ExecuteAsync(
            "OpenViking.Query",
            ct => SendQueryAsync(request, ct),
            HttpTransientClassifier.IsTransient,
            cancellationToken);
    }

    private async Task<OpenVikingQueryResponseDto> SendQueryAsync(
        OpenVikingQueryRequestDto request,
        CancellationToken cancellationToken)
    {
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "query")
        {
            Content = JsonContent.Create(request)
        };

        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        }

        using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<OpenVikingQueryResponseDto>(
            cancellationToken: cancellationToken);

        return result ?? new OpenVikingQueryResponseDto();
    }
}
