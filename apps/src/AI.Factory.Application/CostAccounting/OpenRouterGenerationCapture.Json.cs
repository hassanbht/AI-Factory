using System.Text.Json;

namespace AI.Factory.Application.CostAccounting;

public static partial class OpenRouterGenerationCapture
{
    private static IEnumerable<CapturedGeneration> WalkForGenerations(JsonElement element, int depth)
    {
        if (depth > 6)
        {
            yield break;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            var id = ReadString(element, "id") ?? ReadString(element, "generation_id");
            if (IsWellFormedGenerationId(id))
            {
                var model = ReadString(element, "model");
                var (p, c, t) = ReadUsage(element);
                var looksLikeGeneration = model is not null || p.HasValue || c.HasValue || t.HasValue
                    || HasProperty(element, "choices") || HasProperty(element, "usage");
                if (looksLikeGeneration)
                {
                    yield return new CapturedGeneration(id!, model, p, c, t, null);
                }
            }

            var yielded = 0;
            foreach (var property in element.EnumerateObject())
            {
                if (property.NameEquals("prompt") || property.NameEquals("messages")
                    || property.NameEquals("completion") || property.NameEquals("choices")
                    || property.NameEquals("content") || property.NameEquals("text"))
                {
                    continue;
                }

                foreach (var nested in WalkForGenerations(property.Value, depth + 1))
                {
                    yield return nested;
                    if (++yielded >= MaxGenerationsPerResponse)
                    {
                        yield break;
                    }
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var yielded = 0;
            foreach (var item in element.EnumerateArray())
            {
                foreach (var nested in WalkForGenerations(item, depth + 1))
                {
                    yield return nested;
                    if (++yielded >= MaxGenerationsPerResponse)
                    {
                        yield break;
                    }
                }
            }
        }
    }

    private static bool HasProperty(JsonElement element, string name)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (property.NameEquals(name))
            {
                return true;
            }
        }

        return false;
    }

    private static string? ReadString(JsonElement element, string name)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (property.NameEquals(name) && property.Value.ValueKind == JsonValueKind.String)
            {
                var value = property.Value.GetString();
                return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            }
        }

        return null;
    }

    private static (int? Prompt, int? Completion, int? Total) ReadUsage(JsonElement element)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (property.NameEquals("usage") && property.Value.ValueKind == JsonValueKind.Object)
            {
                var usage = property.Value;
                return (
                    ReadPositiveInt(usage, "prompt_tokens"),
                    ReadPositiveInt(usage, "completion_tokens"),
                    ReadPositiveInt(usage, "total_tokens"));
            }
        }

        return (
            ReadPositiveInt(element, "prompt_tokens"),
            ReadPositiveInt(element, "completion_tokens"),
            ReadPositiveInt(element, "total_tokens"));
    }

    private static int? ReadPositiveInt(JsonElement element, string name)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (property.NameEquals(name)
                && property.Value.ValueKind == JsonValueKind.Number
                && property.Value.TryGetInt32(out var value)
                && value > 0)
            {
                return value;
            }
        }

        return null;
    }
}
