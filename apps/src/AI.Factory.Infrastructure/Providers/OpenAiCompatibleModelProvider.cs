using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using AI.Factory.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace AI.Factory.Infrastructure.Providers;

/// <summary>
/// Generic HTTP client for any OpenAI Chat Completions-compatible API
/// (OpenRouter, self-hosted gateways, etc — not hard-coded to OpenAI
/// itself). One HTTP call per <see cref="CompleteAsync"/>; retries/circuit
/// breaking are the responsibility of <see cref="ResilienceModelProviderDecorator"/>,
/// which wraps this class.
/// </summary>
public sealed class OpenAiCompatibleModelProvider : IModelProvider
{
    private readonly HttpClient _httpClient;
    private readonly ISecretProvider _secretProvider;
    private readonly string? _apiKeySecretName;
    private readonly ILogger<OpenAiCompatibleModelProvider> _logger;

    public string ProviderId { get; }

    public IReadOnlyCollection<string> SupportedCapabilities { get; }

    public OpenAiCompatibleModelProvider(
        string providerId,
        IEnumerable<string> supportedCapabilities,
        HttpClient httpClient,
        ISecretProvider secretProvider,
        string? apiKeySecretName,
        ILogger<OpenAiCompatibleModelProvider> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ProviderId = providerId;
        SupportedCapabilities = supportedCapabilities.ToList();
        _httpClient = httpClient;
        _secretProvider = secretProvider;
        _apiKeySecretName = apiKeySecretName;
        _logger = logger;
    }

    public async Task<AiCompletionResult> CompleteAsync(
        AiCompletionRequest request,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var modelId = request.PreferredModelId;

        if (string.IsNullOrWhiteSpace(modelId))
        {
            return Failure(
                request, "unknown", AiFailureCategory.InvalidRequest,
                "A resolved model ID is required to call an OpenAI-compatible provider.", stopwatch);
        }

        string? apiKey = null;
        if (!string.IsNullOrWhiteSpace(_apiKeySecretName))
        {
            apiKey = await _secretProvider.GetSecretAsync(_apiKeySecretName, cancellationToken);
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                _logger.LogWarning(
                    "AI provider {ProviderId} is missing its API key secret '{SecretName}'.",
                    ProviderId, _apiKeySecretName);

                return Failure(
                    request, modelId, AiFailureCategory.Authentication,
                    $"API key secret '{_apiKeySecretName}' was not found.", stopwatch);
            }
        }

        var messages = new List<ChatMessage>();
        if (!string.IsNullOrWhiteSpace(request.SystemInstructions))
        {
            messages.Add(new ChatMessage("system", request.SystemInstructions));
        }

        if (!string.IsNullOrWhiteSpace(request.Context))
        {
            messages.Add(new ChatMessage("user", request.Context));
        }

        messages.Add(new ChatMessage("user", request.Prompt));

        var payload = new ChatCompletionRequestDto(
            modelId, messages, request.MaxOutputTokens, request.Temperature);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        {
            Content = JsonContent.Create(payload)
        };

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        HttpResponseMessage response;

        try
        {
            response = await _httpClient.SendAsync(httpRequest, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure(request, modelId, AiFailureCategory.Cancelled, "Operation was cancelled.", stopwatch);
        }
        catch (TaskCanceledException)
        {
            // HttpClient surfaces its own request timeout as TaskCanceledException, not our caller's token.
            return Failure(request, modelId, AiFailureCategory.Timeout, "The request timed out.", stopwatch);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "AI provider {ProviderId} network failure calling model {ModelId}.", ProviderId, modelId);
            return Failure(request, modelId, AiFailureCategory.ProviderFailure, "A network error occurred contacting the provider.", stopwatch);
        }

        using (response)
        {
            stopwatch.Stop();

            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadFromJsonAsync<ChatCompletionResponseDto>(cancellationToken: cancellationToken);
                var content = body?.Choices?.FirstOrDefault()?.Message?.Content;

                return new AiCompletionResult(
                    true, content, ProviderId, modelId, request.CorrelationId, stopwatch.ElapsedMilliseconds,
                    body?.Usage?.PromptTokens, body?.Usage?.CompletionTokens);
            }

            var category = MapStatusCode(response.StatusCode);
            var retryAfter = response.Headers.RetryAfter?.Delta;

            _logger.LogWarning(
                "AI provider {ProviderId} returned {StatusCode} for model {ModelId} (category {FailureCategory}).",
                ProviderId, (int)response.StatusCode, modelId, category);

            return new AiCompletionResult(
                false, null, ProviderId, modelId, request.CorrelationId, stopwatch.ElapsedMilliseconds,
                FailureCategory: category,
                ErrorMessage: $"Provider returned HTTP {(int)response.StatusCode} ({response.StatusCode}).",
                RetryAfter: retryAfter);
        }
    }

    private AiCompletionResult Failure(
        AiCompletionRequest request, string modelId, AiFailureCategory category, string message, Stopwatch stopwatch)
    {
        stopwatch.Stop();
        return new AiCompletionResult(
            false, null, ProviderId, modelId, request.CorrelationId, stopwatch.ElapsedMilliseconds,
            FailureCategory: category, ErrorMessage: message);
    }

    private static AiFailureCategory MapStatusCode(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => AiFailureCategory.Authentication,
        HttpStatusCode.TooManyRequests => AiFailureCategory.RateLimited,
        HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout => AiFailureCategory.Timeout,
        HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity or HttpStatusCode.NotFound => AiFailureCategory.InvalidRequest,
        HttpStatusCode.ServiceUnavailable or HttpStatusCode.BadGateway => AiFailureCategory.Unavailable,
        _ when (int)statusCode >= 500 => AiFailureCategory.ProviderFailure,
        _ => AiFailureCategory.ProviderFailure
    };

    private sealed record ChatMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    private sealed record ChatCompletionRequestDto(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] List<ChatMessage> Messages,
        [property: JsonPropertyName("max_tokens")] int? MaxTokens,
        [property: JsonPropertyName("temperature")] double? Temperature);

    private sealed record ChatCompletionResponseDto(
        [property: JsonPropertyName("choices")] List<ChatChoiceDto>? Choices,
        [property: JsonPropertyName("usage")] ChatUsageDto? Usage);

    private sealed record ChatChoiceDto(
        [property: JsonPropertyName("message")] ChatMessage? Message);

    private sealed record ChatUsageDto(
        [property: JsonPropertyName("prompt_tokens")] int? PromptTokens,
        [property: JsonPropertyName("completion_tokens")] int? CompletionTokens);
}
