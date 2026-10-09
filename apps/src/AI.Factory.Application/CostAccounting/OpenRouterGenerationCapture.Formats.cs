using System.Text.Json;

namespace AI.Factory.Application.CostAccounting;

public static partial class OpenRouterGenerationCapture
{
    private static IEnumerable<CapturedGeneration> CaptureFromSse(string body)
    {
        var frames = 0;
        string? terminalId = null;
        string? terminalModel = null;
        int? prompt = null, completion = null, total = null;

        foreach (var rawLine in body.Split('\n'))
        {
            if (frames >= MaxFramesPerResponse)
            {
                yield break;
            }

            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith(':'))
            {
                continue;
            }

            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var payload = line["data:".Length..].Trim();
            if (payload.Length == 0 || string.Equals(payload, "[DONE]", StringComparison.Ordinal))
            {
                continue;
            }

            if (payload.Length > MaxFrameBytes)
            {
                frames++;
                continue;
            }

            frames++;
            JsonDocument? document = null;
            try
            {
                document = JsonDocument.Parse(payload);
            }
            catch (JsonException)
            {
                continue;
            }

            using (document)
            {
                var root = document.RootElement;
                var id = ReadString(root, "id") ?? ReadString(root, "generation_id") ?? ReadString(root, "generationId");
                var model = ReadString(root, "model");
                var (p, c, t) = ReadUsage(root);
                if (p.HasValue) prompt = p;
                if (c.HasValue) completion = c;
                if (t.HasValue) total = t;
                if (IsWellFormedGenerationId(id))
                {
                    terminalId = id;
                    if (model is not null) terminalModel = model;
                }
                else if (model is not null && terminalModel is null)
                {
                    terminalModel = model;
                }
            }
        }

        if (terminalId is not null)
        {
            yield return new CapturedGeneration(terminalId, terminalModel, prompt, completion, total, "sse-terminal-usage");
        }
    }

    private static IEnumerable<CapturedGeneration> CaptureFromJson(string body)
    {
        if (body.Length > MaxFrameBytes * 4)
        {
            yield break;
        }

        JsonDocument? document = null;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            yield break;
        }

        using (document)
        {
            var root = document.RootElement;
            foreach (var candidate in WalkForGenerations(root, depth: 0))
            {
                yield return candidate;
            }
        }
    }
}
