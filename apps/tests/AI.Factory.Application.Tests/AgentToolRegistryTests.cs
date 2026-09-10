using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AI.Factory.Application.Tests;

public sealed class AgentToolRegistryTests
{
    private static AgentToolContext Context() =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "{}");

    [Fact]
    public void Constructor_RejectsDuplicateToolNames()
    {
        var tools = new IAgentTool[]
        {
            new FakeAgentTool("ReadArtifact", FakeAgentTool.Mode.Success),
            new FakeAgentTool("ReadArtifact", FakeAgentTool.Mode.Success)
        };

        Assert.Throws<InvalidOperationException>(
            () => new AgentToolRegistry(tools, NullLogger<AgentToolRegistry>.Instance));
    }

    [Fact]
    public void GetDescriptors_EnumeratesAllRegisteredTools()
    {
        var registry = new AgentToolRegistry(
            new IAgentTool[]
            {
                new FakeAgentTool("A", FakeAgentTool.Mode.Success),
                new FakeAgentTool("B", FakeAgentTool.Mode.Success)
            },
            NullLogger<AgentToolRegistry>.Instance);

        var descriptors = registry.GetDescriptors();

        Assert.Equal(2, descriptors.Count);
        Assert.Contains(descriptors, x => x.Name == "A");
        Assert.Contains(descriptors, x => x.Name == "B");
    }

    [Fact]
    public void GetDescriptors_ExcludesDisabledTools()
    {
        var registry = new AgentToolRegistry(
            new IAgentTool[]
            {
                new FakeAgentTool("Enabled", FakeAgentTool.Mode.Success, enabled: true),
                new FakeAgentTool("Disabled", FakeAgentTool.Mode.Success, enabled: false)
            },
            NullLogger<AgentToolRegistry>.Instance);

        var descriptors = registry.GetDescriptors();

        Assert.Single(descriptors);
        Assert.Equal("Enabled", descriptors[0].Name);
    }

    [Fact]
    public async Task ExecuteAsync_ResolvesAndInvokesTool_OnSuccess()
    {
        var tool = new FakeAgentTool("Echo", FakeAgentTool.Mode.Success);
        var registry = new AgentToolRegistry(new IAgentTool[] { tool }, NullLogger<AgentToolRegistry>.Instance);

        var result = await registry.ExecuteAsync("Echo", Context());

        Assert.True(result.Success);
        Assert.Equal(1, tool.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsFailureResult_WhenToolReportsFailure()
    {
        var registry = new AgentToolRegistry(
            new IAgentTool[] { new FakeAgentTool("Fails", FakeAgentTool.Mode.Failure) },
            NullLogger<AgentToolRegistry>.Instance);

        var result = await registry.ExecuteAsync("Fails", Context());

        Assert.False(result.Success);
        Assert.Equal("Simulated tool failure.", result.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteAsync_TranslatesThrownException_IntoFailureResult()
    {
        var registry = new AgentToolRegistry(
            new IAgentTool[] { new FakeAgentTool("Throws", FakeAgentTool.Mode.Throw) },
            NullLogger<AgentToolRegistry>.Instance);

        var result = await registry.ExecuteAsync("Throws", Context());

        Assert.False(result.Success);
        Assert.Equal("Simulated tool exception.", result.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteAsync_PropagatesCancellation()
    {
        var registry = new AgentToolRegistry(
            new IAgentTool[] { new FakeAgentTool("Cancels", FakeAgentTool.Mode.Cancel) },
            NullLogger<AgentToolRegistry>.Instance);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => registry.ExecuteAsync("Cancels", Context()));
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsFailure_ForUnknownToolName()
    {
        var registry = new AgentToolRegistry(Array.Empty<IAgentTool>(), NullLogger<AgentToolRegistry>.Instance);

        var result = await registry.ExecuteAsync("Missing", Context());

        Assert.False(result.Success);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsFailure_ForDisabledTool()
    {
        var registry = new AgentToolRegistry(
            new IAgentTool[] { new FakeAgentTool("Disabled", FakeAgentTool.Mode.Success, enabled: false) },
            NullLogger<AgentToolRegistry>.Instance);

        var result = await registry.ExecuteAsync("Disabled", Context());

        Assert.False(result.Success);
    }
}
