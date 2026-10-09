using AI.Factory.Domain.Entities;

namespace AI.Factory.Application.Abstractions;

/// <summary>Phase Q.3 — persistence for <see cref="ProviderCaptureSession"/> rows.</summary>
public interface IProviderCaptureSessionStore
{
    Task<ProviderCaptureSession> CreateAsync(ProviderCaptureSession session, CancellationToken cancellationToken = default);
    Task<ProviderCaptureSession?> GetByAgentRunAsync(Guid agentRunId, CancellationToken cancellationToken = default);
    Task UpdateAsync(ProviderCaptureSession session, CancellationToken cancellationToken = default);
}
