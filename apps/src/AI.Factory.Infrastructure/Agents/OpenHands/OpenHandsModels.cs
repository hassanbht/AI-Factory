using System.Text.Json.Serialization;

namespace AI.Factory.Infrastructure.Agents.OpenHands;

public sealed class OpenHandsCreateConversationRequest
{
    [JsonPropertyName("workspace")]
    public OpenHandsWorkspaceDto Workspace { get; set; } = new();

    [JsonPropertyName("agent_settings")]
    public OpenHandsAgentSettingsDto AgentSettings { get; set; } = new();
}

public sealed class OpenHandsWorkspaceDto
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "LocalWorkspace";

    [JsonPropertyName("working_dir")]
    public string WorkingDir { get; set; } = "/workspace";
}

public sealed class OpenHandsAgentSettingsDto
{
    [JsonPropertyName("agent_kind")]
    public string AgentKind { get; set; } = "openhands";

    [JsonPropertyName("agent")]
    public string Agent { get; set; } = "CodeActAgent";

    [JsonPropertyName("llm")]
    public OpenHandsLlmDto Llm { get; set; } = new();
}

public sealed class OpenHandsLlmDto
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("api_key")]
    public string? ApiKey { get; set; }

    [JsonPropertyName("base_url")]
    public string? BaseUrl { get; set; }
}

public sealed class OpenHandsSendMessageRequest
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "user";

    [JsonPropertyName("content")]
    public List<OpenHandsContentDto> Content { get; set; } = new();

    [JsonPropertyName("run")]
    public bool Run { get; set; } = true;
}

public sealed class OpenHandsContentDto
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "text";

    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;
}

public sealed class OpenHandsConversationInfo
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("execution_status")]
    public string ExecutionStatus { get; set; } = string.Empty;

    [JsonPropertyName("stats")]
    public OpenHandsStatsDto? Stats { get; set; }
}

public sealed class OpenHandsStatsDto
{
    [JsonPropertyName("usage_to_metrics")]
    public Dictionary<string, OpenHandsUsageMetricDto>? UsageToMetrics { get; set; }
}

public sealed class OpenHandsUsageMetricDto
{
    [JsonPropertyName("model_name")]
    public string? ModelName { get; set; }

    [JsonPropertyName("accumulated_token_usage")]
    public OpenHandsTokenUsageDto? AccumulatedTokenUsage { get; set; }
}

public sealed class OpenHandsTokenUsageDto
{
    [JsonPropertyName("prompt_tokens")]
    public int? PromptTokens { get; set; }

    [JsonPropertyName("completion_tokens")]
    public int? CompletionTokens { get; set; }
}

public sealed class OpenHandsAgentResponseResult
{
    [JsonPropertyName("response")]
    public string Response { get; set; } = string.Empty;
}

public sealed class OpenHandsSuccessResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }
}
