using AI.Factory.Application.Orchestration;
using Xunit;

namespace AI.Factory.Application.Tests;

public sealed class ExecutionLimitsOptionsTests
{
    [Fact]
    public void Validate_Succeeds_WithDefaultValues()
    {
        var record = Record.Exception(() => new ExecutionLimitsOptions().Validate());
        Assert.Null(record);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_Throws_WhenModelRequestTimeoutSecondsIsNotPositive(int value)
    {
        var options = new ExecutionLimitsOptions { ModelRequestTimeoutSeconds = value };
        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_Throws_WhenAgentExecutionTimeoutMinutesIsNotPositive(int value)
    {
        var options = new ExecutionLimitsOptions { AgentExecutionTimeoutMinutes = value };
        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_Throws_WhenBuildTimeoutMinutesIsNotPositive(int value)
    {
        var options = new ExecutionLimitsOptions { BuildTimeoutMinutes = value };
        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_Throws_WhenTestTimeoutMinutesIsNotPositive(int value)
    {
        var options = new ExecutionLimitsOptions { TestTimeoutMinutes = value };
        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_Throws_WhenMaxRepairAttemptsIsNotPositive(int value)
    {
        var options = new ExecutionLimitsOptions { MaxRepairAttempts = value };
        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_Throws_WhenMaxWorkflowDurationMinutesIsNotPositive(int value)
    {
        var options = new ExecutionLimitsOptions { MaxWorkflowDurationMinutes = value };
        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void ExceedsWorkflowDuration_ReturnsTrue_WhenElapsedExceedsConfiguredLimit()
    {
        var options = new ExecutionLimitsOptions { MaxWorkflowDurationMinutes = 30 };
        Assert.True(options.ExceedsWorkflowDuration(TimeSpan.FromMinutes(31)));
    }

    [Fact]
    public void ExceedsWorkflowDuration_ReturnsFalse_WhenElapsedIsWithinConfiguredLimit()
    {
        var options = new ExecutionLimitsOptions { MaxWorkflowDurationMinutes = 30 };
        Assert.False(options.ExceedsWorkflowDuration(TimeSpan.FromMinutes(5)));
    }
}
