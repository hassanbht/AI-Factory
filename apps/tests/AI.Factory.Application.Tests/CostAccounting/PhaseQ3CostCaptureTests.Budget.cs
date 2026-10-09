using AI.Factory.Application.Abstractions;
using AI.Factory.Application.CostAccounting;
using AI.Factory.Domain.Entities;
using AI.Factory.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AI.Factory.Application.Tests.CostAccounting;

public sealed partial class PhaseQ3CostCaptureTests
{
    [Fact]
    public async Task InterruptedGeneration_StaysPending_AndReconciliationIsIdempotent()
    {
        PhaseQ3LiveEgressGuard.AssertNoLiveEgress();
        await using var db = new FactoryDbContext(_options);
        var row = await Ledger(db).RecordGenerationAsync(Row("gen-resume-1", Guid.NewGuid(), "E", "Q-A-1"));
        Assert.Equal(ProviderGenerationAccountingStatus.PendingUsage, row.AccountingStatus);
        Assert.Contains(await Ledger(db).ListUnresolvedAsync(), x => x.GenerationId == "gen-resume-1");
        Assert.Null(await Ledger(db).SumVerifiedCostForEpochAsync("E"));

        var outcome = new ProviderGenerationUsageOutcome("gen-resume-1", "gen-resume-1", "m", 1, 1, 2, 0.002m,
            ProviderGenerationAccountingStatus.Verified);
        var once = await Ledger(db).ApplyAuthoritativeUsageAsync("gen-resume-1", outcome);
        var twice = await Ledger(db).ApplyAuthoritativeUsageAsync("gen-resume-1", outcome);
        Assert.Equal(once.AuthoritativeCostUsd, twice.AuthoritativeCostUsd);
        Assert.Equal(0.002m, await Ledger(db).SumVerifiedCostForEpochAsync("E"));
    }

    [Fact]
    public void UnresolvedGeneration_BlocksFurtherBilledWork()
    {
        PhaseQ3LiveEgressGuard.AssertNoLiveEgress();
        var rows = new[] { Row("gen-block-1", Guid.NewGuid(), "E", "Q-A-1") };
        Assert.False(PhaseQBudgetGuard.CheckBeforeRequest(rows, 0.01m).Allowed);
    }

    [Fact]
    public void ConcurrentReservations_CannotBypassCeiling()
    {
        PhaseQ3LiveEgressGuard.AssertNoLiveEgress();
        Assert.True(PhaseQBudgetGuard.CheckBeforeRequest(Array.Empty<ProviderGeneration>(), 1.50m).Allowed);
        var committed = new[] { VerifiedRow("gen-conc-1", 1.50m) };
        Assert.False(PhaseQBudgetGuard.CheckBeforeRequest(committed, 1.50m).Allowed);
    }

    [Fact]
    public void ChargeViolatingCeiling_IsRejectedBeforeUpstream()
    {
        PhaseQ3LiveEgressGuard.AssertNoLiveEgress();
        var rows = new[] { VerifiedRow("gen-ceil-1", 1.99m) };
        var check = PhaseQBudgetGuard.CheckBeforeRequest(rows, 0.02m);
        Assert.False(check.Allowed);
        Assert.Equal(1.99m, check.CertifiedSpendUsd);
    }

    [Fact]
    public async Task DuplicateReconciliation_RemainsIdempotent()
    {
        PhaseQ3LiveEgressGuard.AssertNoLiveEgress();
        await using var db = new FactoryDbContext(_options);
        await Ledger(db).RecordGenerationAsync(Row("gen-idem-1", Guid.NewGuid(), "E2", "Q-B-1"));
        var outcome = new ProviderGenerationUsageOutcome("gen-idem-1", "gen-idem-1", "m", 2, 2, 4, 0.004m,
            ProviderGenerationAccountingStatus.Verified);
        await Ledger(db).ApplyAuthoritativeUsageAsync("gen-idem-1", outcome);
        await Ledger(db).ApplyAuthoritativeUsageAsync("gen-idem-1", outcome);
        Assert.Equal(0.004m, await Ledger(db).SumVerifiedCostForEpochAsync("E2"));
    }

    [Fact]
    public void Diagnostics_RedactAuthorizationAndSecrets()
    {
        PhaseQ3LiveEgressGuard.AssertNoLiveEgress();
        var sanitized = Application.Security.SensitiveDataSanitizer.Sanitize(
            "Authorization: Bearer sk-or-v1-abcdefghijklmnopqr api_key=\"hunter2-secret\"");
        Assert.DoesNotContain("sk-or-v1-abcdefghijklmnopqr", sanitized);
        Assert.DoesNotContain("hunter2-secret", sanitized);
    }
}
