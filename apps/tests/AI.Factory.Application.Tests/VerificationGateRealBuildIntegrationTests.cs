using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Orchestration;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using AI.Factory.Infrastructure.Agents;
using AI.Factory.Infrastructure.Validation;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AI.Factory.Application.Tests;

/// <summary>
/// Phase 18.8 §17 — "Real Repository Verification": proves the actual chain
/// VerificationGate -> IBuildExecutor/ITestExecutor -> SystemProcessRunner ->
/// real `dotnet build`/`dotnet test` process -> VerificationResult, against
/// this repo's own already-restored AI.Factory.Domain.csproj (small, no
/// network needed) rather than a throwaway scaffolded project.
/// </summary>
public sealed class VerificationGateRealBuildIntegrationTests
{
    // Phase 18.16 §15 — VerificationGate now resolves the workspace through
    // IWorkspaceResolver first, which requires an existing DIRECTORY (same
    // rule Agent workspace resolution already enforces); Project.RootPath is
    // therefore the Domain project's DIRECTORY, not its .csproj file — dotnet
    // build/test both accept a directory and locate the single project file
    // inside it.
    private static string DomainProjectDirectory =>
        Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../../src/AI.Factory.Domain"));

    [Fact]
    public async Task VerifyAsync_RealDotNetBuildAndTest_ReturnsPassed()
    {
        var processRunner = new SystemProcessRunner();
        var gate = new VerificationGate(
            new DotNetBuildExecutor(processRunner),
            new DotNetTestExecutor(processRunner),
            new ProjectWorkspaceResolver(new AI.Factory.Application.Configuration.AgentWorkspaceOptions()),
            new WorkItemAcceptanceVerifier(NullLogger<WorkItemAcceptanceVerifier>.Instance),
            new ExecutionLimitsOptions { BuildTimeoutMinutes = 2, TestTimeoutMinutes = 2 },
            NullLogger<VerificationGate>.Instance);

        var project = new Project { Id = Guid.NewGuid(), Name = "Domain", Slug = "domain", RootPath = DomainProjectDirectory };
        var workItem = new WorkItem
        {
            Id = Guid.NewGuid(), ProjectId = project.Id, Title = "T", Description = "d", Status = WorkItemStatus.Testing.ToString()
        };

        var result = await gate.VerifyAsync(project, workItem);

        Assert.Equal(VerificationStatus.Passed, result.Status);
        Assert.Equal(0, result.ExitCode);
    }

    /// <summary>
    /// Phase 18.16 §15 — a nonexistent Project workspace now fails
    /// deterministically at the shared IWorkspaceResolver resolution step
    /// (InfrastructureError, no process ever launched), rather than reaching
    /// `dotnet build` and failing there with a nonzero exit — the same
    /// "no silent CWD fallback" rule Agent workspace resolution already
    /// enforces.
    /// </summary>
    [Fact]
    public async Task VerifyAsync_MissingProjectPath_ReturnsInfrastructureError_ViaWorkspaceResolution()
    {
        var processRunner = new SystemProcessRunner();
        var gate = new VerificationGate(
            new DotNetBuildExecutor(processRunner),
            new DotNetTestExecutor(processRunner),
            new ProjectWorkspaceResolver(new AI.Factory.Application.Configuration.AgentWorkspaceOptions()),
            new WorkItemAcceptanceVerifier(NullLogger<WorkItemAcceptanceVerifier>.Instance),
            new ExecutionLimitsOptions { BuildTimeoutMinutes = 1, TestTimeoutMinutes = 1 },
            NullLogger<VerificationGate>.Instance);

        var project = new Project
        {
            Id = Guid.NewGuid(), Name = "Missing", Slug = "missing",
            RootPath = Path.Combine(Path.GetTempPath(), "ai-factory-definitely-does-not-exist-" + Guid.NewGuid().ToString("N"))
        };
        var workItem = new WorkItem
        {
            Id = Guid.NewGuid(), ProjectId = project.Id, Title = "T", Description = "d", Status = WorkItemStatus.Testing.ToString()
        };

        var result = await gate.VerifyAsync(project, workItem);

        Assert.Equal(VerificationStatus.InfrastructureError, result.Status);
    }
}
