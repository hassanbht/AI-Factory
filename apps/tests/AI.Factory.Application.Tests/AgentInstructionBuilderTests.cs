using AI.Factory.Application.Agents;
using Xunit;

namespace AI.Factory.Application.Tests;

/// <summary>Phase 18.1.8 §6/§10 — the prompt/instruction builder.</summary>
public sealed class AgentInstructionBuilderTests
{
    private readonly AgentInstructionBuilder _builder = new();

    [Fact]
    public void Build_IncludesObjectiveAndOutputContract()
    {
        var instruction = new AgentExecutionInstruction(
            CorrelationId: Guid.NewGuid(),
            Objective: "Summarize the attached document",
            Instructions: Array.Empty<string>(),
            ProjectContext: null,
            Constraints: Array.Empty<string>(),
            ExpectedOutputFormat: "structured-json-v1",
            AllowedCapabilities: Array.Empty<string>(),
            PreviousContext: null);

        var text = _builder.Build(instruction);

        Assert.Contains("Summarize the attached document", text);
        Assert.Contains("\"version\":1", text);
        Assert.Contains("structured-json-v1", text);
    }

    [Fact]
    public void Build_IncludesConstraintsAndCapabilitiesAndContext_WhenProvided()
    {
        var instruction = new AgentExecutionInstruction(
            CorrelationId: Guid.NewGuid(),
            Objective: "Do the thing",
            Instructions: new[] { "Be concise" },
            ProjectContext: "ProjectId: 11111111-1111-1111-1111-111111111111",
            Constraints: new[] { "Do not modify files" },
            ExpectedOutputFormat: "structured-json-v1",
            AllowedCapabilities: new[] { "ReadArtifact" },
            PreviousContext: "- [note] prior run preferred concise answers");

        var text = _builder.Build(instruction);

        Assert.Contains("Be concise", text);
        Assert.Contains("Do not modify files", text);
        Assert.Contains("ReadArtifact", text);
        Assert.Contains("ProjectId: 11111111-1111-1111-1111-111111111111", text);
        Assert.Contains("prior run preferred concise answers", text);
    }

    [Fact]
    public void Build_OmitsEmptySections()
    {
        var instruction = new AgentExecutionInstruction(
            CorrelationId: Guid.NewGuid(),
            Objective: "Do the thing",
            Instructions: Array.Empty<string>(),
            ProjectContext: null,
            Constraints: Array.Empty<string>(),
            ExpectedOutputFormat: "structured-json-v1",
            AllowedCapabilities: Array.Empty<string>(),
            PreviousContext: null);

        var text = _builder.Build(instruction);

        Assert.DoesNotContain("Constraints:", text);
        Assert.DoesNotContain("Available capabilities:", text);
        Assert.DoesNotContain("Project context:", text);
        Assert.DoesNotContain("Relevant prior context:", text);
    }
}
