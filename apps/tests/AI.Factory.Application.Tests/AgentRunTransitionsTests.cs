using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using AI.Factory.Domain.Exceptions;
using Xunit;

namespace AI.Factory.Application.Tests;

public sealed class AgentRunTransitionsTests
{
    [Theory]
    [InlineData(AgentRunStatus.Created, AgentRunStatus.Queued)]
    [InlineData(AgentRunStatus.Queued, AgentRunStatus.Running)]
    [InlineData(AgentRunStatus.Queued, AgentRunStatus.Cancelled)]
    [InlineData(AgentRunStatus.Running, AgentRunStatus.Waiting)]
    [InlineData(AgentRunStatus.Running, AgentRunStatus.Succeeded)]
    [InlineData(AgentRunStatus.Running, AgentRunStatus.Failed)]
    [InlineData(AgentRunStatus.Running, AgentRunStatus.TimedOut)]
    [InlineData(AgentRunStatus.Running, AgentRunStatus.Cancelled)]
    [InlineData(AgentRunStatus.Waiting, AgentRunStatus.Running)]
    [InlineData(AgentRunStatus.Waiting, AgentRunStatus.Failed)]
    [InlineData(AgentRunStatus.Waiting, AgentRunStatus.Cancelled)]
    public void IsValid_ReturnsTrue_ForDocumentedTransitions(AgentRunStatus from, AgentRunStatus to)
    {
        Assert.True(AgentRunTransitions.IsValid(from, to));
    }

    [Theory]
    [InlineData(AgentRunStatus.Created, AgentRunStatus.Running)]
    [InlineData(AgentRunStatus.Created, AgentRunStatus.Succeeded)]
    [InlineData(AgentRunStatus.Queued, AgentRunStatus.Succeeded)]
    [InlineData(AgentRunStatus.Succeeded, AgentRunStatus.Running)]
    [InlineData(AgentRunStatus.Failed, AgentRunStatus.Running)]
    [InlineData(AgentRunStatus.Cancelled, AgentRunStatus.Running)]
    [InlineData(AgentRunStatus.TimedOut, AgentRunStatus.Running)]
    [InlineData(AgentRunStatus.Waiting, AgentRunStatus.Succeeded)]
    public void IsValid_ReturnsFalse_ForUndocumentedTransitions(AgentRunStatus from, AgentRunStatus to)
    {
        Assert.False(AgentRunTransitions.IsValid(from, to));
    }

    [Theory]
    [InlineData(AgentRunStatus.Succeeded)]
    [InlineData(AgentRunStatus.Failed)]
    [InlineData(AgentRunStatus.TimedOut)]
    [InlineData(AgentRunStatus.Cancelled)]
    public void IsTerminal_ReturnsTrue_ForTerminalStatuses(AgentRunStatus status)
    {
        Assert.True(AgentRunTransitions.IsTerminal(status));
    }

    [Theory]
    [InlineData(AgentRunStatus.Created)]
    [InlineData(AgentRunStatus.Queued)]
    [InlineData(AgentRunStatus.Running)]
    [InlineData(AgentRunStatus.Waiting)]
    public void IsTerminal_ReturnsFalse_ForNonTerminalStatuses(AgentRunStatus status)
    {
        Assert.False(AgentRunTransitions.IsTerminal(status));
    }

    [Fact]
    public void TransitionTo_AppliesValidTransition_AndSetsStartedAtUtc()
    {
        var run = new AgentRun { Status = AgentRunStatus.Created };

        run.TransitionTo(AgentRunStatus.Queued);
        run.TransitionTo(AgentRunStatus.Running);

        Assert.Equal(AgentRunStatus.Running, run.Status);
        Assert.NotNull(run.StartedAtUtc);
        Assert.Null(run.CompletedAtUtc);
    }

    [Fact]
    public void TransitionTo_TerminalStatus_SetsCompletedAtUtc()
    {
        var run = new AgentRun { Status = AgentRunStatus.Running, StartedAtUtc = DateTime.UtcNow };

        run.TransitionTo(AgentRunStatus.Succeeded);

        Assert.Equal(AgentRunStatus.Succeeded, run.Status);
        Assert.NotNull(run.CompletedAtUtc);
    }

    [Fact]
    public void TransitionTo_InvalidTransition_ThrowsWithFromAndTo()
    {
        var run = new AgentRun { Status = AgentRunStatus.Created };

        var ex = Assert.Throws<InvalidAgentRunTransitionException>(
            () => run.TransitionTo(AgentRunStatus.Succeeded));

        Assert.Equal(AgentRunStatus.Created, ex.From);
        Assert.Equal(AgentRunStatus.Succeeded, ex.To);
        Assert.Equal(AgentRunStatus.Created, run.Status);
    }

    [Fact]
    public void TransitionTo_FromTerminalStatus_AlwaysThrows()
    {
        var run = new AgentRun { Status = AgentRunStatus.Succeeded };

        Assert.Throws<InvalidAgentRunTransitionException>(
            () => run.TransitionTo(AgentRunStatus.Running));
    }
}
