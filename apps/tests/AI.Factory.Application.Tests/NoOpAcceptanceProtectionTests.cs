using System.Text.Json;
using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Agents;
using AI.Factory.Application.Configuration;
using AI.Factory.Application.Orchestration;
using AI.Factory.Application.Projects;
using AI.Factory.Application.Tasks;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using AI.Factory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AI.Factory.Application.Tests;

/// <summary>
/// Phase 18.21 §6/§24 — the no-op agent protection, proven end to end through
/// the real <see cref="VerificationGate"/> and the real
/// <see cref="WorkItemAcceptanceVerifier"/>.
///
/// This is the exact scenario Phase 18.20 measured live and could not catch:
/// the agent changed nothing, the project's own suite was already green, so
/// build/test verification passed and the WorkItem was accepted. Only the
/// build/test executors are faked here (so the test needs no .NET SDK run);
/// everything that makes the accept/reject decision is production code.
/// </summary>
public sealed class NoOpAcceptanceProtectionTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<FactoryDbContext> _options;
    private readonly string _workspace;

    public NoOpAcceptanceProtectionTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<FactoryDbContext>().UseSqlite(_connection).Options;
        using var db = new FactoryDbContext(_options);
        db.Database.EnsureCreated();

        _workspace = Path.Combine(Path.GetTempPath(), "noop-acceptance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_workspace, "src"));

        // The workspace the "agent" left completely untouched.
        File.WriteAllText(
            Path.Combine(_workspace, "src", "Marker.cs"), "public const string Value = \"MARKER_BEFORE\";");
    }

    public void Dispose()
    {
        _connection.Dispose();

        try
        {
            Directory.Delete(_workspace, recursive: true);
        }
        catch
        {
            // best-effort temp cleanup
        }
    }

    private sealed class AlwaysGreenBuildExecutor : IBuildExecutor
    {
        public Task<BuildResult> BuildAsync(BuildRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new BuildResult(true, "build ok", string.Empty, 0));
    }

    private sealed class AlwaysGreenTestExecutor : ITestExecutor
    {
        public Task<TestExecutionResult> TestAsync(TestRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new TestExecutionResult(true, "tests ok", string.Empty, 0));
    }

    private sealed class FixedWorkspaceResolver : IWorkspaceResolver
    {
        private readonly string _path;

        public FixedWorkspaceResolver(string path) => _path = path;

        public WorkspaceResolution Resolve(Project project) => WorkspaceResolution.Valid(_path);
    }

    private sealed class PassThroughCompletionProcessor : IWorkItemCompletionProcessor
    {
        public Task<WorkItemCompletionDecision> ProcessAsync(
            Project project, WorkItem workItem, AgentRun agentRun, VerificationResult verificationResult,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new WorkItemCompletionDecision(WorkItemStatus.Completed, null));
    }

    private VerificationGate RealGate() => new(
        new AlwaysGreenBuildExecutor(),
        new AlwaysGreenTestExecutor(),
        new FixedWorkspaceResolver(_workspace),
        new WorkItemAcceptanceVerifier(NullLogger<WorkItemAcceptanceVerifier>.Instance),
        new ExecutionLimitsOptions { BuildTimeoutMinutes = 1, TestTimeoutMinutes = 1 },
        NullLogger<VerificationGate>.Instance);

    private AgentRunWorkItemSync CreateSync(FactoryDbContext db) => new(
        new WorkItemRepository(db), new ProjectRepository(db), new WorkItemExecutionResultMapper(),
        RealGate(), new PassThroughCompletionProcessor(), NullLogger<AgentRunWorkItemSync>.Instance);

    private async Task<Guid> SeedRunningWorkItemAsync(string? acceptanceCriteria)
    {
        await using var db = new FactoryDbContext(_options);
        var projectRepo = new ProjectRepository(db);
        var project = await new ProjectService(projectRepo).CreateAsync("NoOp Project", "d");
        project.Status = ProjectStatus.Analyzing;
        await projectRepo.SaveChangesAsync();

        var workItems = new WorkItemService(new WorkItemRepository(db), new ProjectRepository(db));
        var item = await workItems.CreateAsync(project.Id, WorkItemType.Implementation, "T", null, "Normal");

        item.AcceptanceCriteria = acceptanceCriteria;
        await new WorkItemRepository(db).UpdateAsync(item);

        await workItems.TransitionStatusAsync(item.Id, WorkItemStatus.Ready);
        await workItems.TransitionStatusAsync(item.Id, WorkItemStatus.Running);
        return item.Id;
    }

    private static AgentRun SucceededRun(Guid workItemId)
    {
        // A perfectly well-formed, self-declared "I completed it" report.
        var data = """{"execution":{"version":1,"status":"completed","summary":"done"}}""";
        var envelope = new StructuredAgentResult(
            StructuredAgentResult.CurrentVersion, StructuredResultStatus.Succeeded, "s", "ok", data,
            Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), null);

        return new AgentRun
        {
            WorkItemId = workItemId,
            Status = AgentRunStatus.Succeeded,
            StructuredResult = JsonSerializer.Serialize(envelope, StructuredAgentResult.JsonOptions)
        };
    }

    [Fact]
    public async Task NoOpAgent_GreenTests_ButAcceptanceFails_GoesToRepairing_NeverCompleted()
    {
        // The agent claims completion, the suite is green — and the requested
        // change simply is not in the workspace.
        var workItemId = await SeedRunningWorkItemAsync("""
            {"version":1,"criteria":[
              {"type":"ExpectedTextPresent","path":"src/Marker.cs","value":"MARKER_AFTER"},
              {"type":"ExpectedTextAbsent","path":"src/Marker.cs","value":"MARKER_BEFORE"}
            ]}
            """);

        await using var db = new FactoryDbContext(_options);
        await CreateSync(db).SyncAsync(SucceededRun(workItemId));

        var workItem = await new WorkItemRepository(db).GetByIdAsync(workItemId);

        Assert.Equal(WorkItemStatus.Repairing.ToString(), workItem!.Status);
        Assert.NotEqual(WorkItemStatus.Completed.ToString(), workItem.Status);

        // The repair agent must receive the deterministic acceptance evidence (§11).
        Assert.Contains("acceptance criteria failed", workItem.CurrentError, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MARKER_AFTER", workItem.CurrentError);
    }

    [Fact]
    public async Task AgentDidTheWork_AcceptancePasses_ReachesCompleted()
    {
        File.WriteAllText(
            Path.Combine(_workspace, "src", "Marker.cs"), "public const string Value = \"MARKER_AFTER\";");

        var workItemId = await SeedRunningWorkItemAsync("""
            {"version":1,"criteria":[
              {"type":"ExpectedTextPresent","path":"src/Marker.cs","value":"MARKER_AFTER"},
              {"type":"ExpectedTextAbsent","path":"src/Marker.cs","value":"MARKER_BEFORE"}
            ]}
            """);

        await using var db = new FactoryDbContext(_options);
        await CreateSync(db).SyncAsync(SucceededRun(workItemId));

        var workItem = await new WorkItemRepository(db).GetByIdAsync(workItemId);
        Assert.Equal(WorkItemStatus.Completed.ToString(), workItem!.Status);
    }

    [Fact]
    public async Task LegacyWorkItemWithoutCriteria_KeepsHistoricalBuildTestSemantics()
    {
        // §9 — a WorkItem predating acceptance criteria must not start failing.
        var workItemId = await SeedRunningWorkItemAsync(null);

        await using var db = new FactoryDbContext(_options);
        await CreateSync(db).SyncAsync(SucceededRun(workItemId));

        var workItem = await new WorkItemRepository(db).GetByIdAsync(workItemId);
        Assert.Equal(WorkItemStatus.Completed.ToString(), workItem!.Status);
    }

    [Fact]
    public async Task InvalidAcceptanceContract_FailsClosed_WithoutEnteringRepairLoop()
    {
        // An authoring error is nothing a repair agent can fix, so it must not
        // consume the repair budget — it fails deterministically instead.
        var workItemId = await SeedRunningWorkItemAsync("""
            {"version":1,"criteria":[{"type":"ExecuteShellCommand","path":"x","value":"whoami"}]}
            """);

        await using var db = new FactoryDbContext(_options);
        await CreateSync(db).SyncAsync(SucceededRun(workItemId));

        var workItem = await new WorkItemRepository(db).GetByIdAsync(workItemId);

        Assert.Equal(WorkItemStatus.Failed.ToString(), workItem!.Status);
        Assert.NotEqual(WorkItemStatus.Completed.ToString(), workItem.Status);
        Assert.Equal(0, workItem.RepairAttemptCount);
    }
}
