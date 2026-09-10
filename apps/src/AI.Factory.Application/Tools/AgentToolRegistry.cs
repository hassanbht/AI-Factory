using AI.Factory.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace AI.Factory.Application.Tools;

/// <summary>
/// In-memory <see cref="IAgentToolRegistry"/> built once from the DI-registered
/// <see cref="IAgentTool"/> collection. Rejects duplicate names deterministically
/// at construction time rather than silently picking one.
/// </summary>
public sealed class AgentToolRegistry : IAgentToolRegistry
{
    private readonly IReadOnlyDictionary<string, IAgentTool> _tools;
    private readonly ILogger<AgentToolRegistry> _logger;

    public AgentToolRegistry(IEnumerable<IAgentTool> tools, ILogger<AgentToolRegistry> logger)
    {
        _logger = logger;

        var map = new Dictionary<string, IAgentTool>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in tools)
        {
            if (!map.TryAdd(tool.Name, tool))
            {
                throw new InvalidOperationException($"Duplicate agent tool name '{tool.Name}'.");
            }
        }

        _tools = map;
    }

    public IReadOnlyList<AgentToolDescriptor> GetDescriptors()
    {
        return _tools.Values
            .Where(x => x.Enabled)
            .Select(x => new AgentToolDescriptor(x.Name, x.Description, x.InputSchema, x.Enabled))
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<AgentToolResult> ExecuteAsync(
        string toolName,
        AgentToolContext context,
        CancellationToken cancellationToken = default)
    {
        if (!_tools.TryGetValue(toolName, out var tool) || !tool.Enabled)
        {
            _logger.LogWarning(
                "ToolExecutionFailed: unknown or disabled tool {ToolName} for AgentRun {AgentRunId}",
                toolName, context.AgentRunId);

            return new AgentToolResult(false, null, $"Tool '{toolName}' is not available.", 0);
        }

        var startedAt = DateTime.UtcNow;
        _logger.LogInformation(
            "ToolExecutionStarted: {ToolName} for AgentRun {AgentRunId}", toolName, context.AgentRunId);

        try
        {
            var result = await tool.ExecuteAsync(context, cancellationToken);

            _logger.LogInformation(
                "ToolExecutionCompleted: {ToolName} for AgentRun {AgentRunId}, Success={Success}",
                toolName, context.AgentRunId, result.Success);

            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var durationMs = (long)(DateTime.UtcNow - startedAt).TotalMilliseconds;

            _logger.LogError(
                ex, "ToolExecutionFailed: {ToolName} for AgentRun {AgentRunId}", toolName, context.AgentRunId);

            return new AgentToolResult(false, null, ex.Message, durationMs);
        }
    }
}
