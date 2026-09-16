using System.Security.Cryptography;
using System.Text;
using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Orchestration;
using AI.Factory.Application.Exceptions;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace AI.Factory.Application.Planning;

/// <summary>
/// Phase 18.5 — the deterministic core of the Project Planner: validates and
/// normalizes an untrusted candidate plan, then persists it atomically as
/// WorkItems + WorkItemDependencies. Reuses ITaskDependencyResolver for
/// cycle detection (no second DFS) and IDatabaseTransactionManager for
/// atomicity (no custom transaction handling). Never touches
/// IAgentRuntime/Hermes.
/// </summary>
public sealed class ProjectPlanService : IProjectPlanService
{
    /// <summary>The canonical, Factory-enforced priority values — public so callers composing a candidate plan (e.g. Phase 18.6's Planner prompt) can reuse the same list instead of duplicating it.</summary>
    public static readonly IReadOnlyCollection<string> ValidPriorities =
        new[] { "Critical", "High", "Normal", "Low" };

    private readonly IProjectRepository _projects;
    private readonly IWorkItemRepository _workItems;
    private readonly ITaskDependencyResolver _resolver;
    private readonly IAgentDefinitionRepository _agentDefinitions;
    private readonly IDatabaseTransactionManager _transactionManager;
    private readonly ILogger<ProjectPlanService> _logger;

    public ProjectPlanService(
        IProjectRepository projects,
        IWorkItemRepository workItems,
        ITaskDependencyResolver resolver,
        IAgentDefinitionRepository agentDefinitions,
        IDatabaseTransactionManager transactionManager,
        ILogger<ProjectPlanService> logger)
    {
        _projects = projects;
        _workItems = workItems;
        _resolver = resolver;
        _agentDefinitions = agentDefinitions;
        _transactionManager = transactionManager;
        _logger = logger;
    }

    public async Task<ProjectPlanResult> CreatePlanAsync(
        ProjectPlanRequest request, CancellationToken cancellationToken = default)
    {
        var project = await _projects.GetByIdAsync(request.ProjectId, cancellationToken)
            ?? throw new NotFoundException($"Project '{request.ProjectId}' was not found.");

        if (project.Status != ProjectStatus.Planning)
        {
            throw new ConflictException(
                $"Project '{request.ProjectId}' must be in '{ProjectStatus.Planning}' status to create a plan; " +
                $"current status is '{project.Status}'.");
        }

        var normalized = ValidateAndNormalize(request);
        await ValidateAgentDefinitionCompatibilityAsync(normalized, cancellationToken);
        DetectCycles(normalized);

        var newHash = ComputePlanHash(normalized);
        var existing = await _workItems.GetByProjectIdAsync(request.ProjectId, cancellationToken);

        if (existing.Count > 0)
        {
            var existingResult = BuildResultFromPersisted(request.ProjectId, existing);

            if (existingResult is not null && existingResult.PlanHash == newHash)
            {
                _logger.LogInformation(
                    "ProjectPlanIdempotentReturn ProjectId={ProjectId} PlanHash={PlanHash}", request.ProjectId, newHash);
                return existingResult;
            }

            throw new ConflictException(
                $"Project '{request.ProjectId}' already has an execution plan that differs from this request. " +
                "Re-planning with a different task set is not supported in this phase.");
        }

        // Persisted atomically (Phase 18.5 §11) via the existing
        // IDatabaseTransactionManager — no custom transaction handling.
        // The read-before-write idempotency check above handles the
        // common repeated-request case; the (ProjectId, PlanKey) unique
        // index is the last-resort guard against a genuine concurrent
        // double-submission race (never silently duplicates — a raw
        // constraint violation surfaces if that race is ever hit, which
        // the transaction manager already logs and rolls back cleanly).
        return await _transactionManager.ExecuteInTransactionAsync(
            ct => PersistAsync(request.ProjectId, normalized, ct), cancellationToken);
    }

    public async Task<ProjectPlanResult?> GetPlanAsync(
        Guid projectId, CancellationToken cancellationToken = default)
    {
        var items = await _workItems.GetByProjectIdAsync(projectId, cancellationToken);
        return BuildResultFromPersisted(projectId, items);
    }

    // ---------------- Validation + normalization (Phase 18.5 §8/§9) ----------------

    private static IReadOnlyList<PlannedWorkItemInput> ValidateAndNormalize(ProjectPlanRequest request)
    {
        if (request.Tasks.Count == 0)
        {
            throw new ValidationException("A plan must contain at least one task.");
        }

        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var task in request.Tasks)
        {
            if (string.IsNullOrWhiteSpace(task.PlanKey))
            {
                throw new ValidationException("Every planned task must have a non-empty PlanKey.");
            }

            if (string.IsNullOrWhiteSpace(task.Title))
            {
                throw new ValidationException($"Task '{task.PlanKey}' is missing a required Title.");
            }

            if (!Enum.IsDefined(task.Type))
            {
                throw new ValidationException($"Task '{task.PlanKey}' has an invalid WorkItemType.");
            }

            if (!ValidPriorities.Contains(task.Priority, StringComparer.OrdinalIgnoreCase))
            {
                throw new ValidationException(
                    $"Task '{task.PlanKey}' has an invalid Priority '{task.Priority}'. " +
                    $"Expected one of: {string.Join(", ", ValidPriorities)}.");
            }

            if (!seenKeys.Add(task.PlanKey.Trim()))
            {
                throw new ValidationException($"Duplicate task PlanKey '{task.PlanKey}'.");
            }

            // Phase 18.22 §2/§5 — every task in a NEWLY created plan must carry
            // a machine-checkable acceptance contract. Phase 18.21 proved that
            // build/test alone cannot tell "the agent did the work" from "the
            // agent did nothing on an already-green repo"; a plan that never
            // states observable evidence would silently reintroduce exactly
            // that gap. Rejected before any persistence, so a plan is never
            // partially written (§5). Historical WorkItems already in the
            // database are untouched by this — the rule governs plan creation,
            // not rows that predate it (§6).
            var acceptance = AcceptanceCriteriaContract.Parse(task.AcceptanceCriteria);

            if (acceptance.Status != AcceptanceCriteriaParseStatus.Valid)
            {
                throw new ValidationException(
                    $"Task '{task.PlanKey}' has no usable acceptance criteria" +
                    (acceptance.Error is null ? "." : $": {acceptance.Error}") +
                    " Every planned task must declare machine-checkable evidence of completion.");
            }
        }

        foreach (var task in request.Tasks)
        {
            foreach (var dep in task.DependsOn)
            {
                if (string.Equals(dep, task.PlanKey, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ValidationException($"Task '{task.PlanKey}' cannot depend on itself.");
                }

                if (!seenKeys.Contains(dep))
                {
                    throw new ValidationException(
                        $"Task '{task.PlanKey}' depends on unknown task '{dep}'.");
                }
            }
        }

        // Normalize: deterministic task order, deterministic+deduplicated
        // dependency order, canonical Priority casing. No timestamps/GUIDs
        // participate in this ordering (Phase 18.5 §9).
        return request.Tasks
            .Select(t => t with
            {
                PlanKey = t.PlanKey.Trim(),
                Priority = ValidPriorities.First(p => string.Equals(p, t.Priority, StringComparison.OrdinalIgnoreCase)),
                DependsOn = t.DependsOn.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(d => d, StringComparer.Ordinal).ToList()
            })
            .OrderBy(t => t.PlanKey, StringComparer.Ordinal)
            .ToList();
    }

    private async Task ValidateAgentDefinitionCompatibilityAsync(
        IReadOnlyList<PlannedWorkItemInput> normalized, CancellationToken cancellationToken)
    {
        var definitions = await _agentDefinitions.GetAllAsync(cancellationToken);

        foreach (var type in normalized.Select(t => t.Type).Distinct())
        {
            var hasDefinition = definitions.Any(d =>
                d.Enabled && string.Equals(d.AgentType, type.ToString(), StringComparison.OrdinalIgnoreCase));

            if (!hasDefinition)
            {
                throw new ValidationException(
                    $"No enabled AgentDefinition exists for WorkItemType '{type}' — this plan would contain a " +
                    "non-dispatchable task. Register a matching AgentDefinition before planning, or remove that task type.");
            }
        }
    }

    /// <summary>
    /// Reuses ITaskDependencyResolver — the same, already-proven cycle
    /// detection TaskDependencyService and TaskDispatchScheduler rely on —
    /// against a transient, never-persisted WorkItem/WorkItemDependency
    /// graph built purely to run that one algorithm (Phase 18.5 §8).
    /// </summary>
    private void DetectCycles(IReadOnlyList<PlannedWorkItemInput> normalized)
    {
        var keyToGuid = normalized.ToDictionary(t => t.PlanKey, _ => Guid.NewGuid(), StringComparer.OrdinalIgnoreCase);

        var transientItems = normalized.Select(t => new WorkItem
        {
            Id = keyToGuid[t.PlanKey],
            Dependencies = t.DependsOn
                .Select(dep => new WorkItemDependency { WorkItemId = keyToGuid[t.PlanKey], DependsOnWorkItemId = keyToGuid[dep] })
                .ToList()
        }).ToList();

        try
        {
            _resolver.Resolve(transientItems);
        }
        catch (CircularWorkItemDependencyException ex)
        {
            throw new ValidationException($"Plan contains a circular dependency: {ex.Message}");
        }
    }

    private static string ComputePlanHash(IReadOnlyList<PlannedWorkItemInput> normalized)
    {
        var canonical = new StringBuilder();

        foreach (var task in normalized)
        {
            canonical.Append(task.PlanKey).Append('|')
                .Append(task.Title).Append('|')
                .Append(task.Type).Append('|')
                .Append(task.Priority).Append('|')
                .Append(string.Join(",", task.DependsOn)).Append('|')
                // Phase 18.22 — two plans that agree on every task but demand
                // different evidence are different plans, so the idempotency
                // hash has to see the acceptance contract too.
                .Append(task.AcceptanceCriteria)
                .Append('\n');
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()));
        return Convert.ToHexString(bytes);
    }

    // ---------------- Persistence (Phase 18.5 §10/§11) ----------------

    private async Task<ProjectPlanResult> PersistAsync(
        Guid projectId, IReadOnlyList<PlannedWorkItemInput> normalized, CancellationToken cancellationToken)
    {
        var keyToWorkItem = new Dictionary<string, WorkItem>(StringComparer.OrdinalIgnoreCase);
        var now = DateTime.UtcNow;

        foreach (var task in normalized)
        {
            var workItem = new WorkItem
            {
                Id = Guid.NewGuid(),
                ProjectId = projectId,
                Type = task.Type,
                Title = task.Title,
                Description = task.Description ?? string.Empty,
                // All validation completed before this method runs — the
                // plan is either persisted whole (atomically) or not at
                // all, so it is always safe to start every task directly
                // at Ready: a dependent task's own Status was Ready all
                // along, and only becomes scheduler-eligible once its
                // dependencies' readiness allows it (Phase 18.5 §10).
                Status = WorkItemStatus.Ready.ToString(),
                Priority = task.Priority,
                PlanKey = task.PlanKey,
                // Phase 18.22 §2/§28 — the validated contract rides into the
                // pre-existing AcceptanceCriteria column, so IVerificationGate
                // evaluates exactly what the plan committed to. No new column.
                AcceptanceCriteria = task.AcceptanceCriteria,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };

            await _workItems.AddAsync(workItem, cancellationToken);
            keyToWorkItem[task.PlanKey] = workItem;
        }

        foreach (var task in normalized)
        {
            foreach (var dep in task.DependsOn)
            {
                await _workItems.AddDependencyAsync(
                    keyToWorkItem[task.PlanKey].Id, keyToWorkItem[dep].Id, cancellationToken);
            }
        }

        var tasks = normalized.Select(task => new ProjectPlanTaskResult(
            task.PlanKey,
            keyToWorkItem[task.PlanKey].Id,
            task.Title,
            task.Type,
            task.Priority,
            task.DependsOn,
            WorkItemStatus.Ready.ToString(),
            TaskReadinessStatus.Ready)).ToList();

        var hash = ComputePlanHash(normalized);

        _logger.LogInformation(
            "ProjectPlanPersisted ProjectId={ProjectId} TaskCount={TaskCount} PlanHash={PlanHash}",
            projectId, tasks.Count, hash);

        return new ProjectPlanResult(projectId, hash, tasks);
    }

    // ---------------- Reconstruction (idempotency + GET) ----------------

    private ProjectPlanResult? BuildResultFromPersisted(Guid projectId, IReadOnlyList<WorkItem> items)
    {
        var planned = items.Where(x => x.PlanKey is not null).ToList();

        if (planned.Count == 0)
        {
            return null;
        }

        var byId = items.ToDictionary(x => x.Id);
        IReadOnlyDictionary<Guid, TaskReadinessStatus> readiness;

        try
        {
            readiness = _resolver.Resolve(items);
        }
        catch (CircularWorkItemDependencyException)
        {
            // A cycle should be structurally impossible post-validation —
            // if data changed out from under us regardless, readiness is
            // simply unknown rather than throwing out of a read path.
            readiness = items.ToDictionary(x => x.Id, _ => TaskReadinessStatus.WaitingForDependency);
        }

        var normalizedInputs = planned
            .Select(x => new PlannedWorkItemInput(
                x.PlanKey!,
                x.Title,
                x.Description,
                x.Type,
                x.Priority,
                x.Dependencies
                    .Where(d => byId.ContainsKey(d.DependsOnWorkItemId) && byId[d.DependsOnWorkItemId].PlanKey is not null)
                    .Select(d => byId[d.DependsOnWorkItemId].PlanKey!)
                    .OrderBy(k => k, StringComparer.Ordinal)
                    .ToList(),
                // Phase 18.22 — ComputePlanHash now covers the acceptance
                // contract, so reconstruction has to carry it too; otherwise a
                // repeated identical plan would hash differently from what was
                // persisted and be rejected as a conflicting re-plan.
                x.AcceptanceCriteria))
            .OrderBy(t => t.PlanKey, StringComparer.Ordinal)
            .ToList();

        var tasks = normalizedInputs.Select(input =>
        {
            var workItem = planned.First(x => x.PlanKey == input.PlanKey);
            return new ProjectPlanTaskResult(
                input.PlanKey,
                workItem.Id,
                workItem.Title,
                workItem.Type,
                workItem.Priority,
                input.DependsOn,
                workItem.Status,
                readiness.TryGetValue(workItem.Id, out var r) ? r : TaskReadinessStatus.WaitingForDependency);
        }).ToList();

        return new ProjectPlanResult(projectId, ComputePlanHash(normalizedInputs), tasks);
    }
}
