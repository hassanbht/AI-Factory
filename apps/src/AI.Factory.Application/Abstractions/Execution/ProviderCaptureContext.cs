namespace AI.Factory.Application.Abstractions.Execution;

/// <summary>
/// Phase Q.3 — Factory-controlled correlation context for one execution's
/// provider capture. Plain values only (no entities, secrets, HTTP). The
/// execution path stamps this from its own context; correlation never depends
/// on parsing task names or guessing from timestamps.
/// </summary>
public sealed record ProviderCaptureContext(
    Guid AgentRunId,
    Guid? WorkItemId,
    string? CertificationEpoch,
    string? BenchmarkIdentity,
    string? GatewayKey,
    string? ModelId,
    string? Provider);
