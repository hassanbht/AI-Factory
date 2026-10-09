using AI.Factory.Application.Abstractions;
using AI.Factory.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AI.Factory.Infrastructure.Persistence;

public sealed partial class ProviderGenerationLedger
{
    public async Task<ProviderGeneration> ApplyAuthoritativeUsageAsync(
        string generationId,
        ProviderGenerationUsageOutcome outcome,
        CancellationToken cancellationToken = default)
    {
        var row = await _db.ProviderGenerations
            .SingleOrDefaultAsync(x => x.GenerationId == generationId, cancellationToken)
            ?? throw new InvalidOperationException("Generation is not recorded; cannot apply usage.");

        if (!string.Equals(outcome.RequestedGenerationId, generationId, StringComparison.Ordinal)
            || (outcome.ReturnedGenerationId is not null
                && !string.Equals(outcome.ReturnedGenerationId, generationId, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("Requested/returned generation id mismatch; refusing to apply usage.");
        }

        if (row.AccountingStatus == ProviderGenerationAccountingStatus.Verified
            && outcome.AccountingStatus == ProviderGenerationAccountingStatus.Verified
            && row.AuthoritativeCostUsd == outcome.TotalCostUsd)
        {
            return row;
        }

        row.ModelId = outcome.Model ?? row.ModelId;
        row.PromptTokens = outcome.PromptTokens ?? row.PromptTokens;
        row.CompletionTokens = outcome.CompletionTokens ?? row.CompletionTokens;
        row.TotalTokens = outcome.TotalTokens ?? row.TotalTokens;
        if (outcome.TotalCostUsd.HasValue)
        {
            row.AuthoritativeCostUsd = outcome.TotalCostUsd;
        }

        row.AccountingStatus = outcome.AccountingStatus;
        row.AccountingNote = outcome.Note;
        row.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        return row;
    }

    public async Task<IReadOnlyList<ProviderGeneration>> ListUnresolvedAsync(CancellationToken ct = default) =>
        await _db.ProviderGenerations.AsNoTracking()
            .Where(x => x.AccountingStatus == ProviderGenerationAccountingStatus.PendingUsage
                || x.AccountingStatus == ProviderGenerationAccountingStatus.Unknown)
            .OrderBy(x => x.RequestedAtUtc).ToListAsync(ct);

    public async Task<decimal?> SumVerifiedCostForEpochAsync(string epoch, CancellationToken ct = default)
    {
        var rows = await _db.ProviderGenerations.AsNoTracking()
            .Where(x => x.CertificationEpoch == epoch).ToListAsync(ct);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        decimal sum = 0m;
        foreach (var row in rows)
        {
            if (!seen.Add(row.GenerationId))
            {
                continue;
            }

            if (row.AccountingStatus != ProviderGenerationAccountingStatus.Verified
                || !row.AuthoritativeCostUsd.HasValue)
            {
                return null;
            }

            sum += row.AuthoritativeCostUsd.Value;
        }

        return sum;
    }
}
