using AI.Factory.Application.Orchestration;
using AI.Factory.Domain.Enums;
using Xunit;

namespace AI.Factory.Application.Tests;

public sealed class WorkflowTransitionValidatorTests
{
    private readonly WorkflowTransitionValidator _validator = new();

    [Theory]
    [InlineData(ProjectStatus.Created, ProjectStatus.Analyzing)]
    [InlineData(ProjectStatus.Analyzing, ProjectStatus.Specification)]
    [InlineData(ProjectStatus.Specification, ProjectStatus.Architecture)]
    [InlineData(ProjectStatus.Architecture, ProjectStatus.Planning)]
    [InlineData(ProjectStatus.Planning, ProjectStatus.Implementation)]
    [InlineData(ProjectStatus.Implementation, ProjectStatus.Testing)]
    [InlineData(ProjectStatus.Testing, ProjectStatus.Repairing)]
    [InlineData(ProjectStatus.Repairing, ProjectStatus.Reviewing)]
    [InlineData(ProjectStatus.Reviewing, ProjectStatus.HumanReview)]
    [InlineData(ProjectStatus.HumanReview, ProjectStatus.Completed)]
    public void CanTransition_ReturnsTrue_ForLinearHappyPath(ProjectStatus from, ProjectStatus to)
    {
        Assert.True(_validator.CanTransition(from, to));
    }

    [Theory]
    [InlineData(ProjectStatus.Created, ProjectStatus.Architecture)]
    [InlineData(ProjectStatus.Analyzing, ProjectStatus.Created)]
    [InlineData(ProjectStatus.Testing, ProjectStatus.Analyzing)]
    [InlineData(ProjectStatus.Created, ProjectStatus.Completed)]
    public void CanTransition_ReturnsFalse_ForNonAdjacentOrBackwardMoves(ProjectStatus from, ProjectStatus to)
    {
        Assert.False(_validator.CanTransition(from, to));
    }

    [Theory]
    [InlineData(ProjectStatus.Completed)]
    [InlineData(ProjectStatus.Failed)]
    [InlineData(ProjectStatus.Cancelled)]
    public void CanTransition_ReturnsFalse_FromTerminalState(ProjectStatus terminal)
    {
        Assert.False(_validator.CanTransition(terminal, ProjectStatus.Analyzing));
        Assert.False(_validator.CanTransition(terminal, ProjectStatus.Failed));
        Assert.False(_validator.CanTransition(terminal, ProjectStatus.Cancelled));
    }

    [Theory]
    [InlineData(ProjectStatus.Created)]
    [InlineData(ProjectStatus.Analyzing)]
    [InlineData(ProjectStatus.Testing)]
    [InlineData(ProjectStatus.HumanReview)]
    public void CanTransition_ReturnsTrue_ToCancelled_FromAnyNonTerminalState(ProjectStatus from)
    {
        Assert.True(_validator.CanTransition(from, ProjectStatus.Cancelled));
    }

    [Theory]
    [InlineData(ProjectStatus.Created)]
    [InlineData(ProjectStatus.Analyzing)]
    [InlineData(ProjectStatus.Testing)]
    [InlineData(ProjectStatus.HumanReview)]
    public void CanTransition_ReturnsTrue_ToFailed_FromAnyNonTerminalState(ProjectStatus from)
    {
        Assert.True(_validator.CanTransition(from, ProjectStatus.Failed));
    }

    [Theory]
    [InlineData(ProjectStatus.Created)]
    [InlineData(ProjectStatus.Analyzing)]
    [InlineData(ProjectStatus.Implementation)]
    [InlineData(ProjectStatus.Testing)]
    public void CanTransition_ReturnsTrue_ToHumanReview_FromAnyNonTerminalState(ProjectStatus from)
    {
        // Repair-exhaustion escalation (Build Test Repair Loop) must be able
        // to pause the project for human review from wherever it currently
        // is, not only as the strict next step after Reviewing.
        Assert.True(_validator.CanTransition(from, ProjectStatus.HumanReview));
    }
}
