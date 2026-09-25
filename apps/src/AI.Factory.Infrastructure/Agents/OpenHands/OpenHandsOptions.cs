namespace AI.Factory.Infrastructure.Agents.OpenHands;

/// <summary>
/// Phase E — OpenHands Agent Server and Execution Adapter options.
/// Configured under "OpenHands" section or populated with verified defaults.
/// </summary>
public sealed class OpenHandsOptions
{
    public const string SectionName = "OpenHands";

    /// <summary>
    /// Pinned OpenHands agent-server image and digest.
    /// Pinned to version 1.49.4-python verified in Gate E0.
    /// </summary>
    public string DockerImage { get; set; } =
        "ghcr.io/openhands/agent-server:1.49.4-python@sha256:9b215fb9ad536bd6bc07964b046a45f378cd05c0d0a856bd1be3adaf6c46c69c";

    /// <summary>
    /// Optional base URL of a running OpenHands Agent Server (e.g. "http://127.0.0.1:48080").
    /// When null or empty, the adapter starts a dedicated, isolated container for each AgentRun
    /// using Docker and ensures deterministic cleanup on completion (Option A).
    /// </summary>
    public string? BaseUrl { get; set; }

    /// <summary>
    /// Session API key for authentication with the OpenHands server.
    /// When using a dedicated container, if left null/empty, an ephemeral key is generated per run.
    /// </summary>
    public string? SessionApiKey { get; set; }

    /// <summary>
    /// Root directory for isolated workspaces when no Project workspace is specified in the spec.
    /// </summary>
    public string WorkspaceRoot { get; set; } =
        Path.Combine(Path.GetTempPath(), "ai-factory-openhands");

    /// <summary>
    /// Default model slug to use if spec.Model.ModelId is null or empty.
    /// </summary>
    public string DefaultModel { get; set; } = "deepseek/deepseek-v4-flash-0731";

    /// <summary>
    /// Gateway / provider base URL for model inference (e.g. OpenRouter).
    /// </summary>
    public string ProviderBaseUrl { get; set; } = "https://openrouter.ai/api/v1";

    /// <summary>
    /// Secret name to resolve the provider API key through ISecretProvider.
    /// </summary>
    public string ProviderApiKeySecretName { get; set; } = "OPENROUTER_API_KEY";

    /// <summary>
    /// Environment variable name for the provider API key.
    /// </summary>
    public string ProviderApiKeyEnvironmentVariable { get; set; } = "OPENROUTER_API_KEY";

    /// <summary>
    /// Python modules to import at agent server startup to register required tools.
    /// </summary>
    public string ImportModules { get; set; } = "openhands.tools.terminal,openhands.tools.file_editor";

    /// <summary>
    /// Docker network mode for the agent server container.
    /// </summary>
    public string NetworkMode { get; set; } = "bridge";

    /// <summary>
    /// Timeout in seconds to wait for the agent server to become ready after container launch.
    /// </summary>
    public int StartupTimeoutSeconds { get; set; } = 45;

    /// <summary>
    /// Polling interval in seconds when observing conversation execution status.
    /// </summary>
    public double PollIntervalSeconds { get; set; } = 2.0;
}
