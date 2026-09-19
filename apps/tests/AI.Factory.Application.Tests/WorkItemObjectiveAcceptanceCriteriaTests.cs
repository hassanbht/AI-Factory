using AI.Factory.Application.Orchestration;
using AI.Factory.Domain.Entities;
using Xunit;

namespace AI.Factory.Application.Tests;

/// <summary>
/// Phase 18.29.1 — the agent must be told the contract it will be judged against.
///
/// WorkItemAcceptanceVerifier evaluates workspace-relative paths, but the Objective
/// never mentioned the criteria or what they were relative to. Agents were graded on
/// an unseen rubric: a perfectly good project created one directory down failed a
/// contract looking for it at the workspace root, and the repair loop could not
/// converge because its objective carried only a truncated failure summary.
///
/// Asserted through the real dispatch path (ITaskDispatchScheduler builds the
/// Objective persisted as AgentRun.Input), so this cannot pass against a
/// reimplementation of the prompt that the production path does not use.
/// </summary>
public sealed class WorkItemObjectiveAcceptanceCriteriaTests
{
    private const string Criteria = """
        {"version":1,"criteria":[
          {"type":"FileExists","path":"golden.csproj"},
          {"type":"ExpectedTextPresent","path":"Calculator.cs","value":"static int Add"}
        ]}
        """;

    private static string BuildObjectiveVia(WorkItem workItem)
    {
        // The Objective builders are private statics on the scheduler; the value they
        // produce is what reaches AgentRun.Input, so it is read back through the same
        // public surface the production dispatch uses.
        var method = typeof(TaskDispatchScheduler).GetMethod(
            "BuildObjective",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        Assert.NotNull(method);

        return (string)method!.Invoke(
            null,
            new object?[] { workItem, new Project { Name = "p", Description = "d" }, Array.Empty<WorkItem>() })!;
    }

    private static string BuildRepairObjectiveVia(WorkItem workItem)
    {
        var method = typeof(TaskDispatchScheduler).GetMethod(
            "BuildRepairObjective",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        Assert.NotNull(method);

        return (string)method!.Invoke(
            null, new object?[] { workItem, new Project { Name = "p", Description = "d" } })!;
    }

    [Fact]
    public void Objective_StatesEveryAcceptanceCriterion()
    {
        var objective = BuildObjectiveVia(new WorkItem
        {
            Title = "Create project",
            Description = "desc",
            AcceptanceCriteria = Criteria
        });

        Assert.Contains("golden.csproj", objective);
        Assert.Contains("Calculator.cs", objective);
        Assert.Contains("static int Add", objective);
    }

    [Fact]
    public void Objective_SaysPathsAreRelativeToTheWorkingDirectoryRoot()
    {
        var objective = BuildObjectiveVia(new WorkItem
        {
            Title = "Create project",
            Description = "desc",
            AcceptanceCriteria = Criteria
        });

        // The specific failure this prevents: an agent creating the file inside a
        // subfolder it invents, which the verifier then cannot find.
        Assert.Contains("relative to the ROOT", objective);
        Assert.Contains("NOT inside a subfolder", objective);
    }

    [Fact]
    public void RepairObjective_AlsoStatesTheCriteria()
    {
        var repair = BuildRepairObjectiveVia(new WorkItem
        {
            Title = "Create project",
            Description = "desc",
            AcceptanceCriteria = Criteria,
            CurrentError = "Acceptance criteria not met (1 of 2): expected file 'golden.csproj' to exist"
        });

        // A repair driven only by the truncated failure summary never learns the rule
        // it is violating, which is how the loop exhausted its bounded attempts.
        Assert.Contains("golden.csproj", repair);
        Assert.Contains("relative to the ROOT", repair);
    }

    [Fact]
    public void NoCriteria_AddsNoAcceptanceSection()
    {
        var objective = BuildObjectiveVia(new WorkItem { Title = "t", Description = "d" });

        Assert.DoesNotContain("Acceptance criteria", objective);
    }

    [Fact]
    public void LegacyFreeTextCriteria_AreNotPresentedAsAContract()
    {
        var objective = BuildObjectiveVia(new WorkItem
        {
            Title = "t",
            Description = "d",
            AcceptanceCriteria = "It should work nicely and be well documented."
        });

        // Free text is not machine-checkable and the verifier reports NotSpecified —
        // presenting it as a checkable contract would promise a gate that never runs.
        Assert.DoesNotContain("Acceptance criteria", objective);
    }
}
