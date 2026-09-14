using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Exceptions;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;

namespace AI.Factory.Application.Projects;

public sealed class ProjectService
{
    private readonly IProjectRepository _repository;
    private readonly IAuditLogger _auditLogger;

    public ProjectService(IProjectRepository repository, IAuditLogger? auditLogger = null)
    {
        _repository = repository;
        _auditLogger = auditLogger ?? NullAuditLogger.Instance;
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

    public Task<Project?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        _repository.GetByIdAsync(id, cancellationToken);

    public Task<IReadOnlyList<Project>> ListAsync(CancellationToken cancellationToken = default) =>
        _repository.ListAsync(cancellationToken);

    /// <summary>
    /// Phase 18.11 §2/§5 — the generic/manual status-transition mechanism can
    /// no longer produce Completed under any circumstance, regardless of the
    /// caller's current status. HumanReview -> Completed is represented only
    /// as the explicit human action <see cref="ApproveAsync"/>, never as an
    /// arbitrary status assignment through this endpoint.
    /// </summary>
    public async Task<Project> TransitionStatusAsync(
        Guid id, ProjectStatus next, CancellationToken cancellationToken = default)
    {
        if (next == ProjectStatus.Completed)
        {
            throw new ConflictException(
                "Project completion requires explicit human approval — use the project approval action instead of the generic status endpoint.");
        }

        var project = await _repository.GetByIdAsync(id, cancellationToken)
            ?? throw new NotFoundException($"Project '{id}' was not found.");

        project.TransitionTo(next);
        await _repository.SaveChangesAsync(cancellationToken);
        return project;
    }

    /// <summary>
    /// Phase 18.11 §4 — the sole explicit human-approval boundary for the new
    /// Project lifecycle: HumanReview -> Completed. Never accepts an
    /// AgentRun/LLM result as authorization — this method takes no such
    /// input at all. Idempotent: a Project already Completed is returned
    /// as-is rather than re-transitioned or rejected. Any other current
    /// status (including Reviewing, Testing, Implementation, Failed,
    /// Cancelled) is rejected with a ConflictException naming the actual
    /// status, never silently coerced.
    /// </summary>
    public async Task<Project> ApproveAsync(
        Guid id, string? actor = null, CancellationToken cancellationToken = default)
    {
        var project = await _repository.GetByIdAsync(id, cancellationToken)
            ?? throw new NotFoundException($"Project '{id}' was not found.");

        if (project.Status == ProjectStatus.Completed)
        {
            // Repeated approval after success is a safe no-op — never
            // re-transitions, never corrupts state.
            return project;
        }

        if (project.Status != ProjectStatus.HumanReview)
        {
            throw new ConflictException(
                $"Project '{id}' cannot be approved from status '{project.Status}'. Only a Project in HumanReview may be approved.");
        }

        project.TransitionTo(ProjectStatus.Completed);

        try
        {
            await _repository.SaveChangesAsync(cancellationToken);
        }
        catch (ConcurrencyConflictException)
        {
            // Another concurrent approval may have already won this exact
            // race — reload and treat an already-Completed result as the
            // same safe idempotent outcome this caller asked for, rather
            // than surfacing a spurious conflict for a request that, in
            // effect, already succeeded.
            var reloaded = await _repository.GetByIdAsync(id, cancellationToken);
            if (reloaded?.Status == ProjectStatus.Completed)
            {
                return reloaded;
            }

            throw;
        }

        await _auditLogger.LogAsync(
            Guid.NewGuid(),
            AuditEventType.HumanApproval,
            AuditSeverity.Info,
            $"Project '{id}' approved and completed.",
            projectId: id,
            outcome: "Approved",
            actorType: "Human",
            actorId: actor,
            cancellationToken: cancellationToken);

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
