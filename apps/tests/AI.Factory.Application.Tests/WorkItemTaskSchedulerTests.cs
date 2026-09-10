using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Orchestration;
using AI.Factory.Domain.Entities;
using Xunit;

namespace AI.Factory.Application.Tests;

public sealed class WorkItemTaskSchedulerTests
{
    private sealed class FakeWorkItemRepository : IWorkItemRepository
    {
        private readonly IReadOnlyList<WorkItem> _items;

        public FakeWorkItemRepository(IReadOnlyList<WorkItem> items)
        {
            _items = items;
        }

        public Task<IReadOnlyList<WorkItem>> GetByProjectIdAsync(
            Guid projectId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_items);
        }

        public Task<WorkItem?> GetByIdAsync(
            Guid workItemId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_items.FirstOrDefault(x => x.Id == workItemId));
        }

        public Task UpdateAsync(
            WorkItem workItem,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task AddAsync(WorkItem workItem, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task AddDependencyAsync(Guid workItemId, Guid dependsOnWorkItemId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<bool> RemoveDependencyAsync(Guid workItemId, Guid dependsOnWorkItemId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<bool> DependencyExistsAsync(Guid workItemId, Guid dependsOnWorkItemId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }

    private static WorkItem MakeItem(
        string status = "Draft",
        string priority = "Normal",
        DateTime? createdAtUtc = null)
    {
        return new WorkItem
        {
            Id = Guid.NewGuid(),
            ProjectId = Guid.NewGuid(),
            Title = "task",
            Status = status,
            Priority = priority,
            CreatedAtUtc = createdAtUtc ?? DateTime.UtcNow
        };
    }

    private static WorkItemTaskScheduler MakeScheduler(params WorkItem[] items)
    {
        return new WorkItemTaskScheduler(
            new FakeWorkItemRepository(items),
            new TaskDependencyResolver());
    }

    [Fact]
    public async Task GetNextReadyWorkItemAsync_ReturnsHigherPriorityFirst()
    {
        var low = MakeItem(priority: "Low", createdAtUtc: DateTime.UtcNow.AddMinutes(-5));
        var high = MakeItem(priority: "High", createdAtUtc: DateTime.UtcNow);

        var scheduler = MakeScheduler(low, high);

        var next = await scheduler.GetNextReadyWorkItemAsync(Guid.NewGuid());

        Assert.Equal(high.Id, next!.Id);
    }

    [Fact]
    public async Task GetNextReadyWorkItemAsync_ReturnsNull_WhenAnotherItemIsRunning()
    {
        var running = MakeItem(status: "Running");
        var ready = MakeItem(status: "Draft");

        var scheduler = MakeScheduler(running, ready);

        var next = await scheduler.GetNextReadyWorkItemAsync(Guid.NewGuid());

        Assert.Null(next);
    }

    [Fact]
    public async Task GetNextReadyWorkItemAsync_ReturnsNull_WhenAllCompleted()
    {
        var a = MakeItem(status: "Completed");
        var b = MakeItem(status: "Completed");

        var scheduler = MakeScheduler(a, b);

        var next = await scheduler.GetNextReadyWorkItemAsync(Guid.NewGuid());

        Assert.Null(next);
    }

    [Fact]
    public async Task GetNextReadyWorkItemAsync_ReturnsRetryableTask_ResetToDraft()
    {
        // Simulates the state after ITaskStateManager.FailAsync reset a
        // retryable task back to Draft — the scheduler must pick it up again.
        var retryable = MakeItem(status: "Draft");

        var scheduler = MakeScheduler(retryable);

        var next = await scheduler.GetNextReadyWorkItemAsync(Guid.NewGuid());

        Assert.Equal(retryable.Id, next!.Id);
    }
}
