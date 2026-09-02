namespace AI.Factory.Infrastructure.Agents;

public sealed class HermesOptions
{
    public string DockerImage { get; set; } = "nousresearch/hermes-agent:latest";

    /// <summary>Host path mounted at /opt/data (Hermes's own persistent state).</summary>
    public string DataVolume { get; set; } = string.Empty;

    /// <summary>Host directory under which a fresh isolated subfolder is created per execution, mounted at /workspace.</summary>
    public string WorkspaceRoot { get; set; } = string.Empty;

    public int TimeoutSeconds { get; set; } = 300;

    public int MaxOutputLength { get; set; } = 100_000;

    /// <summary>Docker's --network value. "none" (least privilege, no network access) is the production default.</summary>
    public string NetworkMode { get; set; } = "none";
}
