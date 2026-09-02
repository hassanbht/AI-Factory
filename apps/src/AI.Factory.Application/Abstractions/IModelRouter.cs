namespace AI.Factory.Application.Abstractions;

public interface IModelRouter
{
    ModelSelection SelectModel(ModelRoutingRequest request);
}

public enum TaskComplexity
{
    Simple,
    Standard,
    Complex,
    Critical
}

public enum CostPreference
{
    Cheapest,
    Balanced,
    BestQuality
}

public sealed record ModelRoutingRequest(
    string TaskCategory,
    TaskComplexity Complexity,
    int EstimatedContextTokens,
    CostPreference CostPreference);

public sealed record ModelSelection(
    string ModelId,
    string Provider,
    string Reason);

/// <summary>
/// A candidate model's declared properties, as configured — not a live
/// probe. <see cref="IsAvailable"/> lets an operator disable a model
/// without removing it from the catalog.
/// </summary>
public sealed class ModelCapability
{
    public string ModelId { get; set; } = string.Empty;

    public string Provider { get; set; } = string.Empty;

    /// <summary>Ordinal scale: 1 = basic .. 5 = frontier.</summary>
    public int ReasoningStrength { get; set; }

    public int MaxContextTokens { get; set; }

    /// <summary>Ordinal scale: 1 = cheapest .. 5 = most expensive.</summary>
    public int CostTier { get; set; }

    public bool IsCodingOptimized { get; set; }

    public bool IsAvailable { get; set; } = true;
}

/// <summary>Thrown when no configured, available model satisfies a routing request.</summary>
public sealed class NoModelAvailableException : Exception
{
    public NoModelAvailableException(string message)
        : base(message)
    {
    }
}
