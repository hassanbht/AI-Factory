using Microsoft.Extensions.Logging;

namespace AI.Factory.Infrastructure.Gateways;

public sealed partial class OpenRouterCaptureProxy
{
    private async Task TeeAsync(
        HttpResponseMessage upstream,
        System.Net.HttpListenerResponse response,
        bool requestWantsSse,
        CancellationToken ct)
    {
        response.StatusCode = (int)upstream.StatusCode;
        string? generationHeader = null;
        foreach (var header in upstream.Headers)
        {
            if (string.Equals(header.Key, "X-Generation-Id", StringComparison.OrdinalIgnoreCase))
            {
                generationHeader = header.Value.FirstOrDefault();
            }

            if (!string.Equals(header.Key, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(header.Key, "Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                try { response.Headers[header.Key] = string.Join(",", header.Value); } catch { }
            }
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (generationHeader is not null)
        {
            headers["X-Generation-Id"] = generationHeader;
        }

        var isSse = requestWantsSse
            || upstream.Content.Headers.ContentType?.MediaType?.Contains("event-stream", StringComparison.OrdinalIgnoreCase) == true;

        try
        {
            if (isSse)
            {
                await TeeSseAsync(upstream, response, headers, ct).ConfigureAwait(false);
            }
            else
            {
                await TeeBufferedAsync(upstream, response, headers, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            try { response.Abort(); } catch { }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Capture proxy streaming failed.");
            try { response.Abort(); } catch { }
        }
    }

    private async Task TeeBufferedAsync(
        HttpResponseMessage upstream,
        System.Net.HttpListenerResponse response,
        Dictionary<string, string> headers,
        CancellationToken ct)
    {
        var bytes = await upstream.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        var preview = System.Text.Encoding.UTF8.GetString(bytes.Length > 256 * 1024 ? bytes[..(256 * 1024)] : bytes);
        var captured = Application.CostAccounting.OpenRouterGenerationCapture.CaptureFromResponse(headers, preview, false);
        foreach (var generation in captured)
        {
            Captured.Enqueue(new CapturedUpstreamResponse(
                generation.GenerationId, generation.Model, generation.PromptTokens,
                generation.CompletionTokens, generation.TotalTokens, (int)upstream.StatusCode, false));
        }

        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, ct).ConfigureAwait(false);
        response.Close();
    }

    private async Task TeeSseAsync(
        HttpResponseMessage upstream,
        System.Net.HttpListenerResponse response,
        Dictionary<string, string> headers,
        CancellationToken ct)
    {
        response.SendChunked = true;
        var output = response.OutputStream;
        using var input = await upstream.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[8192];
        var text = new System.Text.StringBuilder();
        int read;
        while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            await output.FlushAsync(ct).ConfigureAwait(false);
            if (text.Length < 256 * 1024)
            {
                text.Append(System.Text.Encoding.UTF8.GetString(buffer, 0, read));
            }
        }

        var captured = Application.CostAccounting.OpenRouterGenerationCapture.CaptureFromResponse(headers, text.ToString(), true);
        foreach (var generation in captured)
        {
            Captured.Enqueue(new CapturedUpstreamResponse(
                generation.GenerationId, generation.Model, generation.PromptTokens,
                generation.CompletionTokens, generation.TotalTokens, (int)upstream.StatusCode, true));
        }

        try { output.Close(); } catch { }
        response.Close();
    }
}
