using Microsoft.Extensions.Logging;

namespace AI.Factory.Infrastructure.Gateways;

/// <summary>
/// Phase Q.3 — Factory-owned per-run reverse proxy that forwards
/// OpenAI-compatible requests to the routed upstream gateway and captures
/// the real upstream response (generation ids + terminal usage) without
/// altering bytes on the wire. Loopback-only, one ephemeral port per run,
/// idempotent stop. Never stores prompts, completions, auth headers or bodies.
/// </summary>
public sealed partial class OpenRouterCaptureProxy : IAsyncDisposable
{
    private readonly string _upstreamBaseUrl;
    private readonly HttpClient _upstream;
    private readonly ILogger<OpenRouterCaptureProxy> _logger;
    private System.Net.HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _serveTask;
    private int _stopped;

    public OpenRouterCaptureProxy(
        string upstreamBaseUrl,
        HttpClient upstream,
        ILogger<OpenRouterCaptureProxy> logger)
    {
        _upstreamBaseUrl = upstreamBaseUrl.TrimEnd('/');
        _upstream = upstream;
        _logger = logger;
    }

    public string? ListenPrefix { get; private set; }

    public bool IsRunning => _listener?.IsListening == true;

    public System.Collections.Concurrent.ConcurrentQueue<CapturedUpstreamResponse> Captured { get; } = new();

    public string Start()
    {
        if (IsRunning)
        {
            return ListenPrefix!;
        }

        var port = TcpFreePort();
        var prefix = $"http://127.0.0.1:{port}/";
        _listener = new System.Net.HttpListener();
        _listener.Prefixes.Add(prefix);
        _listener.Start();
        // HttpListener has no portable LocalEndpoint API. It is configured only
        // with the literal loopback prefix above; ensure listener startup succeeded.
        if (!_listener.IsListening)
        {
            _listener.Close();
            _listener = null;
            throw new InvalidOperationException("Capture proxy listener did not start on its configured loopback prefix.");
        }
        ListenPrefix = prefix;
        _cts = new CancellationTokenSource();
        _serveTask = ServeAsync(_cts.Token);
        return prefix;
    }

    public async Task StopAsync()
    {
        if (System.Threading.Interlocked.Exchange(ref _stopped, 1) != 0)
        {
            return;
        }

        try
        {
            _cts?.Cancel();
            _listener?.Stop();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Capture proxy stop encountered an error.");
        }

        if (_serveTask is not null)
        {
            try
            {
                await _serveTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Capture proxy serve loop ended with an error.");
            }
        }

        try
        {
            _listener?.Close();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Capture proxy close encountered an error.");
        }

        _cts?.Dispose();
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    public sealed record CapturedUpstreamResponse(
        string GenerationId,
        string? Model,
        int? PromptTokens,
        int? CompletionTokens,
        int? TotalTokens,
        int HttpStatusCode,
        bool WasStreaming);

    private static int TcpFreePort()
    {
        using var socket = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        socket.Start();
        var port = ((System.Net.IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        return port;
    }
}
