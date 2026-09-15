using AI.Factory.Application.Configuration;
using Xunit;

namespace AI.Factory.Application.Tests;

/// <summary>
/// Phase 18.16 §2/B7 — AgentWorkspaceOptions.Validate() fails fast at startup
/// on a malformed configured ProjectsBaseDirectory, matching the same
/// options-validation convention as ExecutionLimitsOptions/ShutdownOptions
/// (Program.cs calls Validate() right after binding).
/// </summary>
public sealed class AgentWorkspaceOptionsTests
{
    [Fact]
    public void Validate_Succeeds_WithDefaultValues()
    {
        var record = Record.Exception(() => new AgentWorkspaceOptions().Validate());
        Assert.Null(record);
    }

    [Fact]
    public void Validate_Succeeds_WithProjectsBaseDirectoryUnset()
    {
        var options = new AgentWorkspaceOptions { ProjectsBaseDirectory = null };
        var record = Record.Exception(options.Validate);
        Assert.Null(record);
    }

    [Fact]
    public void Validate_Succeeds_WithValidProjectsBaseDirectory()
    {
        var options = new AgentWorkspaceOptions { ProjectsBaseDirectory = Path.GetTempPath() };
        var record = Record.Exception(options.Validate);
        Assert.Null(record);
    }

    [Fact]
    public void Validate_Throws_WithMalformedProjectsBaseDirectory()
    {
        // A null character is invalid in a path on every supported platform
        // and reliably makes Path.GetFullPath throw ArgumentException.
        var options = new AgentWorkspaceOptions { ProjectsBaseDirectory = "C:\\invalid\0path" };
        Assert.Throws<InvalidOperationException>(options.Validate);
    }
}
