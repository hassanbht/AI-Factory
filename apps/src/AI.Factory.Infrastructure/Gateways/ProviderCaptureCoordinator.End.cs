using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Abstractions.Execution;
using AI.Factory.Application.CostAccounting;
using AI.Factory.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace AI.Factory.Infrastructure.Gateways;

public sealed partial class ProviderCaptureCoordinator
{
    public async Task EndCaptureAsync(Guid agentRunId, bool succeeded, CancellationToken ct = default)
    {
        if (!_active.TryRemove(agentRunId, out var active))
        {
            return;
        }

        try
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var captured in active.Proxy.Captured)
            {
                if (!seen.Add(captured.GenerationId))
                {
                    continue;
                }

                ProviderGeneration row;
                try
                {
                    row = await _ledger.RecordGenerationAsync(new ProviderGeneration
                    {
                        CertificationEpoch = active.Context.CertificationEpoch,
                        BenchmarkIdentity = active.Context.BenchmarkIdentity,
                        WorkItemId = active.Context.WorkItemId,
                        AgentRunId = active.Context.AgentRunId,
                        GenerationId = captured.GenerationId,
                        ModelId = captured.Model ?? active.Context.ModelId,
                        Provider = active.Context.Provider,
                        RequestedAtUtc = DateTime.UtcNow,
                        PromptTokens = captured.PromptTokens,
                        CompletionTokens = captured.CompletionTokens,
                        TotalTokens = captured.TotalTokens,
                        AccountingStatus = ProviderGenerationAccountingStatus.PendingUsage,
                        AccountingNote = captured.WasStreaming ? "captured-sse" : "captured-json"
                    }, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Capture ledger record failed; generation stays UNKNOWN.");
                    continue;
                }

                await ReconcileAsync(row, ct).ConfigureAwait(false);
            }

            active.Session.CapturedGenerationCount = seen.Count;
            active.Session.Status = succeeded ? "Completed" : "CompletedRunFailed";
            active.Session.CompletedAtUtc = DateTime.UtcNow;
            try
            {
                await _sessions.UpdateAsync(active.Session, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Capture session completion persistence failed.");
            }
        }
        finally
        {
            await active.Proxy.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task ReconcileAsync(ProviderGeneration row, CancellationToken ct)
    {
        OpenRouterGenerationLookupResult lookup;
        try
        {
            lookup = await _usage.GetGenerationAsync(row.GenerationId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Authoritative usage lookup failed; generation stays unresolved.");
            await ApplyAsync(row.GenerationId, new ProviderGenerationUsageOutcome(
                row.GenerationId, null, null, null, null, null, null,
                ProviderGenerationAccountingStatus.Unknown, "lookup-transport-failed"), ct).ConfigureAwait(false);
            return;
        }

        var validation = OpenRouterGenerationUsageValidator.Validate(row.GenerationId, lookup.HttpStatusCode, lookup.ResponseBody);
        if (!validation.Ok || validation.Usage is null)
        {
            var status = validation.IsTransient
                ? ProviderGenerationAccountingStatus.PendingUsage
                : ProviderGenerationAccountingStatus.ReconciliationFailed;
            await ApplyAsync(row.GenerationId, new ProviderGenerationUsageOutcome(
                row.GenerationId, null, null, null, null, null, null, status, validation.Error), ct).ConfigureAwait(false);
            return;
        }

        var usage = validation.Usage;
        await ApplyAsync(row.GenerationId, new ProviderGenerationUsageOutcome(
            usage.GenerationId, usage.GenerationId, usage.Model, usage.PromptTokens,
            usage.CompletionTokens, usage.TotalTokens, usage.TotalCostUsd,
            ProviderGenerationAccountingStatus.Verified, null), ct).ConfigureAwait(false);
    }

    private async Task ApplyAsync(string generationId, ProviderGenerationUsageOutcome outcome, CancellationToken ct)
    {
        try
        {
            await _ledger.ApplyAuthoritativeUsageAsync(generationId, outcome, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Authoritative usage persistence failed; generation stays unresolved.");
        }
    }
}
