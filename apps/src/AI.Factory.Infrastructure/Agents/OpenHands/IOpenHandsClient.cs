namespace AI.Factory.Infrastructure.Agents.OpenHands;

public interface IOpenHandsClient
{
    Task<bool> CheckHealthAsync(CancellationToken cancellationToken = default);

    Task<OpenHandsConversationInfo> CreateConversationAsync(
        OpenHandsCreateConversationRequest request,
        CancellationToken cancellationToken = default);

    Task<bool> SendMessageAsync(
        string conversationId,
        string message,
        bool run = true,
        CancellationToken cancellationToken = default);

    Task<OpenHandsConversationInfo> GetConversationAsync(
        string conversationId,
        CancellationToken cancellationToken = default);

    Task<string?> GetFinalResponseAsync(
        string conversationId,
        CancellationToken cancellationToken = default);

    Task<bool> InterruptAsync(
        string conversationId,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteConversationAsync(
        string conversationId,
        CancellationToken cancellationToken = default);
}

public interface IOpenHandsClientFactory
{
    IOpenHandsClient CreateClient(string serverUrl, string? sessionApiKey);
}
