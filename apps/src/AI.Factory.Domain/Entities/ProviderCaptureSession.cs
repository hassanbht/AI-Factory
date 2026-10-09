namespace AI.Factory.Domain.Entities;

/// <summary>
/// Phase Q.3 — Factory-owned capture of one upstream model response on the
/// real execution route (per-run loopback reverse proxy in front of the
/// routed gateway). One row per run; the proxy forwards bytes unchanged and
/// records only ids, model, token counts and capture status — never prompts,
/// completions, secrets or bodies.
/// </summary>
public sealed class ProviderCaptureSession
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid AgentRunId { get; set; }

    public Guid? WorkItemId { get; set; }

    public string? CertificationEpoch { get; set; }

    public string? BenchmarkIdentity { get; set; }

    public string? GatewayKey { get; set; }

    public string? ModelId { get; set; }

    /// <summary>Loopback listen prefix actually bound (127.0.0.1, ephemeral port).</summary>
    public string? ListenPrefix { get; set; }

    /// <summary>Value injected as the provider base-url override for this run only.</summary>
    public string? OverrideBaseUrl { get; set; }

    public int CapturedGenerationCount { get; set; }

    public string Status { get; set; } = "Active";

    public string? Note { get; set; }

    public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? CompletedAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
