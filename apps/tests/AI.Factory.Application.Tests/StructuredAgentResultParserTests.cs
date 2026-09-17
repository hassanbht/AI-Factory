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

    // ── Phase 18.23 §2/§9 — candidate selection ─────────────────────────────
    //
    // Everything below is about WHICH balanced object is read, never about
    // repairing one. No test here expects a field to be inferred, defaulted or
    // merged across candidates.

    [Fact]
    public void Parse_SurroundingWhitespace_IsToleratedVerbatim()
    {
        var outcome = _parser.Parse("\r\n\t   {\"version\":1,\"status\":\"Succeeded\",\"summary\":\"ok\"}   \n\n");

        Assert.True(outcome.IsValid);
        Assert.Equal("ok", outcome.Result!.Summary);
    }

    /// <summary>
    /// The exact failure Phase 18.23 §24 traced against a real live Planner
    /// run: an abandoned half-written object, prose, then the finished
    /// envelope. Under the old first-object rule the unclosed prefix swallowed
    /// the rest of the text and the run reported no structured result at all.
    /// </summary>
    [Fact]
    public void Parse_AgentAbandonsAnObject_ThenRewritesIt_ReadsTheFinishedOne()
    {
        var outcome = _parser.Parse(
            """
            {"version":1,"status":"Succeeded","summary":"first attempt","data":{"plan":{"version":1,

            Wait — that plan is incomplete. Let me produce the complete and correct version:

            {"version":1,"status":"Succeeded","summary":"second attempt","data":{"plan":{"version":1,"tasks":[]}}}
            """);

        Assert.True(outcome.IsValid);
        Assert.Equal("second attempt", outcome.Result!.Summary);
    }

    [Fact]
    public void Parse_TwoCompleteEnvelopes_TheLastOneWins()
    {
        var outcome = _parser.Parse(
            """
            {"version":1,"status":"Failed","summary":"draft"}
            Actually, I did finish after all:
            {"version":1,"status":"Succeeded","summary":"final"}
            """);

        Assert.True(outcome.IsValid);
        Assert.Equal(StructuredResultStatus.Succeeded, outcome.Result!.Status);
        Assert.Equal("final", outcome.Result.Summary);
    }

    [Fact]
    public void Parse_NestedObjectsAreNeverMistakenForTheEnvelope()
    {
        // Every inner object closes before the envelope does, so a rule that
        // simply took the newest closed object would pick {"count":3} here.
        var outcome = _parser.Parse(
            """{"version":1,"status":"Succeeded","summary":"ok","data":{"plan":{"count":3}}}""");

        Assert.True(outcome.IsValid);
        Assert.Equal("ok", outcome.Result!.Summary);
        Assert.Contains("\"count\"", outcome.Result.Data);
    }

    [Fact]
    public void Parse_OnlyAnUnfinishedEnvelope_StillFailsClosed()
    {
        // Nothing complete to select: no invented status, no salvaged prefix.
        var outcome = _parser.Parse(
            """
            Here is the plan:
            {"version":1,"status":"Succeeded","data":{"plan":{"version":1,"tasks":[
            """);

        Assert.False(outcome.IsValid);
        Assert.Null(outcome.Result);
    }

    [Fact]
    public void Parse_ProseContainingBracesBeforeTheEnvelope_DoesNotDerailTheScan()
    {
        var outcome = _parser.Parse(
            """
            I considered the shape {planKey, title} and rejected it.
            {"version":1,"status":"Succeeded","summary":"ok"}
            """);

        Assert.True(outcome.IsValid);
        Assert.Equal("ok", outcome.Result!.Summary);
    }

    /// <summary>
    /// Observed live in the Phase 18.23 §11 run: an Implementation agent wrote
    /// "metadata":{"files_changed":[...]}, and the whole envelope — including
    /// the "data" the Factory actually reads — was discarded as Malformed.
    /// metadata is a non-authoritative side channel; a non-string value in it
    /// is kept as raw JSON rather than voiding the result.
    /// </summary>
    [Fact]
    public void Parse_NonStringMetadataValue_IsKeptAsRawJson_NotTreatedAsMalformed()
    {
        var outcome = _parser.Parse(
            """{"version":1,"status":"Succeeded","summary":"ok","data":{"n":1},"metadata":{"files_changed":["a.cs","b.cs"],"turns":3,"run":"x"}}""");

        Assert.True(outcome.IsValid);
        Assert.Equal("""["a.cs","b.cs"]""", outcome.Result!.Metadata!["files_changed"]);
        Assert.Equal("3", outcome.Result.Metadata["turns"]);
        Assert.Equal("x", outcome.Result.Metadata["run"]);
        Assert.Contains("\"n\"", outcome.Result.Data);
    }

    [Fact]
    public void Parse_BraceInsideAQuotedValue_IsNotCountedAsStructure()
    {
        var outcome = _parser.Parse(
            """{"version":1,"status":"Succeeded","summary":"wrote a { and a } into the file"}""");

        Assert.True(outcome.IsValid);
        Assert.Equal("wrote a { and a } into the file", outcome.Result!.Summary);
    }
}
