using System.Net.Http.Headers;
using System.Net.Http.Json;
using AI.Factory.Application.Abstractions;
using AI.Factory.Infrastructure.Resilience;
using Microsoft.Extensions.Logging.Abstractions;

namespace AI.Factory.Infrastructure.Context;

public sealed class HttpOpenVikingClient : IOpenVikingClient
{
    private readonly HttpClient _httpClient;
    private readonly OpenVikingOptions _options;
    private readonly ISecretProvider? _secretProvider;
    private readonly RetryExecutor _retryExecutor;

    public HttpOpenVikingClient(
        HttpClient httpClient,
        OpenVikingOptions options,
        RetryExecutor? retryExecutor = null)
        : this(httpClient, options, null, retryExecutor)
    {
    }

    public HttpOpenVikingClient(
        HttpClient httpClient,
        OpenVikingOptions options,
        ISecretProvider? secretProvider,
        RetryExecutor? retryExecutor = null)
    {
        _httpClient = httpClient;
        _options = options;
        _secretProvider = secretProvider;
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

        string? apiKey = _options.ApiKey;
        if (!string.IsNullOrWhiteSpace(_options.ApiKeySecretName) && _secretProvider is not null)
        {
            apiKey = await _secretProvider.GetSecretAsync(_options.ApiKeySecretName, cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<OpenVikingQueryResponseDto>(
            cancellationToken: cancellationToken);

        return result ?? new OpenVikingQueryResponseDto();
    }
}
