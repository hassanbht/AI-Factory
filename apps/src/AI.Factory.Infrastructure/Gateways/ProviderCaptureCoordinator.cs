using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Abstractions.Execution;
using AI.Factory.Domain.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AI.Factory.Infrastructure.Gateways;

/// <summary>
/// Phase Q.3 — prototype capture coordinator. It manages a loopback proxy and
/// reconciliation lifecycle, but the production Hermes-in-Docker route cannot
/// safely reach this host-loopback listener; it remains disconnected until a
/// verified in-container transport interception is implemented. Never interpret
/// its mock-tested support as live cost capture. Ambiguity stays UNKNOWN.
/// </summary>
public sealed partial class ProviderCaptureCoordinator : IProviderCaptureCoordinator
{
    private readonly ProviderCostCaptureOptions _options;
    private readonly IProviderGenerationLedger _ledger;
    private readonly IProviderCaptureSessionStore _sessions;
    private readonly IOpenRouterGenerationClient _usage;
    private readonly Func<HttpClient> _proxyUpstream;
    private readonly ILogger<ProviderCaptureCoordinator> _logger;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, ActiveSession> _active = new();

    public ProviderCaptureCoordinator(
        ProviderCostCaptureOptions options,
        IProviderGenerationLedger ledger,
        IProviderCaptureSessionStore sessions,
        IOpenRouterGenerationClient usage,
        Func<HttpClient> proxyUpstream,
        ILogger<ProviderCaptureCoordinator> logger)
    {
        _options = options;
        _ledger = ledger;
        _sessions = sessions;
        _usage = usage;
        _proxyUpstream = proxyUpstream;
        _logger = logger;
    }

    public async Task<string?> BeginCaptureAsync(ProviderCaptureContext context, CancellationToken ct = default)
    {
        if (!_options.Enabled)
        {
            return null;
        }

        if (!string.Equals(context.GatewayKey, OpenRouterGateway.Key, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var upstream = string.IsNullOrWhiteSpace(_options.UpstreamBaseUrl)
            ? OpenRouterGateway.DefaultBaseUrl
            : _options.UpstreamBaseUrl.TrimEnd('/');

        OpenRouterCaptureProxy proxy;
        try
        {
            proxy = new OpenRouterCaptureProxy(
                upstream, _proxyUpstream(),
                NullLogger<OpenRouterCaptureProxy>.Instance);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Capture proxy construction failed ({ExceptionType}); proceeding without capture.", ex.GetType().Name);
            return null;
        }

        string prefix;
        try
        {
            prefix = proxy.Start();
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Capture proxy start failed ({ExceptionType}); proceeding without capture.", ex.GetType().Name);
            await proxy.DisposeAsync().ConfigureAwait(false);
            return null;
        }

        var session = new ProviderCaptureSession
        {
            AgentRunId = context.AgentRunId,
            WorkItemId = context.WorkItemId,
            CertificationEpoch = context.CertificationEpoch,
            BenchmarkIdentity = context.BenchmarkIdentity,
            GatewayKey = context.GatewayKey,
            ModelId = context.ModelId,
            ListenPrefix = prefix,
            OverrideBaseUrl = prefix,
            Status = "Active"
        };

        try
        {
            await _sessions.CreateAsync(session, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Capture session persistence failed ({ExceptionType}); proceeding without capture.", ex.GetType().Name);
            await proxy.DisposeAsync().ConfigureAwait(false);
            return null;
        }

        _active[context.AgentRunId] = new ActiveSession(proxy, session, context);
        return prefix;
    }

    private sealed record ActiveSession(OpenRouterCaptureProxy Proxy, ProviderCaptureSession Session, ProviderCaptureContext Context);
}
