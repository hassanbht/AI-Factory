using AI.Factory.Application.Abstractions;

namespace AI.Factory.Application.Orchestration;

/// <summary>
/// Enforces structural determinism on a TaskPlan regardless of how
/// non-deterministic the AI provider that proposed it may be. Pure and
/// stateless — no infra dependency, no LLM involved.
/// </summary>
public static class TaskPlanValidator
{
    private enum VisitState
    {
        Unvisited,
        Visiting,
        Visited
    }

    public static void Validate(TaskPlan plan)
    {
        if (plan.Tasks.Count == 0)
        {
            throw new InvalidTaskPlanException("Task plan must contain at least one task.");
        }

        var ids = new HashSet<Guid>();

        foreach (var task in plan.Tasks)
        {
            if (!ids.Add(task.Id))
            {
                throw new InvalidTaskPlanException($"Duplicate task ID '{task.Id}'.");
            }
        }

        foreach (var task in plan.Tasks)
        {
            foreach (var dependencyId in task.Dependencies)
            {
                if (!ids.Contains(dependencyId))
                {
                    throw new InvalidTaskPlanException(
                        $"Task '{task.Id}' depends on missing task '{dependencyId}'.");
                }
            }
        }

        if (!plan.Tasks.Any(t => t.Dependencies.Count == 0))
        {
            throw new InvalidTaskPlanException(
                "Task plan must contain at least one root task (a task with no dependencies).");
        }

        var dependedOnIds = plan.Tasks.SelectMany(t => t.Dependencies).ToHashSet();

        if (!plan.Tasks.Any(t => !dependedOnIds.Contains(t.Id)))
        {
            throw new InvalidTaskPlanException(
                "Task plan must contain at least one terminal task (a task nothing depends on).");
        }

        DetectCycles(plan.Tasks);
    }

    private static void DetectCycles(IReadOnlyList<PlannedTask> tasks)
    {
        var byId = tasks.ToDictionary(t => t.Id);
        var state = tasks.ToDictionary(t => t.Id, _ => VisitState.Unvisited);

        foreach (var task in tasks)
        {
            if (state[task.Id] == VisitState.Unvisited)
            {
                Visit(task.Id, byId, state);
            }
        }
    }

    private static void Visit(
        Guid id,
        Dictionary<Guid, PlannedTask> byId,
        Dictionary<Guid, VisitState> state)
    {
        state[id] = VisitState.Visiting;

        foreach (var dependencyId in byId[id].Dependencies)
        {
            if (state[dependencyId] == VisitState.Visiting)
            {
                throw new InvalidTaskPlanException(
                    $"Circular dependency detected involving task '{dependencyId}'.");
            }

            if (state[dependencyId] == VisitState.Unvisited)
            {
                Visit(dependencyId, byId, state);
            }
        }

        state[id] = VisitState.Visited;
    }
}
