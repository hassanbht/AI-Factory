using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Exceptions;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;

namespace AI.Factory.Application.Projects;

public sealed class ProjectService
{
    private readonly IProjectRepository _repository;

    public ProjectService(IProjectRepository repository)
    {
        _repository = repository;
    }

    public async Task<Project> CreateAsync(
        string name,
        string description,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ValidationException("Project name is required.");
        }

        var slug = GenerateSlug(name);

        var existing = await _repository.GetBySlugAsync(
            slug,
            cancellationToken);

        if (existing is not null)
        {
            throw new ConflictException(
                $"Project '{slug}' already exists.");
        }

        var now = DateTime.UtcNow;

        var project = new Project
        {
            Id = Guid.NewGuid(),
            Name = name,
            Slug = slug,
            Description = description,
            Status = ProjectStatus.Created,
            RootPath = $"projects/{slug}",
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        await _repository.AddAsync(
            project,
            cancellationToken);

        await _repository.SaveChangesAsync(
            cancellationToken);

        return project;
    }

    private static string GenerateSlug(string value)
    {
        return value
            .Trim()
            .ToLowerInvariant()
            .Replace(" ", "-");
    }
}
