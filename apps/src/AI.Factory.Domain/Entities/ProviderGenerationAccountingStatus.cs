namespace AI.Factory.Domain.Entities;

/// <summary>
/// Phase Q.3 — accounting lifecycle of one <see cref="ProviderGeneration"/>.
/// PendingUsage: generation observed, authoritative usage not yet retrieved.
/// Verified: authoritative usage retrieved and validated (cost may still be UNKNOWN
/// only when the provider itself reports no usable cost — otherwise set).
/// ReconciliationFailed: durable failure retrieving/validating authoritative usage.
/// Unknown: interrupted or ambiguous outcome; must block further billed work until resolved.
/// Collapsed success/failure flags are forbidden: these four states stay distinct.
/// </summary>
public enum ProviderGenerationAccountingStatus
{
    PendingUsage = 0,
    Verified = 1,
    ReconciliationFailed = 2,
    Unknown = 3,
}
