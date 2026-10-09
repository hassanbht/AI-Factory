namespace AI.Factory.Application.Abstractions.Execution;

/// <summary>
/// Phase Q.3 — optional Factory-owned capture hook for the execution path.
/// Implementations bind the Factory-controlled <see cref="ProviderCaptureContext"/>
/// to a per-run capture session and persist observed generations. Null/absent
/// means "no capture" — execution proceeds unchanged and cost stays UNKNOWN.
/// </summary>
public interface IProviderCaptureCoordinator
{
    Task<string?> BeginCaptureAsync(ProviderCaptureContext context, CancellationToken cancellationToken = default);
    Task EndCaptureAsync(Guid agentRunId, bool succeeded, CancellationToken cancellationToken = default);
}
