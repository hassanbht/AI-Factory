using AI.Factory.Application.Agents;
using Xunit;

namespace AI.Factory.Application.Tests;

/// <summary>Phase 18.1.8 §4/§10 — the structured-result contract validator.</summary>
public sealed class StructuredAgentResultParserTests
{
    private readonly StructuredAgentResultParser _parser = new();

    [Fact]
    public void Parse_ValidSuccessResult_ReturnsValidStructuredResult()
    {
        var outcome = _parser.Parse(
            """{"version":1,"status":"Succeeded","summary":"done","output":"the answer","warnings":[],"errors":[],"artifacts":[]}""");

        Assert.True(outcome.IsValid);
        Assert.NotNull(outcome.Result);
        Assert.Equal(1, outcome.Result!.Version);
        Assert.Equal(StructuredResultStatus.Succeeded, outcome.Result.Status);
        Assert.Equal("done", outcome.Result.Summary);
        Assert.Equal("the answer", outcome.Result.Output);
    }

    [Fact]
    public void Parse_ValidFailureResult_ReturnsValidStructuredResultWithFailedStatus()
    {
        var outcome = _parser.Parse(
            """{"version":1,"status":"Failed","errors":["could not find the file"]}""");

        Assert.True(outcome.IsValid);
        Assert.Equal(StructuredResultStatus.Failed, outcome.Result!.Status);
        Assert.Equal("could not find the file", Assert.Single(outcome.Result.Errors));
    }

    [Fact]
    public void Parse_JsonEmbeddedInProseAndCodeFence_StillExtractsTheObject()
    {
        var outcome = _parser.Parse(
            """
            Sure, here is my result:
            ```json
            {"version":1,"status":"Succeeded","summary":"ok"}
            ```
            Let me know if you need anything else.
            """);

        Assert.True(outcome.IsValid);
        Assert.Equal("ok", outcome.Result!.Summary);
    }

    [Fact]
    public void Parse_DataFieldIsArbitraryJson_PreservedAsRawJsonText()
    {
        var outcome = _parser.Parse(
            """{"version":1,"status":"Succeeded","data":{"nested":{"count":3}}}""");

        Assert.True(outcome.IsValid);
        Assert.Contains("\"count\"", outcome.Result!.Data);
    }

    [Fact]
    public void Parse_NullOrWhitespace_ReturnsMissing()
    {
        var outcome = _parser.Parse(null);

        Assert.False(outcome.IsValid);
        Assert.Equal(StructuredResultParseFailureReason.Missing, outcome.FailureReason);
    }

    [Fact]
    public void Parse_NoJsonObjectAtAll_ReturnsMissing()
    {
        var outcome = _parser.Parse("I completed the task successfully, no issues.");

        Assert.False(outcome.IsValid);
        Assert.Equal(StructuredResultParseFailureReason.Missing, outcome.FailureReason);
    }

    [Fact]
    public void Parse_TruncatedJson_HasNoBalancedObject_ReturnsMissing()
    {
        var outcome = _parser.Parse("""{"version":1,"status":"Succ""");

        Assert.False(outcome.IsValid);
        Assert.Equal(StructuredResultParseFailureReason.Missing, outcome.FailureReason);
    }

    [Fact]
    public void Parse_BalancedButSyntacticallyInvalidJson_ReturnsMalformed()
    {
        var outcome = _parser.Parse("""{"version":1,"status":,}""");

        Assert.False(outcome.IsValid);
        Assert.Equal(StructuredResultParseFailureReason.Malformed, outcome.FailureReason);
    }

    [Fact]
    public void Parse_MissingVersionField_ReturnsMalformed()
    {
        var outcome = _parser.Parse("""{"status":"Succeeded"}""");

        Assert.False(outcome.IsValid);
        Assert.Equal(StructuredResultParseFailureReason.Malformed, outcome.FailureReason);
    }

    [Fact]
    public void Parse_MissingStatusField_ReturnsMalformed()
    {
        var outcome = _parser.Parse("""{"version":1}""");

        Assert.False(outcome.IsValid);
        Assert.Equal(StructuredResultParseFailureReason.Malformed, outcome.FailureReason);
    }

    [Fact]
    public void Parse_UnrecognizedStatusValue_ReturnsMalformed()
    {
        var outcome = _parser.Parse("""{"version":1,"status":"kinda-done"}""");

        Assert.False(outcome.IsValid);
        Assert.Equal(StructuredResultParseFailureReason.Malformed, outcome.FailureReason);
    }

    [Fact]
    public void Parse_StatusInvalid_IsRejectedAsMalformed_AgentsCanNeverSelfReportInvalid()
    {
        var outcome = _parser.Parse("""{"version":1,"status":"Invalid"}""");

        Assert.False(outcome.IsValid);
        Assert.Equal(StructuredResultParseFailureReason.Malformed, outcome.FailureReason);
    }

    [Fact]
    public void Parse_UnsupportedVersion_ReturnsUnsupportedVersion()
    {
        var outcome = _parser.Parse("""{"version":99,"status":"Succeeded"}""");

        Assert.False(outcome.IsValid);
        Assert.Equal(StructuredResultParseFailureReason.UnsupportedVersion, outcome.FailureReason);
    }

    [Fact]
    public void Parse_NeverTrustsJustBecauseItIsValidJson_ArbitraryJsonWithoutContractFields_IsRejected()
    {
        var outcome = _parser.Parse("""{"foo":"bar","baz":123}""");

        Assert.False(outcome.IsValid);
        Assert.Equal(StructuredResultParseFailureReason.Malformed, outcome.FailureReason);
    }
}
