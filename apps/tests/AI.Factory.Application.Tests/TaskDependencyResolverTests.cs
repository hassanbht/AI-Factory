using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Orchestration;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using Xunit;

namespace AI.Factory.Application.Tests;

public sealed class TaskDependencyResolverTests
{
    private readonly TaskDependencyResolver _resolver = new();

    private static WorkItem MakeItem(
        Guid id,
        string status = "Draft",
        params Guid[] dependsOn)
    {
        var item = new WorkItem
        {
            Id = id,
            ProjectId = Guid.NewGuid(),
            Title = id.ToString(),
            Status = status
        };

        foreach (var dependsOnId in dependsOn)
        {
            item.Dependencies.Add(new WorkItemDependency
            {
                WorkItemId = id,
                DependsOnWorkItemId = dependsOnId
            });
        }

        return item;
    }

    [Fact]
    public void Resolve_LinearChain_OnlyFirstIsReady()
    {
        var a = MakeItem(Guid.NewGuid());
        var b = MakeItem(Guid.NewGuid(), dependsOn: a.Id);
        var c = MakeItem(Guid.NewGuid(), dependsOn: b.Id);

        var result = _resolver.Resolve(new[] { a, b, c });

        Assert.Equal(TaskReadinessStatus.Ready, result[a.Id]);
        Assert.Equal(TaskReadinessStatus.WaitingForDependency, result[b.Id]);
        Assert.Equal(TaskReadinessStatus.WaitingForDependency, result[c.Id]);
    }

    [Fact]
    public void Resolve_RepairingTarget_DependentStaysWaiting()
    {
        // Phase 18.9 §8 — a WorkItem undergoing repair (Repairing) must not
        // satisfy a dependency either; only Completed may.
        var aId = Guid.NewGuid();
        var a = MakeItem(aId, status: "Repairing");
        var b = MakeItem(Guid.NewGuid(), dependsOn: aId);

        var result = _resolver.Resolve(new[] { a, b });

        Assert.Equal(TaskReadinessStatus.WaitingForDependency, result[b.Id]);
    }

    [Fact]
    public void Resolve_TestingTarget_DependentStaysWaiting()
    {
        // Phase 18.8 §9 — a WorkItem undergoing deterministic verification
        // (Testing) must not satisfy a dependency; only Completed may.
        var aId = Guid.NewGuid();
        var a = MakeItem(aId, status: "Testing");
        var b = MakeItem(Guid.NewGuid(), dependsOn: aId);

        var result = _resolver.Resolve(new[] { a, b });

        Assert.Equal(TaskReadinessStatus.WaitingForDependency, result[b.Id]);
    }

    [Fact]
    public void Resolve_LinearChain_AdvancesAsDependenciesComplete()
    {
        var aId = Guid.NewGuid();
        var bId = Guid.NewGuid();
        var cId = Guid.NewGuid();

        var a = MakeItem(aId, status: "Completed");
        var b = MakeItem(bId, dependsOn: aId);
        var c = MakeItem(cId, dependsOn: bId);

        var result = _resolver.Resolve(new[] { a, b, c });

        Assert.Equal(TaskReadinessStatus.Completed, result[aId]);
        Assert.Equal(TaskReadinessStatus.Ready, result[bId]);
        Assert.Equal(TaskReadinessStatus.WaitingForDependency, result[cId]);
    }

    [Fact]
    public void Resolve_BranchingDependencies_DiamondShape()
    {
        var aId = Guid.NewGuid();
        var bId = Guid.NewGuid();
        var cId = Guid.NewGuid();
        var dId = Guid.NewGuid();

        var a = MakeItem(aId, status: "Completed");
        var b = MakeItem(bId, status: "Completed", dependsOn: aId);
        var c = MakeItem(cId, dependsOn: aId);

        // D depends on both B and C.
        var d = MakeItem(dId);
        d.Dependencies.Add(new WorkItemDependency { WorkItemId = dId, DependsOnWorkItemId = bId });
        d.Dependencies.Add(new WorkItemDependency { WorkItemId = dId, DependsOnWorkItemId = cId });

        var result = _resolver.Resolve(new[] { a, b, c, d });

        Assert.Equal(TaskReadinessStatus.Ready, result[cId]);
        Assert.Equal(TaskReadinessStatus.WaitingForDependency, result[dId]);
    }

    [Fact]
    public void Resolve_MultipleIndependentTasks_AllReady()
    {
        var a = MakeItem(Guid.NewGuid());
        var b = MakeItem(Guid.NewGuid());
        var c = MakeItem(Guid.NewGuid());

        var result = _resolver.Resolve(new[] { a, b, c });

        Assert.Equal(TaskReadinessStatus.Ready, result[a.Id]);
        Assert.Equal(TaskReadinessStatus.Ready, result[b.Id]);
        Assert.Equal(TaskReadinessStatus.Ready, result[c.Id]);
    }

    [Fact]
    public void Resolve_CircularDependency_Throws()
    {
        var aId = Guid.NewGuid();
        var bId = Guid.NewGuid();

        var a = MakeItem(aId, dependsOn: bId);
        var b = MakeItem(bId, dependsOn: aId);

        Assert.Throws<CircularWorkItemDependencyException>(
            () => _resolver.Resolve(new[] { a, b }));
    }

    [Fact]
    public void Resolve_FailedDependency_BlocksDependentTask()
    {
        var aId = Guid.NewGuid();
        var bId = Guid.NewGuid();

        var a = MakeItem(aId, status: "Failed");
        var b = MakeItem(bId, dependsOn: aId);

        var result = _resolver.Resolve(new[] { a, b });

        Assert.Equal(TaskReadinessStatus.Failed, result[aId]);
        Assert.Equal(TaskReadinessStatus.BlockedByFailedDependency, result[bId]);
    }

    [Fact]
    public void Resolve_AlreadyCompletedTask_ReportsCompleted()
    {
        var a = MakeItem(Guid.NewGuid(), status: "Completed");

        var result = _resolver.Resolve(new[] { a });

        Assert.Equal(TaskReadinessStatus.Completed, result[a.Id]);
    }
}
