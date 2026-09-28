using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Abstractions.Gateway;
using Microsoft.Extensions.Logging;

namespace AI.Factory.Infrastructure.Gateways;

/// <summary>
/// Gateway implementation for OpenRouter (https://openrouter.ai/api/v1).
/// Owns OpenRouter-specific endpoint conventions, headers, formatting, error mapping, and usage telemetry.
/// Never leaks secrets into DTOs, logs, or persistent state.
/// </summary>
public sealed class OpenRouterGateway : IModelGateway
{
    public const string Key = "OpenRouter";
    public const string DefaultBaseUrl = "https://openrouter.ai/api/v1";
    public const string DefaultApiKeySecretName = "OPENROUTER_API_KEY";

    private readonly HttpClient _httpClient;
    private readonly ISecretProvider _secretProvider;
    private readonly ILogger<OpenRouterGateway> _logger;
    private readonly string _baseUrl;
    private readonly string _apiKeySecretName;

    public string GatewayKey => Key;

    public ModelGatewayCapabilities Capabilities =>
        ModelGatewayCapabilities.Chat
        | ModelGatewayCapabilities.Streaming
        | ModelGatewayCapabilities.ToolCalling
        | ModelGatewayCapabilities.StructuredOutput;

    public OpenRouterGateway(
        HttpClient httpClient,
        ISecretProvider secretProvider,
        ILogger<OpenRouterGateway> logger,
        string? baseUrl = null,
        string? apiKeySecretName = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _secretProvider = secretProvider ?? throw new ArgumentNullException(nameof(secretProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _baseUrl = !string.IsNullOrWhiteSpace(baseUrl) ? baseUrl.TrimEnd('/') : DefaultBaseUrl;
        _apiKeySecretName = !string.IsNullOrWhiteSpace(apiKeySecretName) ? apiKeySecretName : DefaultApiKeySecretName;
    }

    public string FormatModelIdentifier(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return string.Empty;
        }

        return modelId.StartsWith("openrouter/", StringComparison.OrdinalIgnoreCase)
            ? modelId
            : $"openrouter/{modelId}";
    }

    public ModelGatewayTransportInfo GetTransportInfo() =>
        new(
            BaseUrl: _baseUrl,
            ApiKeySecretName: _apiKeySecretName,
            EnvironmentVariableName: "OPENROUTER_API_KEY",
            CliProviderName: "openrouter");

    public async Task<ModelGatewayResponse> ExecuteAsync(
        ModelGatewayRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var stopwatch = Stopwatch.StartNew();
        var modelId = request.ModelId;

        if (string.IsNullOrWhiteSpace(modelId))
        {
            stopwatch.Stop();
            return ModelGatewayResponse.Failed(
                GatewayKey,
                "unknown",
                new ModelGatewayFailure(ModelGatewayFailureReason.InvalidRequest, "ModelId is required for model gateway execution."),
                stopwatch.ElapsedMilliseconds);
        }

        string? apiKey = await _secretProvider.GetSecretAsync(_apiKeySecretName, cancellationToken);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            stopwatch.Stop();
            _logger.LogWarning("OpenRouter API key secret '{SecretName}' was not found.", _apiKeySecretName);
            return ModelGatewayResponse.Failed(
                GatewayKey,
                modelId,
                new ModelGatewayFailure(ModelGatewayFailureReason.Authentication, $"API key secret '{_apiKeySecretName}' was not found."),
                stopwatch.ElapsedMilliseconds);
        }

        var messages = new List<ChatDtoMessage>();
        if (!string.IsNullOrWhiteSpace(request.SystemInstructions))
        {
            messages.Add(new ChatDtoMessage("system", request.SystemInstructions));
        }

        foreach (var msg in request.Messages)
        {
            messages.Add(new ChatDtoMessage(msg.Role, msg.Content));
        }

        // Clean modelId for direct OpenRouter API (strip openrouter/ prefix if present)
        var apiModelId = modelId.StartsWith("openrouter/", StringComparison.OrdinalIgnoreCase)
            ? modelId.Substring("openrouter/".Length)
            : modelId;

        var payload = new ChatCompletionRequestDto(
            apiModelId,
            messages,
            request.MaxTokens,
            request.Temperature);

        var requestUri = $"{_baseUrl}/chat/completions";
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = JsonContent.Create(payload)
        };

        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        httpRequest.Headers.TryAddWithoutValidation("HTTP-Referer", "https://ai-factory.local");
        httpRequest.Headers.TryAddWithoutValidation("X-Title", "AI Factory");

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(httpRequest, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            _logger.LogInformation("OpenRouter request cancelled for model {ModelId}.", modelId);
            return ModelGatewayResponse.Failed(
                GatewayKey,
                modelId,
                new ModelGatewayFailure(ModelGatewayFailureReason.Cancellation, "The request was cancelled."),
                stopwatch.ElapsedMilliseconds);
        }
        catch (TaskCanceledException)
        {
            stopwatch.Stop();
            _logger.LogWarning("OpenRouter request timed out for model {ModelId}.", modelId);
            return ModelGatewayResponse.Failed(
                GatewayKey,
                modelId,
                new ModelGatewayFailure(ModelGatewayFailureReason.Timeout, "The request to OpenRouter timed out."),
                stopwatch.ElapsedMilliseconds);
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();
            _logger.LogWarning(ex, "OpenRouter network failure for model {ModelId}.", modelId);
            return ModelGatewayResponse.Failed(
                GatewayKey,
                modelId,
                new ModelGatewayFailure(ModelGatewayFailureReason.TransportFailure, $"Network error contacting OpenRouter: {ex.Message}"),
                stopwatch.ElapsedMilliseconds);
        }

        using (response)
        {
            stopwatch.Stop();

            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadFromJsonAsync<ChatCompletionResponseDto>(cancellationToken: cancellationToken);
                var choice = body?.Choices?.FirstOrDefault();
                var content = choice?.Message?.Content;
                var finishReason = choice?.FinishReason;

                ModelGatewayUsage? usage = null;
                if (body?.Usage is not null)
                {
                    usage = new ModelGatewayUsage(
                        InputTokens: body.Usage.PromptTokens,
                        OutputTokens: body.Usage.CompletionTokens,
                        TotalTokens: body.Usage.TotalTokens ?? (body.Usage.PromptTokens + body.Usage.CompletionTokens));
                }

                _logger.LogInformation(
                    "OpenRouter request succeeded for model {ModelId} DurationMs={DurationMs} TotalTokens={TotalTokens}",
                    modelId, stopwatch.ElapsedMilliseconds, usage?.TotalTokens);

                return ModelGatewayResponse.Successful(
                    gatewayKey: GatewayKey,
                    modelId: modelId,
                    content: content,
                    finishReason: finishReason,
                    usage: usage,
                    requestId: body?.Id,
                    durationMs: stopwatch.ElapsedMilliseconds);
            }

            var statusCode = (int)response.StatusCode;
            var failureReason = MapStatusCode(response.StatusCode);
            var retryAfter = response.Headers.RetryAfter?.Delta;
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);

            _logger.LogWarning(
                "OpenRouter returned HTTP {StatusCode} ({FailureReason}) for model {ModelId}.",
                statusCode, failureReason, modelId);

            return ModelGatewayResponse.Failed(
                gatewayKey: GatewayKey,
                modelId: modelId,
                failure: new ModelGatewayFailure(
                    Reason: failureReason,
                    Message: $"OpenRouter returned HTTP {statusCode}: {errorBody}",
                    HttpStatusCode: statusCode,
                    RetryAfter: retryAfter),
                durationMs: stopwatch.ElapsedMilliseconds,
                requestId: response.Headers.TryGetValues("x-request-id", out var ids) ? ids.FirstOrDefault() : null);
        }
    }

    private static ModelGatewayFailureReason MapStatusCode(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.Unauthorized => ModelGatewayFailureReason.Authentication,
        HttpStatusCode.PaymentRequired => ModelGatewayFailureReason.Billing,
        HttpStatusCode.Forbidden => ModelGatewayFailureReason.Authorization,
        HttpStatusCode.NotFound => ModelGatewayFailureReason.ModelUnavailable,
        HttpStatusCode.TooManyRequests => ModelGatewayFailureReason.RateLimited,
        HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout => ModelGatewayFailureReason.Timeout,
        HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity => ModelGatewayFailureReason.InvalidRequest,
        HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable => ModelGatewayFailureReason.GatewayUnavailable,
        _ when (int)statusCode >= 500 => ModelGatewayFailureReason.ProviderUnavailable,
        _ => ModelGatewayFailureReason.Unknown
    };

    private sealed record ChatDtoMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    private sealed record ChatCompletionRequestDto(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] List<ChatDtoMessage> Messages,
        [property: JsonPropertyName("max_tokens")] [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? MaxTokens,
        [property: JsonPropertyName("temperature")] [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Temperature);

    private sealed record ChatCompletionResponseDto(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("choices")] List<ChatChoiceDto>? Choices,
        [property: JsonPropertyName("usage")] ChatUsageDto? Usage);

    private sealed record ChatChoiceDto(
        [property: JsonPropertyName("message")] ChatDtoMessage? Message,
        [property: JsonPropertyName("finish_reason")] string? FinishReason);

    private sealed record ChatUsageDto(
        [property: JsonPropertyName("prompt_tokens")] int? PromptTokens,
        [property: JsonPropertyName("completion_tokens")] int? CompletionTokens,
        [property: JsonPropertyName("total_tokens")] int? TotalTokens);
}
