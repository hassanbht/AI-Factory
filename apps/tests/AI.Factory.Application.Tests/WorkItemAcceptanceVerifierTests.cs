using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Orchestration;
using AI.Factory.Domain.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AI.Factory.Application.Tests;

/// <summary>
/// Phase 18.21 §24 — deterministic acceptance-criteria evaluation against a
/// real temp workspace (the verifier's whole job is reading real files, so
/// faking the filesystem would test nothing).
/// </summary>
public sealed class WorkItemAcceptanceVerifierTests : IDisposable
{
    private readonly string _workspace;

    public WorkItemAcceptanceVerifierTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "acceptance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_workspace, "src"));
        File.WriteAllText(Path.Combine(_workspace, "src", "Marker.cs"), "public const string Value = \"MARKER_AFTER\";");
        File.WriteAllText(Path.Combine(_workspace, "STATUS.md"), "READY\n");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_workspace, recursive: true);
        }
        catch
        {
            // best-effort temp cleanup
        }
    }

    private static WorkItemAcceptanceVerifier Verifier() =>
        new(NullLogger<WorkItemAcceptanceVerifier>.Instance);

    private static WorkItem WorkItemWith(string? criteria) =>
        new() { Id = Guid.NewGuid(), Title = "T", Description = "d", AcceptanceCriteria = criteria };

    private AcceptanceVerificationResult Verify(string? criteria) =>
        Verifier().Verify(WorkItemWith(criteria), _workspace);

    [Fact]
    public void Verify_NoCriteria_IsNotSpecified()
    {
        Assert.Equal(AcceptanceVerificationStatus.NotSpecified, Verify(null).Status);
        Assert.Equal(AcceptanceVerificationStatus.NotSpecified, Verify("   ").Status);
    }

    [Fact]
    public void Verify_LegacyFreeTextCriteria_IsNotSpecified_NotInvalid()
    {
        // §9 — historical WorkItems already carry human prose in this column.
        // It is not machine-checkable, but it must not fail them either.
        var result = Verify("The greeting endpoint should return a friendly message.");

        Assert.Equal(AcceptanceVerificationStatus.NotSpecified, result.Status);
        Assert.Equal(0, result.EvaluatedCriteriaCount);
    }

    [Fact]
    public void Verify_ExpectedTextPresent_Passes_WhenTextIsThere()
    {
        var result = Verify("""
            {"version":1,"criteria":[{"type":"ExpectedTextPresent","path":"src/Marker.cs","value":"MARKER_AFTER"}]}
            """);

        Assert.Equal(AcceptanceVerificationStatus.Passed, result.Status);
        Assert.Equal(1, result.EvaluatedCriteriaCount);
    }

    [Fact]
    public void Verify_ExpectedTextPresent_Fails_WhenTextIsMissing()
    {
        var result = Verify("""
            {"version":1,"criteria":[{"type":"ExpectedTextPresent","path":"src/Marker.cs","value":"NEVER_WRITTEN"}]}
            """);

        Assert.Equal(AcceptanceVerificationStatus.Failed, result.Status);
        Assert.Contains("NEVER_WRITTEN", result.Summary);
        Assert.Contains("src/Marker.cs", result.Summary);
    }

    [Fact]
    public void Verify_ExpectedTextAbsent_Fails_WhenOldTextStillPresent()
    {
        // The exact no-op shape from Phase 18.20: the agent was asked to
        // replace a marker and left the old one in place.
        File.WriteAllText(Path.Combine(_workspace, "src", "Marker.cs"), "public const string Value = \"MARKER_BEFORE\";");

        var result = Verify("""
            {"version":1,"criteria":[
              {"type":"ExpectedTextPresent","path":"src/Marker.cs","value":"MARKER_AFTER"},
              {"type":"ExpectedTextAbsent","path":"src/Marker.cs","value":"MARKER_BEFORE"}
            ]}
            """);

        Assert.Equal(AcceptanceVerificationStatus.Failed, result.Status);
        Assert.Equal(2, result.EvaluatedCriteriaCount);
        Assert.Contains("2 of 2", result.Summary);
    }

    [Fact]
    public void Verify_MultipleCriteria_AllMustHold()
    {
        var result = Verify("""
            {"version":1,"criteria":[
              {"type":"FileExists","path":"STATUS.md"},
              {"type":"ExpectedFileContent","path":"STATUS.md","value":"READY"},
              {"type":"ExpectedTextPresent","path":"src/Marker.cs","value":"MARKER_AFTER"},
              {"type":"BuildPasses"},
              {"type":"TestsPass"}
            ]}
            """);

        Assert.Equal(AcceptanceVerificationStatus.Passed, result.Status);
        Assert.Equal(5, result.EvaluatedCriteriaCount);
    }

    [Fact]
    public void Verify_ExpectedFileContent_IgnoresLineEndingAndSurroundingWhitespace()
    {
        File.WriteAllText(Path.Combine(_workspace, "STATUS.md"), "  READY\r\n");

        var result = Verify("""
            {"version":1,"criteria":[{"type":"ExpectedFileContent","path":"STATUS.md","value":"READY"}]}
            """);

        Assert.Equal(AcceptanceVerificationStatus.Passed, result.Status);
    }

    [Fact]
    public void Verify_FileExists_Fails_WhenAgentCreatedNothing()
    {
        var result = Verify("""
            {"version":1,"criteria":[{"type":"FileExists","path":"NOT_CREATED.md"}]}
            """);

        Assert.Equal(AcceptanceVerificationStatus.Failed, result.Status);
        Assert.Contains("NOT_CREATED.md", result.Summary);
    }

    [Fact]
    public void Verify_FileAbsent_Fails_WhenFileStillThere()
    {
        var result = Verify("""
            {"version":1,"criteria":[{"type":"FileAbsent","path":"STATUS.md"}]}
            """);

        Assert.Equal(AcceptanceVerificationStatus.Failed, result.Status);
    }

    [Fact]
    public void Verify_MalformedJson_IsContractInvalid_NotPassed()
    {
        var result = Verify("""{"version":1,"criteria":[{"type":"FileExists",}]}""");

        Assert.Equal(AcceptanceVerificationStatus.ContractInvalid, result.Status);
    }

    [Fact]
    public void Verify_UnsupportedCriterionType_IsContractInvalid()
    {
        var result = Verify("""
            {"version":1,"criteria":[{"type":"RunArbitraryShellCommand","path":"x","value":"rm -rf /"}]}
            """);

        Assert.Equal(AcceptanceVerificationStatus.ContractInvalid, result.Status);
        Assert.Contains("RunArbitraryShellCommand", result.Summary);
    }

    [Fact]
    public void Verify_UnsupportedVersion_IsContractInvalid()
    {
        var result = Verify("""
            {"version":99,"criteria":[{"type":"FileExists","path":"STATUS.md"}]}
            """);

        Assert.Equal(AcceptanceVerificationStatus.ContractInvalid, result.Status);
    }

    [Fact]
    public void Verify_EmptyCriteriaList_IsContractInvalid()
    {
        Assert.Equal(AcceptanceVerificationStatus.ContractInvalid, Verify("""{"version":1,"criteria":[]}""").Status);
    }

    [Fact]
    public void Verify_MissingRequiredFields_IsContractInvalid()
    {
        Assert.Equal(
            AcceptanceVerificationStatus.ContractInvalid,
            Verify("""{"version":1,"criteria":[{"type":"FileExists"}]}""").Status);

        Assert.Equal(
            AcceptanceVerificationStatus.ContractInvalid,
            Verify("""{"version":1,"criteria":[{"type":"ExpectedTextPresent","path":"STATUS.md"}]}""").Status);
    }

    [Theory]
    [InlineData("../../../etc/passwd")]
    [InlineData("..\\..\\windows\\system32\\drivers\\etc\\hosts")]
    public void Verify_PathEscapingTheWorkspace_IsRefused(string path)
    {
        // The contract is untrusted input: a criterion must never be able to
        // read outside the Project workspace, however it was authored.
        var escaped = path.Replace("\\", "\\\\", StringComparison.Ordinal);
        var result = Verify($$"""
            {"version":1,"criteria":[{"type":"FileExists","path":"{{escaped}}"}]}
            """);

        Assert.Equal(AcceptanceVerificationStatus.ContractInvalid, result.Status);
    }

    [Fact]
    public void Verify_IsDeterministic_SameInputSameOutcome()
    {
        const string criteria = """
            {"version":1,"criteria":[{"type":"ExpectedTextPresent","path":"src/Marker.cs","value":"MARKER_AFTER"}]}
            """;

        var first = Verify(criteria);
        var second = Verify(criteria);
        var third = Verify(criteria);

        Assert.Equal(first.Status, second.Status);
        Assert.Equal(second.Status, third.Status);
        Assert.Equal(first.Summary, third.Summary);
    }
}
