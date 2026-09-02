using AI.Factory.Application.Abstractions;

namespace AI.Factory.Application.Orchestration;

/// <summary>
/// Configuration-driven model catalog and per-task-category candidate
/// lists. Bound from configuration — see <c>appsettings.json</c>'s
/// "ModelRouting" section.
/// </summary>
public sealed class ModelRoutingOptions
{
    public List<ModelCapability> Models { get; set; } = new();

    /// <summary>Task category (e.g. "coding") to an ordered list of candidate ModelIds, most preferred first.</summary>
    public Dictionary<string, List<string>> CategoryProfiles { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}
