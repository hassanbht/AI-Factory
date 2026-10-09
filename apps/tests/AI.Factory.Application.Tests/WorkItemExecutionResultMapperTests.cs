using AI.Factory.Application.Agents;
using AI.Factory.Application.Orchestration;
using Xunit;

namespace AI.Factory.Application.Tests;

/// <summary>Phase 18.7 §13 "Unit tests" — structural/type validation of the untrusted WorkItem execution result.</summary>
public sealed class WorkItemExecutionResultMapperTests
{
    private static readonly WorkItemExecutionResultMapper Mapper = new();

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
    public void Map_ValidCompletedResult_ReturnsExecutionSucceeded()
    {
        const string data = """{"execution":{"version":1,"status":"completed","summary":"done"}}""";

        var result = Mapper.Map(Succeeded(data));

        Assert.Equal(WorkItemExecutionResultOutcome.ExecutionSucceeded, result.Outcome);
        Assert.Equal("done", result.Summary);
    }

    [Fact]
    public void Map_ValidRepairRequiredResult_ReturnsExecutionRequestedRepair()
    {
        const string data = """{"execution":{"version":1,"status":"repair_required","summary":"needs work"}}""";

        var result = Mapper.Map(Succeeded(data));

        Assert.Equal(WorkItemExecutionResultOutcome.ExecutionRequestedRepair, result.Outcome);
        Assert.Equal("needs work", result.Summary);
    }

    [Fact]
    public void Map_HermesObjectShapedTests_NormalizesAndPreservesRawExecutionReport()
    {
        const string data = """{"execution":{"version":1,"status":"completed","summary":"done","tests":[{"name":"Unit test","result":"passed"}],"artifacts":[],"notes":["note"]}}""";

        var result = Mapper.Map(Succeeded(data));

        Assert.Equal(WorkItemExecutionResultOutcome.ExecutionSucceeded, result.Outcome);
        Assert.Equal("Unit test", Assert.Single(result.Tests!).Name);
        Assert.Equal("passed", Assert.Single(result.Tests!).Result);
        Assert.Empty(result.Artifacts!);
        Assert.Equal("note", Assert.Single(result.Notes!));
        Assert.Contains("\"tests\":[{\"name\":\"Unit test\",\"result\":\"passed\"}]", result.RawExecutionReport);
    }

    [Fact]
    public void Map_LegacyStringShapedTests_RemainsSupported()
    {
        const string data = """{"execution":{"version":1,"status":"completed","tests":["Unit test"]}}""";

        var result = Mapper.Map(Succeeded(data));

        Assert.Equal(new ExecutionTestResult("Unit test", null), Assert.Single(result.Tests!));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("{}")]
    [InlineData("{\"result\":\"passed\"}")]
    [InlineData("{\"name\":\"Unit test\",\"result\":42}")]
    public void Map_MalformedTestEntry_Rejected(string entry)
    {
        var data = "{\"execution\":{\"version\":1,\"status\":\"completed\",\"tests\":[" + entry + "]}}";

        Assert.Throws<WorkItemExecutionResultInvalidException>(() => Mapper.Map(Succeeded(data)));
    }

    [Fact]
    public void Map_UnsupportedExecutionVersion_Rejected()
    {
        const string data = """{"execution":{"version":2,"status":"completed"}}""";
        Assert.Throws<WorkItemExecutionResultInvalidException>(() => Mapper.Map(Succeeded(data)));
    }

    [Fact]
    public void Map_MissingExecutionObject_Rejected()
    {
        const string data = """{"notExecution":{}}""";
        Assert.Throws<WorkItemExecutionResultInvalidException>(() => Mapper.Map(Succeeded(data)));
    }

    [Fact]
    public void Map_MissingStatus_Rejected()
    {
        const string data = """{"execution":{"version":1,"summary":"no status"}}""";
        Assert.Throws<WorkItemExecutionResultInvalidException>(() => Mapper.Map(Succeeded(data)));
    }

    [Fact]
    public void Map_MalformedJson_Rejected()
    {
        Assert.Throws<WorkItemExecutionResultInvalidException>(() => Mapper.Map(Succeeded("{not json")));
    }

    [Fact]
    public void Map_MalformedResultFields_WrongJsonType_Rejected()
    {
        const string data = """{"execution":{"version":"not-a-number","status":"completed"}}""";
        Assert.Throws<WorkItemExecutionResultInvalidException>(() => Mapper.Map(Succeeded(data)));
    }

    [Fact]
    public void Map_MissingDataPayload_Rejected()
    {
        Assert.Throws<WorkItemExecutionResultInvalidException>(() => Mapper.Map(Succeeded(null)));
    }

    [Fact]
    public void Map_InvalidStatusValue_Rejected()
    {
        const string data = """{"execution":{"version":1,"status":"success"}}""";
        Assert.Throws<WorkItemExecutionResultInvalidException>(() => Mapper.Map(Succeeded(data)));
    }

    [Fact]
    public void Map_EmptyExecutionObject_Rejected()
    {
        const string data = """{"execution":{}}""";
        Assert.Throws<WorkItemExecutionResultInvalidException>(() => Mapper.Map(Succeeded(data)));
    }

    [Fact]
    public void Map_NonSucceededStructuredResult_Rejected()
    {
        var failed = new StructuredAgentResult(
            StructuredAgentResult.CurrentVersion, StructuredResultStatus.Failed, null, null, null,
            Array.Empty<string>(), new[] { "boom" }, Array.Empty<string>(), null);

        Assert.Throws<WorkItemExecutionResultInvalidException>(() => Mapper.Map(failed));
    }

    [Fact]
    public void Map_SameInput_ProducesSameOutput_Deterministic()
    {
        const string data = """{"execution":{"version":1,"status":"completed","summary":"done"}}""";

        var first = Mapper.Map(Succeeded(data));
        var second = Mapper.Map(Succeeded(data));

        Assert.Equal(first, second);
    }

    [Fact]
    public void Map_StatusIsCaseInsensitiveAndTrimmed()
    {
        const string data = """{"execution":{"version":1,"status":" COMPLETED "}}""";

        var result = Mapper.Map(Succeeded(data));

        Assert.Equal(WorkItemExecutionResultOutcome.ExecutionSucceeded, result.Outcome);
    }
}
