using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Orchestration;
using AI.Factory.Domain.Enums;
using Xunit;

namespace AI.Factory.Application.Tests;

public sealed class TaskPlanValidatorTests
{
    private static PlannedTask MakeTask(
        Guid id,
        params Guid[] dependencies)
    {
        return new PlannedTask(
            id,
            id.ToString(),
            "description",
            WorkItemType.Implementation,
            "Normal",
            dependencies,
            null,
            TaskComplexity.Standard);
    }

    [Fact]
    public void Validate_DoesNotThrow_ForValidLinearChain()
    {
        var a = MakeTask(Guid.NewGuid());
        var b = MakeTask(Guid.NewGuid(), a.Id);
        var c = MakeTask(Guid.NewGuid(), b.Id);

        var plan = new TaskPlan(Guid.NewGuid(), new[] { a, b, c });

        var exception = Record.Exception(() => TaskPlanValidator.Validate(plan));

        Assert.Null(exception);
    }

    [Fact]
    public void Validate_Throws_WhenPlanIsEmpty()
    {
        var plan = new TaskPlan(Guid.NewGuid(), Array.Empty<PlannedTask>());

        Assert.Throws<InvalidTaskPlanException>(() => TaskPlanValidator.Validate(plan));
    }

    [Fact]
    public void Validate_Throws_ForDuplicateTaskIds()
    {
        var sharedId = Guid.NewGuid();
        var a = MakeTask(sharedId);
        var b = MakeTask(sharedId);

        var plan = new TaskPlan(Guid.NewGuid(), new[] { a, b });

        var exception = Assert.Throws<InvalidTaskPlanException>(() => TaskPlanValidator.Validate(plan));
        Assert.Contains("Duplicate", exception.Message);
    }

    [Fact]
    public void Validate_Throws_ForMissingDependencyReference()
    {
        var a = MakeTask(Guid.NewGuid(), Guid.NewGuid() /* references a task not in the plan */);

        var plan = new TaskPlan(Guid.NewGuid(), new[] { a });

        var exception = Assert.Throws<InvalidTaskPlanException>(() => TaskPlanValidator.Validate(plan));
        Assert.Contains("missing task", exception.Message);
    }

    [Fact]
    public void Validate_Throws_WhenNoRootTaskExists()
    {
        // Every task has a dependency (A<->B) — no task can start immediately.
        var aId = Guid.NewGuid();
        var bId = Guid.NewGuid();

        var a = MakeTask(aId, bId);
        var b = MakeTask(bId, aId);

        var plan = new TaskPlan(Guid.NewGuid(), new[] { a, b });

        var exception = Assert.Throws<InvalidTaskPlanException>(() => TaskPlanValidator.Validate(plan));
        Assert.Contains("root task", exception.Message);
    }

    [Fact]
    public void Validate_Throws_WhenNoTerminalTaskExists()
    {
        // X is a root (no deps) but every task, including X, has something
        // depending on it: Y depends on X and Z; Z depends on Y.
        var x = MakeTask(Guid.NewGuid());
        var z = MakeTask(Guid.NewGuid());
        var y = MakeTask(Guid.NewGuid(), x.Id, z.Id);
        z = z with { Dependencies = new[] { y.Id } };

        var plan = new TaskPlan(Guid.NewGuid(), new[] { x, y, z });

        var exception = Assert.Throws<InvalidTaskPlanException>(() => TaskPlanValidator.Validate(plan));
        Assert.Contains("terminal task", exception.Message);
    }

    [Fact]
    public void Validate_Throws_ForCircularDependency_WhenRootAndTerminalBothExist()
    {
        // Root (no deps, nothing depends on it either) coexists with an
        // unrelated A<->B cycle elsewhere in the same plan.
        var root = MakeTask(Guid.NewGuid());
        var aId = Guid.NewGuid();
        var bId = Guid.NewGuid();
        var a = MakeTask(aId, bId);
        var b = MakeTask(bId, aId);

        var plan = new TaskPlan(Guid.NewGuid(), new[] { root, a, b });

        var exception = Assert.Throws<InvalidTaskPlanException>(() => TaskPlanValidator.Validate(plan));
        Assert.Contains("Circular", exception.Message);
    }

    [Fact]
    public void Validate_DoesNotThrow_ForDiamondShapedPlan()
    {
        var a = MakeTask(Guid.NewGuid());
        var b = MakeTask(Guid.NewGuid(), a.Id);
        var c = MakeTask(Guid.NewGuid(), a.Id);
        var d = MakeTask(Guid.NewGuid(), b.Id, c.Id);

        var plan = new TaskPlan(Guid.NewGuid(), new[] { a, b, c, d });

        var exception = Record.Exception(() => TaskPlanValidator.Validate(plan));

        Assert.Null(exception);
    }
}
