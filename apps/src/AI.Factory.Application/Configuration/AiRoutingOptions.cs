namespace AI.Factory.Application.Configuration;

/// <summary>
/// Capability-based AI routing settings — bounds the fallback chain and
/// optionally pins one preferred model per capability, ahead of the
/// priority-ordered catalog. Bound from configuration's "AiRouting" section.
/// </summary>
public sealed class AiRoutingOptions
{
    public const string SectionName = "AiRouting";

    /// <summary>Upper bound on distinct provider/model candidates tried per request. Never unbounded.</summary>
    public int MaxFallbackAttempts { get; set; } = 3;

    /// <summary>Capability name (matches <c>AiCapability</c>) to a preferred ModelId, checked before priority ordering.</summary>
    public Dictionary<string, string> CapabilityPreferredModel { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    public void Validate()
    {
        if (MaxFallbackAttempts < 1)
        {
            throw new InvalidOperationException(
                $"AiRouting:MaxFallbackAttempts must be >= 1, was {MaxFallbackAttempts}.");
        }

        if (MaxFallbackAttempts > 10)
        {
            throw new InvalidOperationException(
                $"AiRouting:MaxFallbackAttempts must be <= 10 to prevent unbounded fallback chains, was {MaxFallbackAttempts}.");
        }
    }
}
