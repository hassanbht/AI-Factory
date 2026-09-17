using AI.Factory.Application.Agents;
using AI.Factory.Application.Planning;
using AI.Factory.Domain.Enums;
using Xunit;

namespace AI.Factory.Application.Tests;

/// <summary>Phase 18.6 §27 "Planner result mapping" — structural/type validation of the untrusted candidate plan.</summary>
public sealed class PlannerResultMapperTests
{
    private static readonly PlannerResultMapper Mapper = new();
    private static readonly Guid ProjectId = Guid.NewGuid();

    private static StructuredAgentResult Succeeded(string? data) => new(
        Version: StructuredAgentResult.CurrentVersion,
        Status: StructuredResultStatus.Succeeded,
        Summary: "summary",
        Output: "output",
        Data: data,
        Warnings: Array.Empty<string>(),
        Errors: Array.Empty<string>(),
        Artifacts: Array.Empty<string>(),
        Metadata: null);

    [Fact]
    public void Map_ValidCandidate_ProducesExpectedProjectPlanRequest()
    {
        const string data = """
            {"plan":{"version":1,"summary":"s","tasks":[
              {"planKey":"a","title":"Task A","description":"d","type":"Implementation","priority":"High","dependsOn":[],"acceptanceCriteria":{"version":1,"criteria":[{"type":"FileExists","path":"README.md"}]}},
              {"planKey":"b","title":"Task B","type":"Test","priority":"Normal","dependsOn":["a"],"acceptanceCriteria":{"version":1,"criteria":[{"type":"FileExists","path":"README.md"}]}}
            ]}}
            """;

        var request = Mapper.Map(ProjectId, Succeeded(data));

        Assert.Equal(ProjectId, request.ProjectId);
        Assert.Equal(2, request.Tasks.Count);
        var a = request.Tasks.Single(t => t.PlanKey == "a");
        Assert.Equal(WorkItemType.Implementation, a.Type);
        Assert.Equal("High", a.Priority);
        var b = request.Tasks.Single(t => t.PlanKey == "b");
        Assert.Equal(WorkItemType.Test, b.Type);
        Assert.Equal(new[] { "a" }, b.DependsOn);
    }

    [Fact]
    public void Map_MissingPlanData_Rejected()
    {
        Assert.Throws<PlannerCandidatePlanException>(() => Mapper.Map(ProjectId, Succeeded(null)));
    }

    [Fact]
    public void Map_MissingPlanObject_Rejected()
    {
        Assert.Throws<PlannerCandidatePlanException>(() => Mapper.Map(ProjectId, Succeeded("""{"notPlan":{}}""")));
    }

    [Fact]
    public void Map_UnsupportedPlanVersion_Rejected()
    {
        const string data = """{"plan":{"version":2,"tasks":[{"planKey":"a","title":"A","type":"Implementation"}]}}""";
        Assert.Throws<PlannerCandidatePlanException>(() => Mapper.Map(ProjectId, Succeeded(data)));
    }

    [Fact]
    public void Map_MalformedTask_MissingTitle_Rejected()
    {
        const string data = """{"plan":{"version":1,"tasks":[{"planKey":"a","type":"Implementation"}]}}""";
        Assert.Throws<PlannerCandidatePlanException>(() => Mapper.Map(ProjectId, Succeeded(data)));
    }

    [Fact]
    public void Map_InvalidWorkItemType_Rejected()
    {
        const string data = """{"plan":{"version":1,"tasks":[{"planKey":"a","title":"A","type":"NotARealType"}]}}""";
        Assert.Throws<PlannerCandidatePlanException>(() => Mapper.Map(ProjectId, Succeeded(data)));
    }

    [Fact]
    public void Map_MalformedDependencyList_WrongJsonType_Rejected()
    {
        const string data = """{"plan":{"version":1,"tasks":[{"planKey":"a","title":"A","type":"Implementation","dependsOn":"not-an-array"}]}}""";
        Assert.Throws<PlannerCandidatePlanException>(() => Mapper.Map(ProjectId, Succeeded(data)));
    }

    [Fact]
    public void Map_EmptyTaskList_Rejected()
    {
        const string data = """{"plan":{"version":1,"tasks":[]}}""";
        Assert.Throws<PlannerCandidatePlanException>(() => Mapper.Map(ProjectId, Succeeded(data)));
    }

    /// <summary>Phase 18.23 §9 — an absent "tasks" is rejected exactly like an empty one; never read as "a plan with nothing to do".</summary>
    [Fact]
    public void Map_MissingTasksField_Rejected()
    {
        const string data = """{"plan":{"version":1,"summary":"s"}}""";
        Assert.Throws<PlannerCandidatePlanException>(() => Mapper.Map(ProjectId, Succeeded(data)));
    }

    /// <summary>
    /// Phase 18.23 §6 — the stage a rejection carries is what makes a failed
    /// Planner run diagnosable, so it is asserted rather than left incidental.
    /// </summary>
    [Fact]
    public void Map_MissingAcceptanceCriteria_IsTaggedAsAnAcceptanceFailure_NotASchemaFailure()
    {
        const string data = """{"plan":{"version":1,"tasks":[{"planKey":"a","title":"A","type":"Implementation"}]}}""";

        var ex = Assert.Throws<PlannerCandidatePlanException>(() => Mapper.Map(ProjectId, Succeeded(data)));

        Assert.Equal(PlannerDiagnosticStage.PlannerAcceptanceValidation, ex.Stage);
    }

    [Fact]
    public void Map_InvalidWorkItemType_IsTaggedAsASchemaFailure()
    {
        const string data = """{"plan":{"version":1,"tasks":[{"planKey":"a","title":"A","type":"Teleportation"}]}}""";

        var ex = Assert.Throws<PlannerCandidatePlanException>(() => Mapper.Map(ProjectId, Succeeded(data)));

        Assert.Equal(PlannerDiagnosticStage.PlannerSchemaValidation, ex.Stage);
    }

    [Fact]
    public void Map_MissingPlanKey_Rejected()
    {
        const string data = """{"plan":{"version":1,"tasks":[{"title":"A","type":"Implementation"}]}}""";
        Assert.Throws<PlannerCandidatePlanException>(() => Mapper.Map(ProjectId, Succeeded(data)));
    }

    [Fact]
    public void Map_NonSucceededStructuredResult_Rejected()
    {
        var failed = new StructuredAgentResult(
            StructuredAgentResult.CurrentVersion, StructuredResultStatus.Failed, null, null, null,
            Array.Empty<string>(), new[] { "boom" }, Array.Empty<string>(), null);

        Assert.Throws<PlannerCandidatePlanException>(() => Mapper.Map(ProjectId, failed));
    }

    [Fact]
    public void Map_DefaultsMissingPriorityToNormal()
    {
        const string data = """{"plan":{"version":1,"tasks":[{"planKey":"a","title":"A","type":"Implementation","acceptanceCriteria":{"version":1,"criteria":[{"type":"FileExists","path":"README.md"}]}}]}}""";

        var request = Mapper.Map(ProjectId, Succeeded(data));

        Assert.Equal("Normal", request.Tasks.Single().Priority);
    }
}
