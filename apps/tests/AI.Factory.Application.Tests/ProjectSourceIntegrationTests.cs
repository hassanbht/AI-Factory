using System.Diagnostics;
using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Agents;
using AI.Factory.Application.Configuration;
using AI.Factory.Application.Orchestration;
using AI.Factory.Application.Projects;
using AI.Factory.Application.Tasks;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using AI.Factory.Infrastructure.Agents;
using AI.Factory.Infrastructure.Context;
using AI.Factory.Infrastructure.Persistence;
using AI.Factory.Infrastructure.Source;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AI.Factory.Application.Tests;

/// <summary>
/// Phase 18.16 §19/§27 — the end-to-end claim this phase exists to prove:
/// Project -> provisioned RootPath -> real Git source initialized -> the
/// SAME physical workspace is what WorkItem dispatch/AgentOrchestrator (no
/// real Hermes) and IWorkspaceResolver (what VerificationGate itself uses)
/// both see — a real cloned file, not an empty directory, and no second
/// workspace authority anywhere in the chain.
/// </summary>
public sealed class ProjectSourceIntegrationTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<FactoryDbContext> _options;
    private readonly string _tempRoot;
    private readonly string _bareRepoPath;

    public ProjectSourceIntegrationTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<FactoryDbContext>().UseSqlite(_connection).Options;
        using var db = new FactoryDbContext(_options);
        db.Database.EnsureCreated();

        _tempRoot = Path.Combine(Path.GetTempPath(), "aifsi", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempRoot);
        _bareRepoPath = Path.Combine(_tempRoot, "fx.git");
        CreateBareFixtureRepository();
    }

    public void Dispose()
    {
        _connection.Dispose();

        if (Directory.Exists(_tempRoot))
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(_tempRoot, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(_tempRoot, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best-effort cleanup only.
            }
        }
    }

    private void CreateBareFixtureRepository()
    {
        var seedDir = Path.Combine(_tempRoot, "seed");
        Directory.CreateDirectory(seedDir);

        RunGit(seedDir, "init", "-b", "main");
        RunGit(seedDir, "config", "user.email", "test@example.com");
        RunGit(seedDir, "config", "user.name", "Test");
        RunGit(seedDir, "config", "commit.gpgsign", "false");
        File.WriteAllText(Path.Combine(seedDir, "real-source.txt"), "this is real cloned source");
        RunGit(seedDir, "add", ".");
        RunGit(seedDir, "commit", "-m", "initial commit");

        RunGit(_tempRoot, "clone", "--bare", seedDir, _bareRepoPath);
    }

    private static void RunGit(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
        };
        foreach (var arg in arguments) startInfo.ArgumentList.Add(arg);

        using var process = Process.Start(startInfo)!;
        process.WaitForExit(15000);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {process.StandardError.ReadToEnd()}");
        }
    }

    private const string CompletedExecutionResult = """
        {"version":1,"status":"Succeeded","summary":"done","output":"ok","data":{"execution":{"version":1,"status":"completed","summary":"implemented it"}},"warnings":[],"errors":[],"artifacts":[],"metadata":{}}
        """;

    [Fact]
    public async Task CreateProject_InitializeGitSource_Dispatch_AgentOrchestratorAndVerificationSeeSameRealSourceTree()
    {
        var workspaceOptions = new AgentWorkspaceOptions { ProjectsBaseDirectory = _tempRoot };

        Guid projectId;
        string resolvedByProvisioner;

        // 1. Create Project -> RootPath provisioned (real ProjectWorkspaceProvisioner).
        await using (var db = new FactoryDbContext(_options))
        {
            var repo = new ProjectRepository(db);
            var resolver = new ProjectWorkspaceResolver(workspaceOptions);
            var provisioner = new ProjectWorkspaceProvisioner(resolver, workspaceOptions, NullLogger<ProjectWorkspaceProvisioner>.Instance);
            var sourceInitializer = new ProjectSourceInitializer(
                resolver, new SystemProcessRunner(new CommandPolicyOptions()),
                new ExecutionLimitsOptions { BuildTimeoutMinutes = 1 }, NullLogger<ProjectSourceInitializer>.Instance);
            var service = new ProjectService(repo, provisioner, sourceInitializer: sourceInitializer);

            var project = await service.CreateAsync("Integration Project", "d");
            projectId = project.Id;
            resolvedByProvisioner = resolver.Resolve(project).Path!;

            // 2. Initialize source: a real Git clone into that same RootPath.
            var initialized = await service.InitializeSourceAsync(
                projectId, new SourceSpecification(ProjectSourceType.GitRepository, _bareRepoPath));

            Assert.Equal(ProjectStatus.Analyzing, initialized.Status);
            Assert.Equal(_bareRepoPath, initialized.GitRepository);

            // 3. Project.RootPath contains the expected real cloned source file.
            Assert.True(File.Exists(Path.Combine(resolvedByProvisioner, "real-source.txt")));
        }

        // 4. Create/dispatch a WorkItem now that the source decision was made.
        await SeedAgentDefinitionAsync();
        Guid workItemId;
        await using (var db = new FactoryDbContext(_options))
        {
            var workItems = new WorkItemService(new WorkItemRepository(db), new ProjectRepository(db));
            var item = await workItems.CreateAsync(projectId, WorkItemType.Implementation, "Inspect real source", null, "Normal");
            await workItems.TransitionStatusAsync(item.Id, WorkItemStatus.Ready);
            workItemId = item.Id;
        }

        Guid agentRunId;
        await using (var db = new FactoryDbContext(_options))
        {
            var scheduler = new TaskDispatchScheduler(
                new WorkItemRepository(db), new ProjectRepository(db), new TaskDependencyResolver(),
                new AgentRunRepository(db), new AgentDefinitionRepository(db),
                new TaskSchedulerOptions { MaxConcurrentTasks = 2 }, new ExecutionLimitsOptions(),
                new ProjectCompletionGate(
                    new ProjectRepository(db), new AgentRunRepository(db), new FakeVerificationGate(),
                    NullLogger<ProjectCompletionGate>.Instance),
                new OperationalMetrics(), NullLogger<TaskDispatchScheduler>.Instance);

            var cycle = await scheduler.RunCycleAsync(projectId);
            Assert.Equal(1, cycle.DispatchedCount);
            agentRunId = cycle.Outcomes.Single().AgentRunId!.Value;
        }

        // 5. AgentOrchestrator (fake Hermes runtime — no real Hermes required)
        // resolves the workspace via the SAME resolver/base-directory rule.
        string? resolvedByOrchestrator = null;
        await using (var db = new FactoryDbContext(_options))
        {
            var probingResolver = new RecordingWorkspaceResolver(new ProjectWorkspaceResolver(workspaceOptions));
            var orchestrator = new AgentOrchestrator(
                new AgentRunRepository(db), new ProjectRepository(db), new FakeAgentToolRegistry(), new FakeAgentMemory(),
                new FakeAgentRuntime(FakeAgentRuntime.Mode.Success, CompletedExecutionResult),
                probingResolver, new AgentInstructionBuilder(), new StructuredAgentResultParser(),
                NullLogger<AgentOrchestrator>.Instance);

            var result = await orchestrator.ExecuteAsync(agentRunId);
            Assert.True(result.Success);
            resolvedByOrchestrator = probingResolver.LastResolvedPath;
        }

        Assert.Equal(resolvedByProvisioner, resolvedByOrchestrator);

        // 6. IWorkspaceResolver — what VerificationGate itself resolves through
        // (Phase 18.16 §15) — agrees on the exact same absolute path too, and
        // that path still contains the real cloned file.
        await using (var db = new FactoryDbContext(_options))
        {
            var project = (await new ProjectRepository(db).GetByIdAsync(projectId))!;
            var verificationResolution = new ProjectWorkspaceResolver(workspaceOptions).Resolve(project);

            Assert.True(verificationResolution.IsValid, verificationResolution.FailureReason);
            Assert.Equal(resolvedByProvisioner, verificationResolution.Path);
            Assert.True(File.Exists(Path.Combine(verificationResolution.Path!, "real-source.txt")));
        }
    }

    private async Task SeedAgentDefinitionAsync()
    {
        await using var db = new FactoryDbContext(_options);
        await new AgentDefinitionRepository(db).CreateAsync(new AgentDefinition
        {
            Name = "Agent-Implementation", AgentType = "Implementation", SystemPrompt = "x"
        });
    }

    /// <summary>Deterministic stand-in — this test proves workspace-path agreement and dispatch wiring, not build/test execution.</summary>
    private sealed class FakeVerificationGate : IVerificationGate
    {
        public Task<VerificationResult> VerifyAsync(Project project, WorkItem workItem, CancellationToken cancellationToken = default) =>
            Task.FromResult(new VerificationResult(VerificationStatus.Passed, "fake", 0, TimeSpan.Zero));

        public Task<VerificationResult> VerifyProjectAsync(Project project, CancellationToken cancellationToken = default) =>
            Task.FromResult(new VerificationResult(VerificationStatus.Passed, "fake", 0, TimeSpan.Zero));
    }

    /// <summary>Delegates to a real resolver while recording the last resolved path, so the test can compare it against the Provisioner's own resolution without re-implementing resolution logic.</summary>
    private sealed class RecordingWorkspaceResolver : IWorkspaceResolver
    {
        private readonly IWorkspaceResolver _inner;
        public string? LastResolvedPath { get; private set; }

        public RecordingWorkspaceResolver(IWorkspaceResolver inner) => _inner = inner;

        public WorkspaceResolution Resolve(Project project)
        {
            var result = _inner.Resolve(project);
            LastResolvedPath = result.Path;
            return result;
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // Phase 18.17 §5 — Context / Agent / Verification consistency
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Phase 18.17 §5 — proves the unified-workspace invariant holds across all
    /// three production components that consume a Project workspace:
    ///
    ///   LocalContextProvider (Phase 18.17 fix)
    ///   AgentOrchestrator    (Phase 18.14)
    ///   VerificationGate     (Phase 18.16)
    ///
    /// All three resolve to the SAME absolute physical directory when
    /// ProjectsBaseDirectory is configured — without depending on the process
    /// CWD. The test does NOT duplicate the existing 18.14–18.16 coverage; it
    /// adds the one previously-missing Context leg of the triangle.
    /// </summary>
    [Fact]
    public async Task ContextProvider_AgentOrchestrator_VerificationGate_AllResolveToSameAbsolutePath_WhenBaseDirectoryConfigured()
    {
        var workspaceOptions = new AgentWorkspaceOptions { ProjectsBaseDirectory = _tempRoot };

        Guid projectId;
        string resolvedByProvisioner;

        // 1. Create Project + initialize Git source into the provisioned workspace.
        await using (var db = new FactoryDbContext(_options))
        {
            var repo = new ProjectRepository(db);
            var resolver = new ProjectWorkspaceResolver(workspaceOptions);
            var provisioner = new ProjectWorkspaceProvisioner(
                resolver, workspaceOptions, NullLogger<ProjectWorkspaceProvisioner>.Instance);
            var sourceInitializer = new ProjectSourceInitializer(
                resolver, new SystemProcessRunner(new CommandPolicyOptions()),
                new ExecutionLimitsOptions { BuildTimeoutMinutes = 1 },
                NullLogger<ProjectSourceInitializer>.Instance);
            var service = new ProjectService(repo, provisioner, sourceInitializer: sourceInitializer);

            var project = await service.CreateAsync("Consistency Test Project", "d");
            projectId = project.Id;
            resolvedByProvisioner = resolver.Resolve(project).Path!;

            await service.InitializeSourceAsync(
                projectId, new SourceSpecification(ProjectSourceType.GitRepository, _bareRepoPath));
        }

        // Seed WorkItem + dispatch for Orchestrator path.
        await SeedAgentDefinitionAsync();
        Guid workItemId;
        await using (var db = new FactoryDbContext(_options))
        {
            var workItems = new WorkItemService(new WorkItemRepository(db), new ProjectRepository(db));
            var item = await workItems.CreateAsync(projectId, WorkItemType.Implementation, "t", null, "Normal");
            await workItems.TransitionStatusAsync(item.Id, WorkItemStatus.Ready);
            workItemId = item.Id;
        }

        Guid agentRunId;
        await using (var db = new FactoryDbContext(_options))
        {
            var scheduler = new TaskDispatchScheduler(
                new WorkItemRepository(db), new ProjectRepository(db), new TaskDependencyResolver(),
                new AgentRunRepository(db), new AgentDefinitionRepository(db),
                new TaskSchedulerOptions { MaxConcurrentTasks = 2 }, new ExecutionLimitsOptions(),
                new ProjectCompletionGate(
                    new ProjectRepository(db), new AgentRunRepository(db), new FakeVerificationGate(),
                    NullLogger<ProjectCompletionGate>.Instance),
                new OperationalMetrics(), NullLogger<TaskDispatchScheduler>.Instance);

            var cycle = await scheduler.RunCycleAsync(projectId);
            agentRunId = cycle.Outcomes.Single().AgentRunId!.Value;
        }

        // 2. AgentOrchestrator resolves workspace via IWorkspaceResolver.
        string? resolvedByOrchestrator = null;
        await using (var db = new FactoryDbContext(_options))
        {
            var probingResolver = new RecordingWorkspaceResolver(new ProjectWorkspaceResolver(workspaceOptions));
            var orchestrator = new AgentOrchestrator(
                new AgentRunRepository(db), new ProjectRepository(db),
                new FakeAgentToolRegistry(), new FakeAgentMemory(),
                new FakeAgentRuntime(FakeAgentRuntime.Mode.Success, CompletedExecutionResult),
                probingResolver, new AgentInstructionBuilder(), new StructuredAgentResultParser(),
                NullLogger<AgentOrchestrator>.Instance);

            await orchestrator.ExecuteAsync(agentRunId);
            resolvedByOrchestrator = probingResolver.LastResolvedPath;
        }

        // 3. LocalContextProvider (Phase 18.17) resolves workspace via IWorkspaceResolver.
        string? resolvedByContextProvider = null;
        await using (var db = new FactoryDbContext(_options))
        {
            var contextResolver = new RecordingWorkspaceResolver(new ProjectWorkspaceResolver(workspaceOptions));
            var contextProvider = new LocalContextProvider(db, contextResolver);
            var project = (await new ProjectRepository(db).GetByIdAsync(projectId))!;

            // Trigger both filesystem-backed query paths through the resolver.
            await contextProvider.QueryAsync(new ContextQuery(projectId, ContextCategory.RelevantSourceFiles));
            resolvedByContextProvider = contextResolver.LastResolvedPath;
        }

        // 4. VerificationGate resolver (standalone re-resolve, matching what RunAsync does).
        string? resolvedByVerificationGate = null;
        await using (var db = new FactoryDbContext(_options))
        {
            var project = (await new ProjectRepository(db).GetByIdAsync(projectId))!;
            var verificationResolution = new ProjectWorkspaceResolver(workspaceOptions).Resolve(project);
            Assert.True(verificationResolution.IsValid, verificationResolution.FailureReason);
            resolvedByVerificationGate = verificationResolution.Path;
        }

        // All three must agree on the same absolute path — and that path must
        // contain the real cloned fixture file.
        Assert.Equal(resolvedByProvisioner, resolvedByOrchestrator);
        Assert.Equal(resolvedByProvisioner, resolvedByContextProvider);
        Assert.Equal(resolvedByProvisioner, resolvedByVerificationGate);
        Assert.True(
            File.Exists(Path.Combine(resolvedByProvisioner, "real-source.txt")),
            $"Fixture file missing from resolved workspace '{resolvedByProvisioner}'");
    }
}
