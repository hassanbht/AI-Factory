using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Exceptions;
using AI.Factory.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace AI.Factory.Application.Agents;

/// <summary>The application-layer use-case behind the Agent Definitions API — validates, then delegates to IAgentDefinitionRepository. No EF/HTTP concerns here.</summary>
public interface IAgentDefinitionService
{
    Task<AgentDefinition> CreateAsync(CreateAgentDefinitionCommand command, CancellationToken cancellationToken = default);

    Task<AgentDefinition?> GetAsync(Guid agentDefinitionId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AgentDefinition>> ListAsync(CancellationToken cancellationToken = default);
}

public sealed record CreateAgentDefinitionCommand(
    string Name,
    string? Description,
    string AgentType,
    string? SystemPrompt,
    bool Enabled = true);

public sealed class AgentDefinitionService : IAgentDefinitionService
{
    private readonly IAgentDefinitionRepository _repository;
    private readonly ILogger<AgentDefinitionService> _logger;

    public AgentDefinitionService(IAgentDefinitionRepository repository, ILogger<AgentDefinitionService> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public async Task<AgentDefinition> CreateAsync(
        CreateAgentDefinitionCommand command, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command.Name))
        {
            throw new ValidationException("Name is required.");
        }

        if (string.IsNullOrWhiteSpace(command.AgentType))
        {
            throw new ValidationException("AgentType is required.");
        }

        var definition = new AgentDefinition
        {
            Name = command.Name,
            Description = command.Description ?? string.Empty,
            AgentType = command.AgentType,
            SystemPrompt = command.SystemPrompt ?? string.Empty,
            Enabled = command.Enabled
        };

        definition = await _repository.CreateAsync(definition, cancellationToken);

        _logger.LogInformation(
            "AgentDefinitionCreated AgentDefinitionId={AgentDefinitionId} Name={Name}", definition.Id, definition.Name);

        return definition;
    }

    public Task<AgentDefinition?> GetAsync(Guid agentDefinitionId, CancellationToken cancellationToken = default) =>
        _repository.GetByIdAsync(agentDefinitionId, cancellationToken);

    public Task<IReadOnlyList<AgentDefinition>> ListAsync(CancellationToken cancellationToken = default) =>
        _repository.GetAllAsync(cancellationToken);
}
