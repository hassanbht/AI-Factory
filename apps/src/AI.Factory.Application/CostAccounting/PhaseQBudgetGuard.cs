using AI.Factory.Domain.Entities;

namespace AI.Factory.Application.CostAccounting;

/// <summary>
/// Phase Q.3 — fail-closed certification budget guard.
/// All certified spend is authoritative generation-linked cost; unresolved
/// usage stays UNKNOWN and never enlarges the apparent remaining budget.
/// Uses decimal accounting (no float); duplicate records cannot double-count.
/// </summary>
public sealed class PhaseQBudgetGuard
{
    public const decimal MaxSpendUsd = 2.00m;

    public sealed record CheckResult(bool Allowed, decimal CertifiedSpendUsd, decimal RemainingUsd, string Reason);

    /// <summary>
    /// Pre-request check: blocks when any unresolved generation exists, when a safe
    /// upper bound cannot be established, or when the bound would breach the ceiling.
    /// </summary>
    public static CheckResult CheckBeforeRequest(
        IReadOnlyList<ProviderGeneration> epochGenerations,
        decimal? requestUpperBoundUsd,
        decimal maxSpendUsd = MaxSpendUsd)
    {
        var (certified, unresolved) = Summarize(epochGenerations);
        if (unresolved > 0)
        {
            return new CheckResult(false, certified, Remaining(maxSpendUsd, certified),
                $"Blocked: {unresolved} generation(s) with unresolved authoritative cost; no further billed work.");
        }

        if (requestUpperBoundUsd is null)
        {
            return new CheckResult(false, certified, Remaining(maxSpendUsd, certified),
                "Blocked: no safe upper bound for this request could be established.");
        }

        if (requestUpperBoundUsd < 0)
        {
            return new CheckResult(false, certified, Remaining(maxSpendUsd, certified),
                "Blocked: negative upper bound is invalid.");
        }

        if (certified + requestUpperBoundUsd.Value > maxSpendUsd)
        {
            return new CheckResult(false, certified, Remaining(maxSpendUsd, certified),
                $"Blocked: certified {certified:0.00000000} + bound {requestUpperBoundUsd.Value:0.00000000} exceeds ceiling {maxSpendUsd:0.00}.");
        }

        return new CheckResult(true, certified, Remaining(maxSpendUsd, certified), "Allowed within certified budget.");
    }

    /// <summary>Post-request check: any unresolved generation blocks further billed work.</summary>
    public static CheckResult CheckAfterReconciliation(
        IReadOnlyList<ProviderGeneration> epochGenerations,
        decimal maxSpendUsd = MaxSpendUsd)
    {
        var (certified, unresolved) = Summarize(epochGenerations);
        if (unresolved > 0)
        {
            return new CheckResult(false, certified, Remaining(maxSpendUsd, certified),
                $"Blocked: {unresolved} generation(s) still lack authoritative cost.");
        }

        if (certified > maxSpendUsd)
        {
            return new CheckResult(false, certified, Remaining(maxSpendUsd, certified),
                $"Blocked: certified spend {certified:0.00000000} exceeds ceiling {maxSpendUsd:0.00}.");
        }

        return new CheckResult(true, certified, Remaining(maxSpendUsd, certified), "Ledger reconciled within ceiling.");
    }

    private static (decimal Certified, int Unresolved) Summarize(IReadOnlyList<ProviderGeneration> generations)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        decimal certified = 0m;
        var unresolved = 0;

        foreach (var generation in generations)
        {
            if (!seen.Add(generation.GenerationId))
            {
                continue; // duplicate record: counted once, never double-counted.
            }

            if (generation.AccountingStatus == ProviderGenerationAccountingStatus.Verified
                && generation.AuthoritativeCostUsd.HasValue)
            {
                certified += generation.AuthoritativeCostUsd.Value;
            }
            else
            {
                unresolved++;
            }
        }

        return (certified, unresolved);
    }

    private static decimal Remaining(decimal ceiling, decimal certified) =>
        ceiling - certified < 0 ? 0 : ceiling - certified;
}
