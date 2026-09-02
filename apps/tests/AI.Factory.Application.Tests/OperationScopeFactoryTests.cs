using AI.Factory.Application.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AI.Factory.Application.Tests;

public sealed class OperationScopeFactoryTests
{
    private static OperationScopeFactory MakeFactory(out OperationContextAccessor accessor, out OperationalMetrics metrics)
    {
        accessor = new OperationContextAccessor();
        metrics = new OperationalMetrics();
        return new OperationScopeFactory(accessor, metrics, NullLogger<OperationScopeFactory>.Instance);
    }

    [Fact]
    public async Task Begin_WithNoCorrelationId_GeneratesANewOne()
    {
        var factory = MakeFactory(out _, out _);

        await using var scope = factory.Begin("Operation");

        Assert.NotEqual(Guid.Empty, scope.Context.CorrelationId);
    }

    [Fact]
    public async Task Begin_NestedWhileParentOpen_InheritsParentCorrelationId_AndLinksParentOperationId()
    {
        var factory = MakeFactory(out _, out _);

        await using var parent = factory.Begin("Parent");
        await using var child = factory.Begin("Child");

        Assert.Equal(parent.Context.CorrelationId, child.Context.CorrelationId);
        Assert.Equal(parent.Context.OperationId, child.Context.ParentOperationId);
        Assert.NotEqual(parent.Context.OperationId, child.Context.OperationId);
    }

    [Fact]
    public async Task Dispose_RestoresParentContext_AsAmbientCurrent()
    {
        var factory = MakeFactory(out var accessor, out _);

        await using (var parent = factory.Begin("Parent"))
        {
            await using (var child = factory.Begin("Child"))
            {
                Assert.Equal(child.Context, accessor.Current);
            }

            Assert.Equal(parent.Context, accessor.Current);
        }

        Assert.Null(accessor.Current);
    }

    [Fact]
    public async Task Begin_RecordsOperationStarted_OnMetrics()
    {
        var factory = MakeFactory(out _, out var metrics);

        await using var scope = factory.Begin("Operation");

        Assert.Equal(1, metrics.GetSnapshot().OperationsStarted);
    }

    [Fact]
    public async Task DisposeWithoutFail_RecordsOperationSucceeded_OnMetrics()
    {
        var factory = MakeFactory(out _, out var metrics);

        await using (factory.Begin("Operation"))
        {
        }

        var snapshot = metrics.GetSnapshot();
        Assert.Equal(1, snapshot.OperationsSucceeded);
        Assert.Equal(0, snapshot.OperationsFailed);
    }

    [Fact]
    public async Task DisposeAfterFail_RecordsOperationFailed_OnMetrics()
    {
        var factory = MakeFactory(out _, out var metrics);

        await using (var scope = factory.Begin("Operation"))
        {
            scope.Fail("something broke");
        }

        var snapshot = metrics.GetSnapshot();
        Assert.Equal(0, snapshot.OperationsSucceeded);
        Assert.Equal(1, snapshot.OperationsFailed);
    }
}
