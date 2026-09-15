using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Configuration;
using AI.Factory.Application.Exceptions;
using AI.Factory.Application.Orchestration;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;

namespace AI.Factory.Application.Projects;

public sealed class ProjectService
{
    private readonly IProjectRepository _repository;
    private readonly IProjectWorkspaceProvisioner _workspaceProvisioner;
    private readonly IAuditLogger _auditLogger;
    private readonly IProjectSourceInitializer? _sourceInitializer;

    public ProjectService(
        IProjectRepository repository,
        IProjectWorkspaceProvisioner? workspaceProvisioner = null,
        IAuditLogger? auditLogger = null,
        IProjectSourceInitializer? sourceInitializer = null)
    {
        _repository = repository;
        _workspaceProvisioner = workspaceProvisioner ?? CreateDefaultWorkspaceProvisioner();
        _auditLogger = auditLogger ?? NullAuditLogger.Instance;
        // Unlike workspaceProvisioner above, no in-Application-layer default
        // is created here: the real implementation depends on IProcessRunner,
        // an Infrastructure-layer concern (Clean Architecture — Application
        // must not depend on Infrastructure). Callers that need
        // InitializeSourceAsync must supply one (DI wires the real
        // Infrastructure implementation in Program.cs; tests supply a fake).
        _sourceInitializer = sourceInitializer;
    }

    /// <summary>
    /// Callers that construct <see cref="ProjectService"/> directly without
    /// going through DI (mainly tests predating Phase 18.15) still get a real,
    /// working provisioner rather than a silent no-op — the same
    /// unset-AllowedRoot trust model <see cref="AgentWorkspaceOptions"/>
    /// already documents as the default for workspace resolution.
    /// </summary>
    private static IProjectWorkspaceProvisioner CreateDefaultWorkspaceProvisioner()
    {
        var options = new AgentWorkspaceOptions();
        return new ProjectWorkspaceProvisioner(
            new ProjectWorkspaceResolver(options), options, NullLogger<ProjectWorkspaceProvisioner>.Instance);
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

        // Phase 18.15 §3/§6 — provision BEFORE persisting: a Project row is
        // never written unless its required workspace could actually be
        // created. This sidesteps needing any DB/filesystem compensation
        // logic entirely, by construction rather than cleanup-after-the-fact.
        var provisioning = _workspaceProvisioner.Provision(project);

        if (!provisioning.Success)
        {
            // IOException classifies as FailureCategory.Infrastructure
            // (transient) via the existing IFailureClassifier, which
            // ApiExceptionMapper already maps to 503 Service Unavailable —
            // the correct "server could not provision required
            // infrastructure" signal, without inventing a new exception type.
            throw new IOException(
                $"Failed to provision workspace for project '{slug}': {provisioning.FailureReason}");
        }

        await _repository.AddAsync(
            project,
            cancellationToken);

        await _repository.SaveChangesAsync(
            cancellationToken);

        return project;
    }

    /// <summary>
    /// Phase 18.15 §17 — reusable operation for future operational repair of
    /// historical Projects whose workspace does not exist on disk. Never
    /// invoked automatically (no startup sweep, no hidden migration); no
    /// public API endpoint exists for it in this phase.
    /// </summary>
    public async Task<ProvisioningResult> EnsureWorkspaceAsync(
        Guid projectId, CancellationToken cancellationToken = default)
    {
        var project = await _repository.GetByIdAsync(projectId, cancellationToken)
            ?? throw new NotFoundException($"Project '{projectId}' was not found.");

        return _workspaceProvisioner.Provision(project);
    }

    /// <summary>
    /// Phase 18.16 — the explicit "source decision" operation (spec §5/§18):
    /// separate from <see cref="CreateAsync"/> because a Git clone/network
    /// operation can fail independently of Project metadata creation, and a
    /// Project must not be considered execution-ready before this succeeds.
    /// <see cref="ProjectStatus.Created"/> means "no source decision made
    /// yet"; a successful call here (GitRepository OR explicit Empty) is the
    /// one thing that advances a Project past Created, via the existing
    /// Created -&gt; Analyzing transition — no new state machine. Rejecting
    /// any call when Status is already past Created covers BOTH "the
    /// decision is final" and "repeated initialization is refused" with one
    /// rule. A failed initialization never persists GitRepository/GitBranch
    /// and never transitions the Project, so it can never be falsely marked
    /// ready (spec §11/§25).
    /// </summary>
    public async Task<Project> InitializeSourceAsync(
        Guid projectId, SourceSpecification source, CancellationToken cancellationToken = default)
    {
        if (_sourceInitializer is null)
        {
            throw new InvalidOperationException(
                $"{nameof(ProjectService)} was constructed without an {nameof(IProjectSourceInitializer)}.");
        }

        var project = await _repository.GetByIdAsync(projectId, cancellationToken)
            ?? throw new NotFoundException($"Project '{projectId}' was not found.");

        if (project.Status != ProjectStatus.Created)
        {
            throw new ConflictException(
                $"Project '{projectId}' source has already been initialized (status '{project.Status}').");
        }

        var result = await _sourceInitializer.InitializeAsync(project, source, cancellationToken);

        if (!result.Success)
        {
            // Phase 18.16 — HTTP status mapping chosen via FailureKind
            // (never by parsing FailureReason text): Validation -> 400,
            // Conflict -> 409 (non-empty workspace), anything else
            // (Infrastructure — git unavailable/clone failure/timeout, or a
            // workspace-resolution failure) -> IOException, mapped by the
            // existing IFailureClassifier/ApiExceptionMapper convention to
            // 503, exactly like ProjectService.CreateAsync's own
            // provisioning-failure mapping.
            var message = $"Source initialization failed for project '{projectId}': {result.FailureReason}";

            throw result.FailureKind switch
            {
                SourceInitializationFailureKind.Validation => new ValidationException(message),
                SourceInitializationFailureKind.Conflict => new ConflictException(message),
                _ => new IOException(message)
            };
        }

        if (source.SourceType == ProjectSourceType.GitRepository)
        {
            project.GitRepository = source.Location;
            project.GitBranch = source.Branch;
        }

        project.TransitionTo(ProjectStatus.Analyzing);
        await _repository.SaveChangesAsync(cancellationToken);

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

    /// <summary>
    /// Phase 18.16 §8/B1 — hardened so no Project name can produce a slug
    /// containing "/", "\", "..", a drive letter, or any other filesystem
    /// separator/traversal character: every character outside the safe
    /// [a-z0-9] charset (this necessarily includes "/", "\", ".", ":",
    /// spaces, and all non-ASCII/Unicode input) collapses to a single "-"
    /// separator, repeated separators collapse further, and leading/trailing
    /// separators are trimmed. A name that normalizes to nothing (e.g. all
    /// punctuation/Unicode) falls back to a deterministic
    /// "project-{sha256-prefix-of-original-name}" segment rather than an
    /// empty or random string, so the same input always regenerates the same
    /// safe slug. Project.RootPath = "projects/{slug}" therefore always
    /// stays a safe, single-segment child path.
    /// </summary>
    private static string GenerateSlug(string value)
    {
        var lowered = value.Trim().ToLowerInvariant();
        var slug = new System.Text.StringBuilder(lowered.Length);
        var lastWasSeparator = false;

        foreach (var ch in lowered)
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                slug.Append(ch);
                lastWasSeparator = false;
            }
            else if (!lastWasSeparator && slug.Length > 0)
            {
                slug.Append('-');
                lastWasSeparator = true;
            }
        }

        if (slug.Length > 0 && slug[^1] == '-')
        {
            slug.Length--;
        }

        if (slug.Length == 0)
        {
            var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
            return "project-" + Convert.ToHexString(hash)[..12].ToLowerInvariant();
        }

        return slug.ToString();
    }
}
