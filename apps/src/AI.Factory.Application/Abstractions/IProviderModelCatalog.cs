namespace AI.Factory.Application.Abstractions;

/// <summary>
/// Phase 18.29.1 — reads the set of model ids a provider can currently serve.
///
/// This exists so a configured model/provider pair can be checked for being
/// SERVABLE, which <see cref="Orchestration.ModelRoutingOptions.Validate"/>
/// deliberately cannot do: that check is offline and structural, and a model id
/// can be perfectly well-formed, present in the catalog and referenced by every
/// category while the provider has never heard of it. That is exactly how
/// "auto/coding:free" — an OmniRoute gateway virtual model — reached the v1.0.0
/// release gate configured against OpenRouter, which rejects it with HTTP 400.
///
/// Implementations live in Infrastructure (they make provider calls); the
/// Application layer only ever sees this abstraction.
/// </summary>
public interface IProviderModelCatalog
{
    /// <summary>Provider id this catalog answers for, matching ModelCapability.Provider (case-insensitive).</summary>
    string Provider { get; }

    Task<ProviderModelCatalogResult> GetAvailableModelIdsAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Why a catalog lookup produced no usable answer matters: a provider OUTAGE and
/// an INVALID MODEL ID are different conditions with different correct responses
/// (retry later vs. fix the configuration and never deploy it). Collapsing them
/// into "no models" would make an outage look like a misconfiguration and block
/// startup for something a restart would fix.
/// </summary>
public enum ProviderModelCatalogStatus
{
    /// <summary>The provider answered; <see cref="ProviderModelCatalogResult.ModelIds"/> is authoritative.</summary>
    Available = 0,

    /// <summary>The provider could not be reached or did not answer usefully. Says nothing about any model id.</summary>
    Unavailable = 1
}

public sealed record ProviderModelCatalogResult(
    ProviderModelCatalogStatus Status,
    IReadOnlyCollection<string> ModelIds,
    string? UnavailableReason)
{
    public static ProviderModelCatalogResult Available(IReadOnlyCollection<string> modelIds) =>
        new(ProviderModelCatalogStatus.Available, modelIds, null);

    public static ProviderModelCatalogResult Unavailable(string reason) =>
        new(ProviderModelCatalogStatus.Unavailable, Array.Empty<string>(), reason);
}
