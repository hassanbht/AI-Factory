using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AI.Factory.Infrastructure.Agents.OpenHands;

public sealed class OpenHandsClient : IOpenHandsClient
{
    private const string SessionApiKeyHeader = "X-Session-API-Key";
    private readonly HttpClient _httpClient;
    private readonly ILogger<OpenHandsClient> _logger;

    public OpenHandsClient(
        HttpClient httpClient,
        string? sessionApiKey,
        ILogger<OpenHandsClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;

        if (!string.IsNullOrWhiteSpace(sessionApiKey))
        {
            _httpClient.DefaultRequestHeaders.Remove(SessionApiKeyHeader);
            _httpClient.DefaultRequestHeaders.Add(SessionApiKeyHeader, sessionApiKey);
        }
    }

    public async Task<bool> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _httpClient.GetAsync("/api/agent-profiles", cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "OpenHands health check probe failed.");
            return false;
        }
    }

    public async Task<OpenHandsConversationInfo> CreateConversationAsync(
        OpenHandsCreateConversationRequest request,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Creating OpenHands conversation for workspace {WorkingDir}.", request.Workspace.WorkingDir);

        using var response = await _httpClient.PostAsJsonAsync("/api/conversations", request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("Failed to create OpenHands conversation. Status: {StatusCode}, Body: {ErrorBody}",
                response.StatusCode, errorBody);
            throw new HttpRequestException($"OpenHands conversation creation failed: {response.StatusCode} {errorBody}");
        }

        var result = await response.Content.ReadFromJsonAsync<OpenHandsConversationInfo>(cancellationToken: cancellationToken);
        return result ?? throw new InvalidOperationException("OpenHands returned null conversation info.");
    }

    public async Task<bool> SendMessageAsync(
        string conversationId,
        string message,
        bool run = true,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Sending task message to OpenHands conversation {ConversationId} (run={Run}).", conversationId, run);

        var payload = new OpenHandsSendMessageRequest
        {
            Role = "user",
            Content = new List<OpenHandsContentDto>
            {
                new() { Type = "text", Text = message }
            },
            Run = run
        };

        using var response = await _httpClient.PostAsJsonAsync($"/api/conversations/{conversationId}/events", payload, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("Failed to send message to OpenHands conversation {ConversationId}. Status: {StatusCode}, Body: {ErrorBody}",
                conversationId, response.StatusCode, errorBody);
            throw new HttpRequestException($"OpenHands message submission failed: {response.StatusCode} {errorBody}");
        }

        var result = await response.Content.ReadFromJsonAsync<OpenHandsSuccessResponse>(cancellationToken: cancellationToken);
        return result?.Success ?? true;
    }

    public async Task<OpenHandsConversationInfo> GetConversationAsync(
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync($"/api/conversations/{conversationId}", cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"OpenHands get conversation failed: {response.StatusCode} {errorBody}");
        }

        var result = await response.Content.ReadFromJsonAsync<OpenHandsConversationInfo>(cancellationToken: cancellationToken);
        return result ?? throw new InvalidOperationException("OpenHands returned null conversation info.");
    }

    public async Task<string?> GetFinalResponseAsync(
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _httpClient.GetAsync($"/api/conversations/{conversationId}/agent_final_response", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var result = await response.Content.ReadFromJsonAsync<OpenHandsAgentResponseResult>(cancellationToken: cancellationToken);
            return result?.Response;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get final response for conversation {ConversationId}.", conversationId);
            return null;
        }
    }

    public async Task<bool> InterruptAsync(
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _httpClient.PostAsync($"/api/conversations/{conversationId}/interrupt", null, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to interrupt OpenHands conversation {ConversationId}.", conversationId);
            return false;
        }
    }

    public async Task<bool> DeleteConversationAsync(
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _httpClient.DeleteAsync($"/api/conversations/{conversationId}", cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete OpenHands conversation {ConversationId}.", conversationId);
            return false;
        }
    }
}

public sealed class DefaultOpenHandsClientFactory : IOpenHandsClientFactory
{
    private readonly HttpClient? _sharedHttpClient;
    private readonly ILoggerFactory _loggerFactory;

    public DefaultOpenHandsClientFactory(
        HttpClient? sharedHttpClient = null,
        ILoggerFactory? loggerFactory = null)
    {
        _sharedHttpClient = sharedHttpClient;
        _loggerFactory = loggerFactory ?? LoggerFactory.Create(b => { });
    }

    public IOpenHandsClient CreateClient(string serverUrl, string? sessionApiKey)
    {
        var client = _sharedHttpClient ?? new HttpClient();
        if (_sharedHttpClient is null)
        {
            client.BaseAddress = new Uri(serverUrl);
        }

        return new OpenHandsClient(
            client,
            sessionApiKey,
            _loggerFactory.CreateLogger<OpenHandsClient>());
    }
}
