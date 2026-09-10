using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using AI.Factory.Domain.Exceptions;
using Xunit;

namespace AI.Factory.Application.Tests;

/// <summary>Phase 18.2 §4/§15 — Project lifecycle transition validation.</summary>
public sealed class ProjectTransitionsTests
{
    [Theory]
    [InlineData(ProjectStatus.Created, ProjectStatus.Analyzing, true)]
    [InlineData(ProjectStatus.Created, ProjectStatus.Cancelled, true)]
    [InlineData(ProjectStatus.Created, ProjectStatus.Completed, false)]
    [InlineData(ProjectStatus.Analyzing, ProjectStatus.Paused, true)]
    [InlineData(ProjectStatus.Paused, ProjectStatus.Implementation, true)]
    [InlineData(ProjectStatus.Paused, ProjectStatus.Created, false)]
    [InlineData(ProjectStatus.Completed, ProjectStatus.Analyzing, false)]
    [InlineData(ProjectStatus.Cancelled, ProjectStatus.Analyzing, false)]
    public void IsValid_MatchesExpectedTransition(ProjectStatus from, ProjectStatus to, bool expected)
    {
        Assert.Equal(expected, ProjectTransitions.IsValid(from, to));
    }

    [Theory]
    [InlineData(ProjectStatus.Completed, true)]
    [InlineData(ProjectStatus.Failed, true)]
    [InlineData(ProjectStatus.Cancelled, true)]
    [InlineData(ProjectStatus.Created, false)]
    [InlineData(ProjectStatus.Implementation, false)]
    public void IsTerminal_MatchesExpected(ProjectStatus status, bool expected)
    {
        Assert.Equal(expected, ProjectTransitions.IsTerminal(status));
    }

    [Fact]
    public void Project_TransitionTo_Valid_UpdatesStatusAndTimestamp()
    {
        var project = new Project { Status = ProjectStatus.Created, UpdatedAtUtc = DateTime.UtcNow.AddDays(-1) };

        project.TransitionTo(ProjectStatus.Analyzing);

        Assert.Equal(ProjectStatus.Analyzing, project.Status);
        Assert.True(project.UpdatedAtUtc > DateTime.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public void Project_TransitionTo_Invalid_Throws_AndDoesNotMutateStatus()
    {
        var project = new Project { Status = ProjectStatus.Completed };

        Assert.Throws<InvalidProjectTransitionException>(() => project.TransitionTo(ProjectStatus.Analyzing));
        Assert.Equal(ProjectStatus.Completed, project.Status);
    }
}
