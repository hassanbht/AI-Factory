namespace AI.Factory.Infrastructure.Gateways;

/// <summary>
/// Phase Q.3 — opt-in Factory-owned cost-capture configuration.
/// Disabled by default; enabling it without a separately authorized live
/// canary still performs no provider generation (tests use loopback fakes).
/// </summary>
public sealed class ProviderCostCaptureOptions
{
    public const string SectionName = "ProviderCostCapture";

    /// <summary>Master switch. False (default): no proxy, no override, no lookup.</summary>
    public bool Enabled { get; set; }

    /// <summary>Upstream gateway the proxy forwards to (default: OpenRouter production base).</summary>
    public string UpstreamBaseUrl { get; set; } = OpenRouterGateway.DefaultBaseUrl;
}
