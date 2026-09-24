using AI.Factory.Application.Abstractions.Execution;
using AI.Factory.Application.Execution;
using Xunit;

namespace AI.Factory.Application.Tests.Execution;

public sealed class ExecutionAdapterRegistryTests
{
    [Fact]
    public void RegisteredAdapter_IsFoundByKey_CaseInsensitively()
    {
        var hermes = new FakeExecutionAdapter("Hermes");
        var registry = new ExecutionAdapterRegistry(new[] { hermes });

        Assert.True(registry.TryGet("Hermes", out var exact));
        Assert.Same(hermes, exact);
        Assert.True(registry.TryGet("hermes", out var lower));
        Assert.Same(hermes, lower);
    }

    [Fact]
    public void Keys_AreDeterministic_InOrdinalOrder()
    {
        var registry = new ExecutionAdapterRegistry(new[]
        {
            new FakeExecutionAdapter("Zeta"), new FakeExecutionAdapter("Alpha"), new FakeExecutionAdapter("Mid")
        });

        Assert.Equal(new[] { "Alpha", "Mid", "Zeta" }, registry.Keys);
    }

    [Theory]
    [InlineData("Hermes", "Hermes")]
    [InlineData("Hermes", "HERMES")]
    public void DuplicateKey_IsRejected(string first, string second)
    {
        var ex = Assert.Throws<DuplicateExecutionAdapterException>(() => new ExecutionAdapterRegistry(new[]
        {
            new FakeExecutionAdapter(first), new FakeExecutionAdapter(second)
        }));

        Assert.Contains(second, ex.Message);
    }

    [Theory]
    [InlineData("OpenHands")]
    [InlineData("")]
    [InlineData("   ")]
    public void UnknownOrBlankKey_IsNotFound_AndNeverSubstituted(string key)
    {
        var registry = new ExecutionAdapterRegistry(new[] { new FakeExecutionAdapter("Hermes") });

        Assert.False(registry.TryGet(key, out var adapter));
        Assert.Null(adapter);
    }

    [Fact]
    public void BlankAdapterKey_IsRejectedAtConstruction()
    {
        Assert.Throws<ArgumentException>(() => new ExecutionAdapterRegistry(new[] { new FakeExecutionAdapter(" ") }));
    }

    [Fact]
    public void EmptyRegistry_HasNoKeys()
    {
        var registry = new ExecutionAdapterRegistry(Array.Empty<IExecutionAdapter>());

        Assert.Empty(registry.Keys);
        Assert.False(registry.TryGet("Hermes", out _));
    }
}
