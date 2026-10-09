using AI.Factory.Application.CostAccounting;

namespace AI.Factory.Application.Abstractions;

/// <summary>
/// Phase Q.3 — authoritative generation-usage lookup (OpenRouter
/// <c>GET /generation?id=gen-…</c>). Implemented in Infrastructure over HTTP;
/// validated by <see cref="OpenRouterGenerationUsageValidator"/>. No prompts,
/// completions or secrets in results — ids, model, tokens and cost only.
/// </summary>
public interface IOpenRouterGenerationClient
{
    Task<OpenRouterGenerationLookupResult> GetGenerationAsync(
        string generationId,
        CancellationToken cancellationToken = default);
}

public sealed record OpenRouterGenerationLookupResult(
    string RequestedGenerationId,
    int HttpStatusCode,
    string? ResponseBody,
    bool WasEgressAttempted);
