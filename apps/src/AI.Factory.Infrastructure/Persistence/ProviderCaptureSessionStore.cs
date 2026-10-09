using AI.Factory.Application.Abstractions;
using AI.Factory.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AI.Factory.Infrastructure.Persistence;

/// <summary>Phase Q.3 — EF Core persistence for <see cref="ProviderCaptureSession"/> rows.</summary>
public sealed class ProviderCaptureSessionStore : IProviderCaptureSessionStore
{
    private readonly FactoryDbContext _db;

    public ProviderCaptureSessionStore(FactoryDbContext db) => _db = db;

    public async Task<ProviderCaptureSession> CreateAsync(ProviderCaptureSession session, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        session.CreatedAtUtc = DateTime.UtcNow;
        session.UpdatedAtUtc = DateTime.UtcNow;
        _db.ProviderCaptureSessions.Add(session);
        await _db.SaveChangesAsync(ct);
        return session;
    }

    public Task<ProviderCaptureSession?> GetByAgentRunAsync(Guid agentRunId, CancellationToken ct = default) =>
        _db.ProviderCaptureSessions.AsNoTracking().SingleOrDefaultAsync(x => x.AgentRunId == agentRunId, ct);

    public async Task UpdateAsync(ProviderCaptureSession session, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        session.UpdatedAtUtc = DateTime.UtcNow;
        _db.ProviderCaptureSessions.Update(session);
        await _db.SaveChangesAsync(ct);
    }
}
