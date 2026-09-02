using AI.Factory.Application.Abstractions;

namespace AI.Factory.Application.Orchestration;

public sealed class ModelRouter : IModelRouter
{
    private readonly ModelRoutingOptions _options;

    public ModelRouter(ModelRoutingOptions options)
    {
        _options = options;
    }

    public ModelSelection SelectModel(ModelRoutingRequest request)
    {
        if (!_options.CategoryProfiles.TryGetValue(request.TaskCategory, out var candidateIds)
            || candidateIds.Count == 0)
        {
            throw new NoModelAvailableException(
                $"No model profile configured for task category '{request.TaskCategory}'.");
        }

        var minReasoning = MinReasoningFor(request.Complexity);

        var candidates = candidateIds
            .Select(id => _options.Models.FirstOrDefault(
                m => string.Equals(m.ModelId, id, StringComparison.OrdinalIgnoreCase)))
            .Where(m => m is not null)
            .Select(m => m!)
            .Where(m => m.IsAvailable)
            .Where(m => m.MaxContextTokens >= request.EstimatedContextTokens)
            .Where(m => m.ReasoningStrength >= minReasoning)
            .ToList();

        if (candidates.Count == 0)
        {
            throw new NoModelAvailableException(
                $"No available model satisfies task category '{request.TaskCategory}' " +
                $"with complexity '{request.Complexity}' and context size {request.EstimatedContextTokens}.");
        }

        var chosen = request.CostPreference switch
        {
            CostPreference.Cheapest => candidates.OrderBy(m => m.CostTier).First(),
            CostPreference.BestQuality => candidates.OrderByDescending(m => m.ReasoningStrength).First(),
            // Balanced: respect the category's own configured preference order.
            _ => candidates.First()
        };

        return new ModelSelection(
            chosen.ModelId,
            chosen.Provider,
            $"Selected for category '{request.TaskCategory}' " +
            $"(complexity={request.Complexity}, costPreference={request.CostPreference}).");
    }

    private static int MinReasoningFor(TaskComplexity complexity)
    {
        return complexity switch
        {
            TaskComplexity.Simple => 1,
            TaskComplexity.Standard => 2,
            TaskComplexity.Complex => 4,
            TaskComplexity.Critical => 5,
            _ => 2
        };
    }
}
