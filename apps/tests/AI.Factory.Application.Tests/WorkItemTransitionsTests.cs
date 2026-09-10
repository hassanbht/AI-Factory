using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using AI.Factory.Domain.Exceptions;
using Xunit;

namespace AI.Factory.Application.Tests;

/// <summary>Phase 18.2 §6/§15 — Task (WorkItem) lifecycle transition validation.</summary>
public sealed class WorkItemTransitionsTests
{
    [Theory]
    [InlineData(WorkItemStatus.Draft, WorkItemStatus.Ready, true)]
    [InlineData(WorkItemStatus.Ready, WorkItemStatus.Running, true)]
    [InlineData(WorkItemStatus.Ready, WorkItemStatus.Blocked, true)]
    [InlineData(WorkItemStatus.Running, WorkItemStatus.Completed, true)]
    [InlineData(WorkItemStatus.Running, WorkItemStatus.Failed, true)]
    [InlineData(WorkItemStatus.Blocked, WorkItemStatus.Ready, true)]
    [InlineData(WorkItemStatus.Draft, WorkItemStatus.Running, false)]
    [InlineData(WorkItemStatus.Completed, WorkItemStatus.Ready, false)]
    [InlineData(WorkItemStatus.Cancelled, WorkItemStatus.Ready, false)]
    public void IsValid_MatchesExpectedTransition(WorkItemStatus from, WorkItemStatus to, bool expected)
    {
        Assert.Equal(expected, WorkItemTransitions.IsValid(from, to));
    }

    [Fact]
    public void WorkItem_TransitionTo_Valid_UpdatesStatusAndStartedAt()
    {
        var workItem = new WorkItem { Status = WorkItemStatus.Ready.ToString() };

        workItem.TransitionTo(WorkItemStatus.Running);

        Assert.Equal(WorkItemStatus.Running.ToString(), workItem.Status);
        Assert.NotNull(workItem.StartedAtUtc);
    }

    [Fact]
    public void WorkItem_TransitionTo_ToTerminal_SetsCompletedAtUtc()
    {
        var workItem = new WorkItem { Status = WorkItemStatus.Running.ToString() };

        workItem.TransitionTo(WorkItemStatus.Completed);

        Assert.NotNull(workItem.CompletedAtUtc);
    }

    [Fact]
    public void WorkItem_TransitionTo_Invalid_Throws_AndDoesNotMutateStatus()
    {
        var workItem = new WorkItem { Status = WorkItemStatus.Draft.ToString() };

        Assert.Throws<InvalidWorkItemTransitionException>(() => workItem.TransitionTo(WorkItemStatus.Running));
        Assert.Equal(WorkItemStatus.Draft.ToString(), workItem.Status);
    }
}
