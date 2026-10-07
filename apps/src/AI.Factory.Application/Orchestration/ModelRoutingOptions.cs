using AI.Factory.Application.Abstractions;

namespace AI.Factory.Application.Orchestration;

/// <summary>
/// Configuration-driven model catalog and per-task-category candidate
/// lists. Bound from configuration — see <c>appsettings.json</c>'s
/// "ModelRouting" section.
/// </summary>
public sealed class ModelRoutingOptions
{
    /// <summary>
    /// Phase 18.21 §15 — model ids that are obviously not real, shipped as
    /// examples in earlier phases. Kept as data so the check stays
    /// deterministic and needs no provider call to run.
    /// </summary>
    public static readonly IReadOnlyList<string> PlaceholderModelIds = new[]
    {
        "strong-reasoning-model-a",
        "strong-reasoning-model-b",
        "coding-optimized-model-a",
        "coding-optimized-model-b",
        "independent-strong-model-a",
        "cheap-model-a"
    };

    public List<ModelCapability> Models { get; set; } = new();

    /// <summary>Task category (e.g. "coding") to an ordered list of candidate ModelIds, most preferred first.</summary>
    public Dictionary<string, List<string>> CategoryProfiles { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Phase 18.21 §13/§14 — AgentDefinition.AgentType to the task category
    /// whose profile selects that role's model, so Implementation, Repair,
    /// Review and Planner can each be pointed at a different model purely by
    /// configuration (they are not required to differ). Before this, nothing
    /// in the production pipeline chose a model for Implementation or Repair
    /// runs at all — AgentRun.Model was always null and every run silently
    /// fell through to whatever the Hermes container had configured
    /// internally, which is why Repair could not be given a faster model when
    /// Phase 18.20 measured it exceeding the execution budget.
    /// </summary>
    public Dictionary<string, string> AgentTypeCategories { get; set; } =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Implementation"] = "coding",
            ["Repair"] = "debugging",
            ["Test"] = "testing",
            ["Review"] = "code-review",
            ["Planner"] = "planning",
            ["Documentation"] = "documentation"
        };

    /// <summary>
    /// Phase P.1 — rejects automatic or implicit model routing (e.g. openrouter/auto, auto/coding, auto).
    /// Model selection must be explicit, deterministic and fail-closed.
    /// </summary>
    public static bool IsAutoOrImplicitModel(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return false;
        }

        var trimmed = modelId.Trim();
        return trimmed.StartsWith("auto/", StringComparison.OrdinalIgnoreCase)
            || trimmed.EndsWith("/auto", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "auto", StringComparison.OrdinalIgnoreCase)
            || trimmed.IndexOf("/auto/", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>
    /// Phase 18.21 §15 — deterministic configuration validation. Never calls a
    /// provider: it only checks that the catalog is internally coherent and
    /// free of the placeholder ids no provider can serve. Returns a problem
    /// per issue so a host can log them all at once.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();

        foreach (var model in Models)
        {
            if (string.IsNullOrWhiteSpace(model.ModelId))
            {
                problems.Add("ModelRouting contains a model with an empty ModelId.");
                continue;
            }

            if (IsAutoOrImplicitModel(model.ModelId))
            {
                problems.Add(
                    $"ModelRouting model '{model.ModelId}' is an automatic or implicit model identifier — " +
                    "model selection must be explicit, deterministic, and fail-closed.");
            }

            if (PlaceholderModelIds.Contains(model.ModelId, StringComparer.OrdinalIgnoreCase))
            {
                problems.Add(
                    $"ModelRouting model '{model.ModelId}' is a placeholder id that no provider can serve — " +
                    "configure a real model id for this deployment's provider.");
            }

            if (string.IsNullOrWhiteSpace(model.Provider))
            {
                problems.Add($"ModelRouting model '{model.ModelId}' has no Provider configured.");
            }
        }

        foreach (var (category, candidates) in CategoryProfiles)
        {
            if (candidates.Count == 0)
            {
                problems.Add($"ModelRouting category '{category}' has no candidate models.");
                continue;
            }

            foreach (var candidate in candidates)
            {
                if (IsAutoOrImplicitModel(candidate))
                {
                    problems.Add(
                        $"ModelRouting category '{category}' references automatic or implicit model '{candidate}' — " +
                        "model selection must be explicit, deterministic, and fail-closed.");
                }

                if (!Models.Any(m => string.Equals(m.ModelId, candidate, StringComparison.OrdinalIgnoreCase)))
                {
                    problems.Add($"ModelRouting category '{category}' references unknown model '{candidate}'.");
                }
            }
        }

        foreach (var (agentType, category) in AgentTypeCategories)
        {
            if (!CategoryProfiles.ContainsKey(category))
            {
                problems.Add(
                    $"ModelRouting maps agent type '{agentType}' to category '{category}', which has no profile.");
            }
        }

        return problems;
    }
}
