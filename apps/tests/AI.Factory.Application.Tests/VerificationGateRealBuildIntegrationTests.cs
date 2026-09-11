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
    private static string DomainProjectPath =>
        Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../../src/AI.Factory.Domain/AI.Factory.Domain.csproj"));

    [Fact]
    public async Task VerifyAsync_RealDotNetBuildAndTest_ReturnsPassed()
    {
        var processRunner = new SystemProcessRunner();
        var gate = new VerificationGate(
            new DotNetBuildExecutor(processRunner),
            new DotNetTestExecutor(processRunner),
            new ExecutionLimitsOptions { BuildTimeoutMinutes = 2, TestTimeoutMinutes = 2 },
            NullLogger<VerificationGate>.Instance);

        var project = new Project { Id = Guid.NewGuid(), Name = "Domain", Slug = "domain", RootPath = DomainProjectPath };
        var workItem = new WorkItem
        {
            Id = Guid.NewGuid(), ProjectId = project.Id, Title = "T", Description = "d", Status = WorkItemStatus.Testing.ToString()
        };

        var result = await gate.VerifyAsync(project, workItem);

        Assert.Equal(VerificationStatus.Passed, result.Status);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task VerifyAsync_MissingProjectPath_ReturnsFailed()
    {
        var processRunner = new SystemProcessRunner();
        var gate = new VerificationGate(
            new DotNetBuildExecutor(processRunner),
            new DotNetTestExecutor(processRunner),
            new ExecutionLimitsOptions { BuildTimeoutMinutes = 1, TestTimeoutMinutes = 1 },
            NullLogger<VerificationGate>.Instance);

        var project = new Project
        {
            Id = Guid.NewGuid(), Name = "Missing", Slug = "missing",
            RootPath = Path.Combine(Path.GetTempPath(), "definitely-does-not-exist.csproj")
        };
        var workItem = new WorkItem
        {
            Id = Guid.NewGuid(), ProjectId = project.Id, Title = "T", Description = "d", Status = WorkItemStatus.Testing.ToString()
        };

        var result = await gate.VerifyAsync(project, workItem);

        Assert.Equal(VerificationStatus.Failed, result.Status);
    }
}
