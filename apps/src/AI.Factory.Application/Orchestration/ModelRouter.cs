using AI.Factory.Application.Abstractions;

namespace AI.Factory.Application.Orchestration;

public sealed class ModelRouter : IModelRouter
{
    private readonly ModelRoutingOptions _options;

    public ModelRouter(ModelRoutingOptions options)
    {
        _options = options;
    }

    public ModelSelection SelectModel(ModelRoutingRequest request) => SelectCandidates(request)[0];

    public IReadOnlyList<ModelSelection> SelectCandidates(ModelRoutingRequest request)
    {
        if (!_options.CategoryProfiles.TryGetValue(request.TaskCategory, out var candidateIds)
            || candidateIds.Count == 0)
        {
            throw new NoModelAvailableException(
                $"No model profile configured for task category '{request.TaskCategory}'.");
        }

        if (candidateIds.Any(ModelRoutingOptions.IsAutoOrImplicitModel))
        {
            throw new NoModelAvailableException(
                $"Task category '{request.TaskCategory}' contains automatic or implicit model identifiers which are rejected.");
        }

        var minReasoning = MinReasoningFor(request.Complexity);

        var candidates = candidateIds
            .Where(id => !ModelRoutingOptions.IsAutoOrImplicitModel(id))
            .Select(id => _options.Models.FirstOrDefault(
                m => string.Equals(m.ModelId, id, StringComparison.OrdinalIgnoreCase)))
            .Where(m => m is not null)
            .Select(m => m!)
            .Where(m => !ModelRoutingOptions.IsAutoOrImplicitModel(m.ModelId))
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

        var ordered = request.CostPreference switch
        {
            CostPreference.Cheapest => candidates.OrderBy(m => m.CostTier).ToList(),
            CostPreference.BestQuality => candidates.OrderByDescending(m => m.ReasoningStrength).ToList(),
            // Balanced: respect the category's own configured preference order.
            _ => candidates
        };

        return ordered
            .Select((m, index) => new ModelSelection(
                m.ModelId,
                m.Provider,
                $"{(index == 0 ? "Primary" : $"Fallback #{index}")} for category '{request.TaskCategory}' " +
                $"(complexity={request.Complexity}, costPreference={request.CostPreference})."))
            .ToList();
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
