using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Orchestration;
using Xunit;

namespace AI.Factory.Application.Tests;

/// <summary>
/// Phase P.1 (suite B) — the strict review transport/parser boundary:
/// startup log + valid framed result, startup log + valid JSON result, valid findings,
/// empty findings, malformed result, missing result, ambiguity, and the rule that a
/// payload is never silently dropped or invented.
/// </summary>
public sealed class ReviewPayloadParserTests
{
    private const string StartupLog =
        """
        [s6-init] making a few final tweaks...
        [s6-rc] starting services...
        s6-rc-init: booting stage 2...
        hermes-agent 0.20.6 ready.
        """;

    private const string ValidFindings =
        """
        [{"severity":"High","category":"Correctness","file":"src/Calc.cs","line":12,"description":"off-by-one","recommendation":"fix bound"}]
        """;

    private static string Framed(string payload) =>
        $"{ReviewPayloadParser.BeginMarker}\n{payload}\n{ReviewPayloadParser.EndMarker}";

    // ---------------- startup log + valid result ----------------

    [Fact]
    public void StartupLogBeforeAValidJsonArray_ExtractsTheFindings()
    {
        var result = ReviewPayloadParser.Parse(StartupLog + "\n" + ValidFindings);

        Assert.Equal(ReviewParseStatus.Parsed, result.Status);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(ReviewFindingSeverity.High, finding.Severity);
        Assert.Equal("off-by-one", finding.Description);
    }

    [Fact]
    public void StartupLogAroundAFencedValidJsonArray_ExtractsTheFindings()
    {
        var result = ReviewPayloadParser.Parse(StartupLog + "\n```json\n" + ValidFindings + "\n```\nbye");

        Assert.Equal(ReviewParseStatus.Parsed, result.Status);
        Assert.Single(result.Findings);
    }

    [Fact]
    public void StartupLogPlusFramedResult_ParsesTheFrame()
    {
        var output = StartupLog + "\n" + Framed(ValidFindings) + "\ntrailing log line";

        var result = ReviewPayloadParser.Parse(output);

        Assert.Equal(ReviewParseStatus.Parsed, result.Status);
        Assert.Single(result.Findings);
    }

    [Fact]
    public void FramedResult_WithCodeFenceInsideTheFrame_IsAccepted()
    {
        var output = Framed("```json\n" + ValidFindings + "\n```");

        var result = ReviewPayloadParser.Parse(output);

        Assert.Equal(ReviewParseStatus.Parsed, result.Status);
        Assert.Single(result.Findings);
    }

    // ---------------- plain payloads ----------------

    [Fact]
    public void ValidFindings_Only_Parses()
    {
        var result = ReviewPayloadParser.Parse(ValidFindings);

        Assert.Equal(ReviewParseStatus.Parsed, result.Status);
        Assert.Single(result.Findings);
    }

    [Fact]
    public void EmptyArray_ParsesToZeroFindings_NotToMissingOrMalformed()
    {
        var result = ReviewPayloadParser.Parse("[]");

        Assert.Equal(ReviewParseStatus.Parsed, result.Status);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public void ObjectWithFindingsArray_IsAccepted()
    {
        var result = ReviewPayloadParser.Parse("""{"findings":""" + ValidFindings + "}");

        Assert.Equal(ReviewParseStatus.Parsed, result.Status);
        Assert.Single(result.Findings);
    }

    // ---------------- malformed / missing ----------------

    [Fact]
    public void NullOutput_IsMissing_NotAnEmptyReview()
    {
        var result = ReviewPayloadParser.Parse(null);

        Assert.Equal(ReviewParseStatus.Missing, result.Status);
        Assert.Empty(result.Findings);
        Assert.NotNull(result.Reason);
    }

    [Fact]
    public void WhitespaceOutput_IsMissing()
    {
        Assert.Equal(ReviewParseStatus.Missing, ReviewPayloadParser.Parse("   \n  ").Status);
    }

    [Fact]
    public void ProseWithoutAnyJson_IsMalformed_NotAnEmptyReview()
    {
        var result = ReviewPayloadParser.Parse("The code looks great, no issues at all!");

        Assert.Equal(ReviewParseStatus.Malformed, result.Status);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public void ArrayWithAnEntryMissingSeverity_IsMalformed_TheEntryIsNeverDropped()
    {
        var payload = """
            [{"category":"Security","description":"something","recommendation":"fix it"}]
            """;

        var result = ReviewPayloadParser.Parse(payload);

        Assert.Equal(ReviewParseStatus.Malformed, result.Status);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public void ArrayWithAnUnparseableSeverity_IsMalformed()
    {
        var payload = """
            [{"severity":"Catastrophic","category":"Security","description":"x","recommendation":"y"}]
            """;

        Assert.Equal(ReviewParseStatus.Malformed, ReviewPayloadParser.Parse(payload).Status);
    }

    [Fact]
    public void ArrayWithANonObjectElement_IsMalformed_TheEntryIsNeverDropped()
    {
        var payload = """
            ["just a string",{"severity":"Low","category":"Security","description":"x","recommendation":"y"}]
            """;

        Assert.Equal(ReviewParseStatus.Malformed, ReviewPayloadParser.Parse(payload).Status);
    }

    [Fact]
    public void TruncatedJson_IsMalformed()
    {
        Assert.Equal(ReviewParseStatus.Malformed, ReviewPayloadParser.Parse("[{\"severity\":\"High\"").Status);
    }

    // ---------------- frame rules ----------------

    [Fact]
    public void FrameWithMalformedJsonInside_FailsOnTheFrame_NeverScansOutsideIt()
    {
        // A valid payload exists OUTSIDE the frame; the frame is authoritative.
        var output = ReviewPayloadParser.BeginMarker + "\nnot json at all\n" + ReviewPayloadParser.EndMarker
            + "\n" + ValidFindings;

        var result = ReviewPayloadParser.Parse(output);

        Assert.Equal(ReviewParseStatus.Malformed, result.Status);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public void EmptyFrame_IsMalformed_NotAnEmptyReview()
    {
        var result = ReviewPayloadParser.Parse(Framed("   "));

        Assert.Equal(ReviewParseStatus.Malformed, result.Status);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public void RepeatedFrameMarkers_AreAmbiguous_AndMalformed()
    {
        var output = Framed(ValidFindings) + "\n" + Framed("[]");

        Assert.Equal(ReviewParseStatus.Malformed, ReviewPayloadParser.Parse(output).Status);
    }

    [Fact]
    public void EndMarkerBeforeBeginMarker_IsMalformed()
    {
        var output = ReviewPayloadParser.EndMarker + "\n[]\n" + ReviewPayloadParser.BeginMarker;

        Assert.Equal(ReviewParseStatus.Malformed, ReviewPayloadParser.Parse(output).Status);
    }

    [Fact]
    public void TwoValidPayloadsOutsideAFrame_AreAmbiguous_AndMalformed()
    {
        var output = StartupLog + "\n" + ValidFindings + "\n" + ValidFindings;

        Assert.Equal(ReviewParseStatus.Malformed, ReviewPayloadParser.Parse(output).Status);
    }

    [Fact]
    public void UnrelatedJsonFromALogIsNotAReview_WhenItDoesNotMatchTheSchema()
    {
        var output = StartupLog + "\n{\"level\":\"info\",\"event\":\"startup\"}\n";

        var result = ReviewPayloadParser.Parse(output);

        Assert.Equal(ReviewParseStatus.Malformed, result.Status);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public void ExtractFramedRegion_ReturnsThePayloadInsideAWellFormedFrame()
    {
        var extracted = ReviewPayloadParser.ExtractFramedRegion(StartupLog + "\n" + Framed(ValidFindings));

        Assert.Equal(ValidFindings, extracted.Trim());
    }

    [Fact]
    public void ExtractFramedRegion_WithoutAFrame_ReturnsTheOriginalOutputUnchanged()
    {
        var extracted = ReviewPayloadParser.ExtractFramedRegion(StartupLog + "\n[]");

        Assert.Equal(StartupLog + "\n[]", extracted);
    }

    [Fact]
    public void ExtractFramedRegion_WithAMalformedFrame_ReturnsTheOriginalOutputForTheParserToJudge()
    {
        var output = ReviewPayloadParser.BeginMarker + ReviewPayloadParser.BeginMarker + "[]";

        Assert.Equal(output, ReviewPayloadParser.ExtractFramedRegion(output));
    }
}
