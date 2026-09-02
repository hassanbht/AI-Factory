using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Exceptions;
using AI.Factory.Application.Orchestration;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using Xunit;

namespace AI.Factory.Application.Tests;

public sealed class WorkflowEngineTests
{
    private sealed class FakeWorkflowStateStore : IWorkflowStateStore
    {
        public ProjectStatus CurrentStatus { get; set; } = ProjectStatus.Created;

        public WorkflowTransition? LastPersisted { get; private set; }

        public Task<ProjectStatus> GetCurrentStatusAsync(
            Guid projectId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CurrentStatus);
        }

        public Task ApplyTransitionAsync(
            WorkflowTransition transition,
            CancellationToken cancellationToken = default)
        {
            CurrentStatus = transition.ToStatus;
            LastPersisted = transition;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task TransitionAsync_PersistsTransition_ForValidMove()
    {
        var store = new FakeWorkflowStateStore { CurrentStatus = ProjectStatus.Created };
        var engine = new WorkflowEngine(store, new WorkflowTransitionValidator());
        var projectId = Guid.NewGuid();

        var result = await engine.TransitionAsync(
            new WorkflowTransitionRequest(projectId, ProjectStatus.Analyzing, "start", "tester"));

        Assert.Equal(ProjectStatus.Created, result.FromStatus);
        Assert.Equal(ProjectStatus.Analyzing, result.ToStatus);
        Assert.Equal(ProjectStatus.Analyzing, store.CurrentStatus);
        Assert.Same(result, store.LastPersisted);
    }

    [Fact]
    public async Task TransitionAsync_Throws_ForInvalidMove()
    {
        var store = new FakeWorkflowStateStore { CurrentStatus = ProjectStatus.Created };
        var engine = new WorkflowEngine(store, new WorkflowTransitionValidator());
        var projectId = Guid.NewGuid();

        await Assert.ThrowsAsync<ConflictException>(() =>
            engine.TransitionAsync(
                new WorkflowTransitionRequest(projectId, ProjectStatus.Completed, "skip ahead", "tester")));

        Assert.Null(store.LastPersisted);
    }

    [Fact]
    public async Task TransitionAsync_Throws_ForCancellationFromTerminalState()
    {
        var store = new FakeWorkflowStateStore { CurrentStatus = ProjectStatus.Completed };
        var engine = new WorkflowEngine(store, new WorkflowTransitionValidator());
        var projectId = Guid.NewGuid();

        await Assert.ThrowsAsync<ConflictException>(() =>
            engine.TransitionAsync(
                new WorkflowTransitionRequest(projectId, ProjectStatus.Cancelled, "too late", "tester")));
    }

    [Fact]
    public async Task TransitionAsync_AllowsFailure_FromAnyNonTerminalState()
    {
        var store = new FakeWorkflowStateStore { CurrentStatus = ProjectStatus.Testing };
        var engine = new WorkflowEngine(store, new WorkflowTransitionValidator());
        var projectId = Guid.NewGuid();

        var result = await engine.TransitionAsync(
            new WorkflowTransitionRequest(projectId, ProjectStatus.Failed, "build broke", "system"));

        Assert.Equal(ProjectStatus.Failed, result.ToStatus);
    }

    [Fact]
    public async Task TransitionAsync_AllowsCancellation_FromAnyNonTerminalState()
    {
        var store = new FakeWorkflowStateStore { CurrentStatus = ProjectStatus.Planning };
        var engine = new WorkflowEngine(store, new WorkflowTransitionValidator());
        var projectId = Guid.NewGuid();

        var result = await engine.TransitionAsync(
            new WorkflowTransitionRequest(projectId, ProjectStatus.Cancelled, "user cancelled", "user"));

        Assert.Equal(ProjectStatus.Cancelled, result.ToStatus);
    }
}
