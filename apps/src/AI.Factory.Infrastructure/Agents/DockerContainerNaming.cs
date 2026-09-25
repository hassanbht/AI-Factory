namespace AI.Factory.Infrastructure.Agents;

/// <summary>
/// Phase D.1 — Deterministic container naming and label conventions.
/// Every agent execution container must be identifiable by:
///   factory=ai-factory
///   managed=true
///   agent-run-id=&lt;GUID&gt;
///   adapter=&lt;Key&gt;
///   project-id=&lt;GUID&gt; (when available)
/// Container names follow the deterministic pattern:
///   ai-factory-agent-&lt;agent-run-id:N&gt;
/// </summary>
public static class DockerContainerNaming
{
    public const string FactoryLabelKey = "factory";
    public const string FactoryLabelValue = "ai-factory";

    public const string ManagedLabelKey = "managed";
    public const string ManagedLabelValue = "true";

    public const string AgentRunIdLabelKey = "agent-run-id";
    public const string ProjectIdLabelKey = "project-id";
    public const string AdapterLabelKey = "adapter";

    public const string ContainerNamePrefix = "ai-factory-agent-";

    /// <summary>Produces the deterministic container name for the given AgentRunId.</summary>
    public static string GetContainerName(Guid agentRunId) =>
        $"{ContainerNamePrefix}{agentRunId:N}";

    /// <summary>Returns the standard Docker metadata labels for an execution container.</summary>
    public static Dictionary<string, string> GetExecutionLabels(
        Guid agentRunId,
        Guid? projectId = null,
        string adapter = "Hermes")
    {
        var labels = new Dictionary<string, string>
        {
            [FactoryLabelKey] = FactoryLabelValue,
            [ManagedLabelKey] = ManagedLabelValue,
            [AgentRunIdLabelKey] = agentRunId.ToString(),
            [AdapterLabelKey] = adapter
        };

        if (projectId.HasValue)
        {
            labels[ProjectIdLabelKey] = projectId.Value.ToString();
        }

        return labels;
    }
}
