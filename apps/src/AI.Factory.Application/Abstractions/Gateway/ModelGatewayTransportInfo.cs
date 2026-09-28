namespace AI.Factory.Application.Abstractions.Gateway;

/// <summary>
/// Transport parameters for connecting execution adapters or containers to a model gateway.
/// NEVER contains plaintext credentials — only configuration keys or secret names.
/// </summary>
public sealed record ModelGatewayTransportInfo(
    string BaseUrl,
    string? ApiKeySecretName,
    string? EnvironmentVariableName,
    string? CliProviderName);
