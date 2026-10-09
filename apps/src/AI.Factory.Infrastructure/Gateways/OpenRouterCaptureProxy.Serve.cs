using Microsoft.Extensions.Logging;

namespace AI.Factory.Infrastructure.Gateways;

public sealed partial class OpenRouterCaptureProxy
{
    private async Task ServeAsync(CancellationToken ct)
    {
        var listener = _listener;
        if (listener is null)
        {
            return;
        }

        while (!ct.IsCancellationRequested && listener.IsListening)
        {
            System.Net.HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                if (ct.IsCancellationRequested)
                {
                    break;
                }

                _logger.LogWarning("Capture proxy accept failed.");
                break;
            }

            _ = HandleAsync(context, ct);
        }
    }

    private async Task HandleAsync(System.Net.HttpListenerContext context, CancellationToken ct)
    {
        var request = context.Request;
        var response = context.Response;
        var path = request.Url?.AbsolutePath ?? "/";
        var query = request.Url?.Query ?? string.Empty;

        byte[] incoming;
        try
        {
            using var ms = new MemoryStream();
            await request.InputStream.CopyToAsync(ms, ct).ConfigureAwait(false);
            incoming = ms.ToArray();
            if (incoming.Length > 4 * 1024 * 1024)
            {
                response.StatusCode = 413;
                response.Close();
                return;
            }
        }
        catch (OperationCanceledException)
        {
            try { response.Abort(); } catch { }
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Capture proxy failed reading inbound request.");
            try { response.StatusCode = 502; response.Close(); } catch { }
            return;
        }

        var isSse = request.Headers["Accept"]?.Contains("event-stream", StringComparison.OrdinalIgnoreCase) == true;

        HttpResponseMessage upstream;
        try
        {
            using var forward = new HttpRequestMessage(new HttpMethod(request.HttpMethod), _upstreamBaseUrl + path + query);
            if (incoming.Length > 0)
            {
                forward.Content = new ByteArrayContent(incoming);
                if (!string.IsNullOrWhiteSpace(request.ContentType))
                {
                    forward.Content.Headers.TryAddWithoutValidation("Content-Type", request.ContentType);
                }
            }

            foreach (string? header in request.Headers.AllKeys ?? Array.Empty<string>())
            {
                if (header is null)
                {
                    continue;
                }

                if (string.Equals(header, "Host", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(header, "Content-Length", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(header, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var values = request.Headers.GetValues(header);
                if (values is null)
                {
                    continue;
                }

                foreach (var value in values)
                {
                    if (!forward.Headers.TryAddWithoutValidation(header, value))
                    {
                        forward.Content?.Headers.TryAddWithoutValidation(header, value);
                    }
                }
            }

            upstream = await _upstream.SendAsync(forward, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { response.Abort(); } catch { }
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Capture proxy upstream forward failed.");
            try { response.StatusCode = 502; response.Close(); } catch { }
            return;
        }

        using (upstream)
        {
            await TeeAsync(upstream, response, isSse, ct).ConfigureAwait(false);
        }
    }
}
