using AI.Factory.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace AI.Factory.Infrastructure.Context;

/// <summary>
/// OpenViking-backed IContextProvider for long-lived contextual knowledge
/// (project documentation, architecture decisions, previous successful
/// implementations, recurring failures, coding patterns, technical
/// knowledge, validated solutions). Never stores authoritative workflow
/// state — that stays in SQLite (see IWorkflowStateStore) — and never
/// stores source code — that stays in Git/filesystem. Purely a retrieval
/// layer, and a drop-in alternative to LocalContextProvider: the
/// Application layer depends only on IContextProvider and is unaware
/// OpenViking exists.
/// </summary>
public sealed class OpenVikingContextProvider : IContextProvider
{
    private readonly IOpenVikingClient _client;
    private readonly OpenVikingOptions _options;
    private readonly ILogger<OpenVikingContextProvider> _logger;

    public OpenVikingContextProvider(
        IOpenVikingClient client,
        OpenVikingOptions options,
        ILogger<OpenVikingContextProvider> logger)
    {
        _client = client;
        _options = options;
        _logger = logger;
    }

    public async Task<ContextResult> QueryAsync(
        ContextQuery query,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            return new ContextResult(Array.Empty<ContextDocument>());
        }

        try
        {
            var response = await _client.QueryAsync(
                new OpenVikingQueryRequestDto
                {
                    Category = query.Category.ToString(),
                    ProjectId = query.ProjectId,
                    MaxResults = query.MaxResults
                },
                cancellationToken);

            var documents = response.Documents
                .Select(d => new ContextDocument(
                    d.Source,
                    d.Content,
                    d.Relevance,
                    d.Metadata is null
                        ? new Dictionary<string, string>()
                        : new Dictionary<string, string>(d.Metadata)))
                .ToList();

            return new ContextResult(documents);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // OpenViking is optional/best-effort — any failure (network,
            // timeout, malformed response) degrades to no results rather
            // than breaking the caller. The system must stay operational
            // without OpenViking.
            _logger.LogWarning(
                ex,
                "OpenViking query failed for category {Category} — falling back to empty context.",
                query.Category);

            return new ContextResult(Array.Empty<ContextDocument>());
        }
    }
}
