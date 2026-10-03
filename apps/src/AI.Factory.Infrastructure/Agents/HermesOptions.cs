namespace AI.Factory.Infrastructure.Agents;

public sealed class HermesOptions
{
    public string DockerImage { get; set; } = "nousresearch/hermes-agent:0.20.6";

    /// <summary>Directory under which a fresh isolated subfolder is created per execution, mounted at /workspace.</summary>
    public string WorkspaceRoot { get; set; } = string.Empty;

    /// <summary>
    /// Phase 18.29.1 — name of the Docker volume backing <see cref="WorkspaceRoot"/>
    /// when the Worker itself runs in a container and reaches the daemon through a
    /// mounted socket. Unset (the default, and every host/dev/test execution) keeps
    /// the original `-v &lt;workspacePath&gt;:/workspace` bind mount unchanged.
    ///
    /// When set, the per-execution workspace is mounted as a SUBPATH of that volume
    /// (`--mount type=volume,volume-subpath=...`) rather than as a host path, which
    /// is the only form that addresses the correct directory when the daemon is the
    /// host's. The subpath is always derived from the Factory-resolved workspace
    /// relative to <see cref="WorkspaceRoot"/> — an execution whose workspace falls
    /// outside that root is refused rather than mounted, so this never widens what
    /// Hermes can reach beyond the workspace volume.
    /// </summary>
    public string? WorkspaceVolumeName { get; set; }


    public int TimeoutSeconds { get; set; } = 300;

    public int MaxOutputLength { get; set; } = 100_000;

    /// <summary>Docker's --network value. "none" (least privilege, no network access) is the production default.</summary>
    public string NetworkMode { get; set; } = "none";

    /// <summary>Hermes CLI --provider value (e.g. "openrouter"). Null/empty means "let Hermes use its own configured default".</summary>
    public string? Provider { get; set; }

    /// <summary>
    /// Phase 18.20 — Hermes CLI -m fallback for any AgentRun that carries no
    /// Model of its own. Nothing in the production pipeline populates
    /// AgentRun.Model (TaskDispatchScheduler and ProjectPlanner both create
    /// runs without one), so without this every scheduler-dispatched run
    /// silently executed on whatever model Hermes had persisted internally,
    /// with no way to configure it from the Factory side. An explicit
    /// AgentRun.Model still wins — e.g. AgentCodeReviewer, which routes its
    /// own model through IModelRouter. Null/empty keeps the pre-existing
    /// behavior (no -m flag at all).
    /// </summary>
    public string? DefaultModel { get; set; }

    /// <summary>
    /// Non-secret environment variables to pass into the Hermes container beyond PATH. Phase K: a
    /// <c>{PROVIDER}_BASE_URL</c> entry (e.g. OPENROUTER_BASE_URL) must equal the routed model
    /// gateway's BaseUrl — an override pointing Hermes at a different endpoint than the gateway the
    /// run was routed to is refused, never silently used.
    /// Never put a secret value here — use <see cref="ProviderApiKeyEnvironmentVariable"/>/
    /// <see cref="ProviderApiKeySecretName"/> instead, which resolve through ISecretProvider.
    /// </summary>
    public Dictionary<string, string> AdditionalEnvironmentVariables { get; set; } = new();

    /// <summary>Container environment variable name to receive the resolved provider API key (e.g. "OPENROUTER_API_KEY").</summary>
    public string? ProviderApiKeyEnvironmentVariable { get; set; }

    /// <summary>Factory secret name (resolved via ISecretProvider) whose value becomes <see cref="ProviderApiKeyEnvironmentVariable"/> inside the container.</summary>
    public string? ProviderApiKeySecretName { get; set; }
}
