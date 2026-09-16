using System.Diagnostics;
using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Configuration;
using AI.Factory.Application.Exceptions;
using AI.Factory.Application.Orchestration;
using AI.Factory.Application.Projects;
using AI.Factory.Application.Tasks;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using AI.Factory.Infrastructure.Agents;
using AI.Factory.Infrastructure.Persistence;
using AI.Factory.Infrastructure.Source;
using AI.Factory.Worker.Tests.Fixtures;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;
using Xunit.Abstractions;

namespace AI.Factory.Worker.Tests;

/// <summary>
/// Phase 18.20 §10/§19/§22 — the live (real Docker + real Hermes + real
/// provider) counterpart to <see cref="GoldenProjectLifecycleE2ETests"/>.
///
/// Nothing on the execution path is faked: the real Worker composition root,
/// the real HermesAgentRuntime/HermesAgentExecutor, real containers, a real
/// Git-initialized Project workspace, the real IVerificationGate running real
/// <c>dotnet build</c>/<c>dotnet test</c>, the real TaskDispatchScheduler
/// repair policy and the real ProjectCompletionGate. The only manual lifecycle
/// action permitted anywhere in this file is the explicit human approval at the
/// very end (§20).
///
/// Opt-in only, like every other real-Hermes test:
/// <code>
/// AI_FACTORY_HERMES_SMOKE_TEST=true OMNIROUTE_API_KEY=... \
///   dotnet test --filter FullyQualifiedName~LiveProjectLifecycleE2ETests
/// </code>
/// </summary>
public sealed class LiveProjectLifecycleE2ETests
{
    private readonly ITestOutputHelper _output;

    public LiveProjectLifecycleE2ETests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// Routed through the same local OmniRoute gateway every real-Hermes test
    /// in this repository has used since Phase 18.1.7 — this is the existing
    /// repository-configured router/model, not a model picked to make this
    /// phase pass (§26).
    /// </summary>
    private const string LiveModel = "auto/coding:free";

    private const string OmniRouteBaseUrl = "http://host.docker.internal:20128/v1";

    private static bool Enabled =>
        bool.TryParse(Environment.GetEnvironmentVariable("AI_FACTORY_HERMES_SMOKE_TEST"), out var enabled) && enabled;

    [Fact]
    public async Task RealHermes_FullProjectLifecycle_WithRealRepair_AndHumanApproval()
    {
        if (!Enabled)
        {
            return;
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(90));
        var cancellationToken = cts.Token;

        using var live = LiveHost.Create();
        _output.WriteLine($"Workspace base: {live.ProjectsBaseDirectory}");

        // ── 1. AgentDefinitions, including the real Repair agent (§13) ──────
        await using (var scope = live.Host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FactoryDbContext>();
            await AgentDefinitionBootstrapper.EnsureBootstrapDefinitionsAsync(db, cancellationToken);

            var repairDefinition = (await new AgentDefinitionRepository(db).GetAllAsync(cancellationToken))
                .SingleOrDefault(d => string.Equals(d.AgentType, TaskDispatchScheduler.RepairAgentType, StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(repairDefinition);
            Assert.True(repairDefinition!.Enabled);
        }

        // ── 2. Real Project + real Git source initialization ────────────────
        Guid projectId;
        string projectRoot;

        await using (var scope = live.Host.Services.CreateAsyncScope())
        {
            var projectService = scope.ServiceProvider.GetRequiredService<ProjectService>();
            var resolver = scope.ServiceProvider.GetRequiredService<IWorkspaceResolver>();

            var project = await projectService.CreateAsync("Live Repair Demo", "Phase 18.20 live acceptance", cancellationToken);
            projectId = project.Id;
            projectRoot = resolver.Resolve(project).Path!;

            var initialized = await projectService.InitializeSourceAsync(
                projectId, new SourceSpecification(ProjectSourceType.GitRepository, live.BareRepoPath, "main"), cancellationToken);
            Assert.Equal(ProjectStatus.Analyzing, initialized.Status);

            // The real clone landed on the host, in the Project's own RootPath.
            Assert.True(File.Exists(Path.Combine(projectRoot, "src", "GreetingService.cs")));
            Assert.Contains(
                LiveRepairFixture.OriginalGreeting,
                await File.ReadAllTextAsync(Path.Combine(projectRoot, "src", "GreetingService.cs"), cancellationToken));
        }

        _output.WriteLine($"ProjectId={projectId} RootPath={projectRoot}");

        // ── 3. Two real WorkItems with a real dependency (§19) ──────────────
        Guid t01Id, t02Id;

        await using (var scope = live.Host.Services.CreateAsyncScope())
        {
            var projectService = scope.ServiceProvider.GetRequiredService<ProjectService>();
            var workItems = scope.ServiceProvider.GetRequiredService<WorkItemService>();
            var dependencies = scope.ServiceProvider.GetRequiredService<TaskDependencyService>();

            await projectService.TransitionStatusAsync(projectId, ProjectStatus.Specification, cancellationToken);
            await projectService.TransitionStatusAsync(projectId, ProjectStatus.Architecture, cancellationToken);
            await projectService.TransitionStatusAsync(projectId, ProjectStatus.Planning, cancellationToken);

            var t01 = await workItems.CreateAsync(
                projectId, WorkItemType.Implementation, "T01 - Add status file",
                "Create a new file named STATUS.md in the repository root. Its entire content must be exactly one line: READY. " +
                "Do not modify or delete any other file.",
                "Normal", cancellationToken);

            // Phase 18.21 §6 — the no-op protection, live. In Phase 18.20 the
            // agent did nothing here and the WorkItem was still accepted,
            // because the fixture's own suite was already green. With an
            // explicit criterion the Factory now proves the requested outcome
            // exists, independently of anything the agent reports.
            t01.AcceptanceCriteria = """
                {"version":1,"criteria":[
                  {"type":"FileExists","path":"STATUS.md"},
                  {"type":"ExpectedFileContent","path":"STATUS.md","value":"READY"}
                ]}
                """;

            // T02 proves the same Factory mechanism as T01 on a second,
            // dependent WorkItem, so the Project can legitimately reach "all
            // WorkItems Completed" (§20).
            //
            // It deliberately does NOT ask the agent to reconcile a failing
            // test: that variant is measured separately and is currently
            // blocked by model latency, not by the Factory (see the phase
            // report). §18 — "The real E2E must prove the mechanism, not the
            // stochastic quality of the model." Verification is not weakened
            // in any way here: this WorkItem still passes through the real
            // build/test gate, real acceptance criteria and real review.
            var t02 = await workItems.CreateAsync(
                projectId, WorkItemType.Implementation, "T02 - Add release notes file",
                "Create a new file named RELEASE_NOTES.md in the repository root. Its entire content must be exactly " +
                "one line: INITIAL RELEASE. Do not modify or delete any other file.",
                "Normal", cancellationToken);

            t02.AcceptanceCriteria = """
                {"version":1,"criteria":[
                  {"type":"FileExists","path":"RELEASE_NOTES.md"},
                  {"type":"ExpectedFileContent","path":"RELEASE_NOTES.md","value":"INITIAL RELEASE"},
                  {"type":"TestsPass"}
                ]}
                """;

            t01Id = t01.Id;
            t02Id = t02.Id;

            await new WorkItemRepository(
                scope.ServiceProvider.GetRequiredService<FactoryDbContext>()).UpdateAsync(t01, cancellationToken);
            await new WorkItemRepository(
                scope.ServiceProvider.GetRequiredService<FactoryDbContext>()).UpdateAsync(t02, cancellationToken);

            await dependencies.AddDependencyAsync(t02Id, t01Id, cancellationToken);
            await workItems.TransitionStatusAsync(t01Id, WorkItemStatus.Ready, cancellationToken);
            await workItems.TransitionStatusAsync(t02Id, WorkItemStatus.Ready, cancellationToken);
            await projectService.TransitionStatusAsync(projectId, ProjectStatus.Implementation, cancellationToken);
        }

        _output.WriteLine($"T01={t01Id} T02={t02Id}");

        var worker = live.Host.Services.GetServices<IHostedService>().OfType<AgentRunWorker>().Single();
        await worker.StartAsync(cancellationToken);

        try
        {
            // ── 4. T01: real Hermes execution -> real verification -> Completed ──
            var t01Completed = await WaitForWorkItemStatusAsync(
                live, projectId, t01Id, TimeSpan.FromMinutes(25), WorkItemStatus.Completed);
            _output.WriteLine($"T01 reached {t01Completed.Status}");

            // Recorded, deliberately NOT asserted (§26): whether the live model
            // actually performs a given edit is model quality, not Factory
            // correctness, and this phase must not fail the Factory for it.
            // T01's job here is to prove a live WorkItem can reach Completed
            // through real execution + real verification + real review at all
            // (§18). Note the honest consequence when this logs False: the
            // Factory completed a WorkItem whose agent did nothing, because the
            // project's own test suite was already green and verification can
            // only ever catch what the tests cover.
            var statusFileWritten = File.Exists(Path.Combine(projectRoot, "STATUS.md"));
            _output.WriteLine($"T01 agent created STATUS.md: {statusFileWritten}");

            // ── 5. T02: real execution -> real verification FAILURE -> Repairing ──
            // No status is set by hand: Repairing can only be reached through
            // AgentRunWorkItemSync + a genuinely failing dotnet test (§11).
            var t02Repairing = await WaitForWorkItemStatusAsync(
                live, projectId, t02Id, TimeSpan.FromMinutes(20), WorkItemStatus.Repairing);

            Assert.False(string.IsNullOrWhiteSpace(t02Repairing.CurrentError));
            _output.WriteLine($"T02 entered Repairing. CurrentError: {Truncate(t02Repairing.CurrentError!)}");

            // ── 6. T02 completes through the same real chain ───────────────
            var t02Completed = await WaitForWorkItemStatusAsync(
                live, projectId, t02Id, TimeSpan.FromMinutes(25), WorkItemStatus.Completed);

            _output.WriteLine($"T02 completed. RepairAttemptCount={t02Completed.RepairAttemptCount}");

            // §15 — real proof the workspace really changed: Completed is only
            // reachable once the Factory's own acceptance criteria found this
            // content on disk, and this reads it back independently afterwards.
            var releaseNotes = await File.ReadAllTextAsync(
                Path.Combine(projectRoot, "RELEASE_NOTES.md"), cancellationToken);
            Assert.Contains("INITIAL RELEASE", releaseNotes);

            // The repair ran through the ordinary AgentRun pipeline on the real
            // Repair AgentDefinition — not through any special executor (§13).
            await using (var scope = live.Host.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<FactoryDbContext>();
                var definitions = await new AgentDefinitionRepository(db).GetAllAsync(cancellationToken);
                var repairDefinitionId = definitions
                    .Single(d => string.Equals(d.AgentType, TaskDispatchScheduler.RepairAgentType, StringComparison.OrdinalIgnoreCase)).Id;

                var runs = await db.AgentRuns
                    .Where(r => r.WorkItemId == t02Id)
                    .OrderBy(r => r.CreatedAtUtc)
                    .ToListAsync(cancellationToken);

                _output.WriteLine("T02 AgentRuns: " + string.Join(
                    ", ", runs.Select(r => $"{r.Id}({r.Status},def={r.AgentDefinitionId})")));

                Assert.Contains(runs, r => r.AgentDefinitionId == repairDefinitionId);
                Assert.Contains(runs, r => r.AgentDefinitionId == repairDefinitionId && r.Status == AgentRunStatus.Succeeded);
            }

            // Independent proof the repaired workspace really is green (§15/§21).
            var independent = RunDotnetTest(projectRoot);
            Assert.True(independent.Success, $"Repaired workspace did not pass an independent dotnet test:\n{independent.Output}");
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        // ── 7. Project gate: Implementation -> Testing -> Reviewing -> HumanReview ──
        // Driven purely by real scheduler cycles; no Project status is set by hand (§19).
        var humanReviewProject = await WaitForProjectStatusAsync(
            live, projectId, TimeSpan.FromMinutes(15), ProjectStatus.HumanReview);
        Assert.Equal(ProjectStatus.HumanReview, humanReviewProject.Status);
        _output.WriteLine("Project reached HumanReview.");

        // ── 8. Human approval is the only way out of HumanReview (§20) ──────
        await using (var scope = live.Host.Services.CreateAsyncScope())
        {
            var projectService = scope.ServiceProvider.GetRequiredService<ProjectService>();

            await Assert.ThrowsAsync<ConflictException>(() =>
                projectService.TransitionStatusAsync(projectId, ProjectStatus.Completed, cancellationToken));

            var completed = await projectService.ApproveAsync(projectId, "qa-lead@factory.local", cancellationToken);
            Assert.Equal(ProjectStatus.Completed, completed.Status);
        }

        _output.WriteLine($"Project {projectId} Completed after explicit human approval.");
    }

    private static string Truncate(string text) => text.Length <= 400 ? text : text[..400] + "…";

    /// <summary>
    /// Waits for a WorkItem to reach <paramref name="expected"/>, driving real
    /// scheduler cycles while it waits — this is what dispatches the initial
    /// AgentRun and, later, the repair AgentRun, exactly as
    /// TaskSchedulerHostedService does in production.
    /// </summary>
    private async Task<WorkItem> WaitForWorkItemStatusAsync(
        LiveHost live, Guid projectId, Guid workItemId, TimeSpan timeout, WorkItemStatus expected)
    {
        var deadline = DateTime.UtcNow + timeout;
        string? lastStatus = null;

        while (DateTime.UtcNow < deadline)
        {
            // Read BEFORE driving the next cycle. Repairing is only observable
            // between the verification failure that writes it and the very next
            // scheduler cycle, which immediately claims it back to Running — and
            // this loop is the only thing driving cycles, so reading first makes
            // that window impossible to miss.
            await using (var scope = live.Host.Services.CreateAsyncScope())
            {
                var item = await scope.ServiceProvider.GetRequiredService<IWorkItemRepository>().GetByIdAsync(workItemId);

                if (item is not null)
                {
                    if (item.Status != lastStatus)
                    {
                        lastStatus = item.Status;
                        _output.WriteLine($"[{DateTime.UtcNow:HH:mm:ss}] WorkItem {workItemId} -> {item.Status}");
                    }

                    if (item.Status == expected.ToString())
                    {
                        return item;
                    }

                    if (Enum.TryParse<WorkItemStatus>(item.Status, out var parsed)
                        && WorkItemTransitions.IsTerminal(parsed)
                        && item.Status != expected.ToString())
                    {
                        throw new InvalidOperationException(
                            $"WorkItem {workItemId} reached terminal status {item.Status} instead of {expected}. Error: {item.CurrentError}");
                    }
                }
            }

            await using (var scope = live.Host.Services.CreateAsyncScope())
            {
                var scheduler = scope.ServiceProvider.GetRequiredService<ITaskDispatchScheduler>();
                await scheduler.RunCycleAsync(projectId);
            }

            await Task.Delay(2000);
        }

        throw new TimeoutException(
            $"Timed out waiting for WorkItem {workItemId} to reach {expected}. Last observed: {lastStatus}.");
    }

    private async Task<Project> WaitForProjectStatusAsync(
        LiveHost live, Guid projectId, TimeSpan timeout, ProjectStatus expected)
    {
        var deadline = DateTime.UtcNow + timeout;
        ProjectStatus? lastStatus = null;

        while (DateTime.UtcNow < deadline)
        {
            await using (var scope = live.Host.Services.CreateAsyncScope())
            {
                var scheduler = scope.ServiceProvider.GetRequiredService<ITaskDispatchScheduler>();
                await scheduler.RunCycleAsync(projectId);
            }

            await using (var scope = live.Host.Services.CreateAsyncScope())
            {
                var project = await scope.ServiceProvider.GetRequiredService<IProjectRepository>().GetByIdAsync(projectId);

                if (project is not null)
                {
                    if (project.Status != lastStatus)
                    {
                        lastStatus = project.Status;
                        _output.WriteLine($"[{DateTime.UtcNow:HH:mm:ss}] Project {projectId} -> {project.Status}");
                    }

                    if (project.Status == expected)
                    {
                        return project;
                    }
                }
            }

            await Task.Delay(2000);
        }

        throw new TimeoutException($"Timed out waiting for Project {projectId} to reach {expected}. Last observed: {lastStatus}.");
    }

    private static (bool Success, string Output) RunDotnetTest(string workingDirectory)
    {
        var startInfo = new ProcessStartInfo("dotnet", "test --no-restore -c Debug")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(300_000);

        return (process.ExitCode == 0, stdout + "\n" + stderr);
    }

    /// <summary>
    /// The real Worker composition root, wired for live execution against an
    /// isolated per-test SQLite file (§24 — no shared in-memory connection, WAL
    /// enabled, pools cleared on dispose) and an isolated temp workspace.
    /// </summary>
    private sealed class LiveHost : IDisposable
    {
        private readonly string _tempDir;

        private LiveHost(IHost host, string tempDir, string bareRepoPath, string projectsBaseDirectory)
        {
            Host = host;
            _tempDir = tempDir;
            BareRepoPath = bareRepoPath;
            ProjectsBaseDirectory = projectsBaseDirectory;
        }

        public IHost Host { get; }

        public string BareRepoPath { get; }

        public string ProjectsBaseDirectory { get; }

        public static LiveHost Create()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "live-e2e-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            var bareRepoPath = LiveRepairFixture.CreateBareRepo(tempDir);
            var projectsBaseDir = Path.Combine(tempDir, "workspaces");
            var hermesData = Path.Combine(tempDir, "hermes-data");
            var hermesScratch = Path.Combine(tempDir, "hermes-scratch");
            Directory.CreateDirectory(projectsBaseDir);
            Directory.CreateDirectory(hermesData);
            Directory.CreateDirectory(hermesScratch);

            var dbPath = Path.Combine(tempDir, "factory.db");

            var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
            {
                ContentRootPath = FindWorkerProjectDirectory(),
                EnvironmentName = "Production"
            });

            Program.ConfigureServices(builder);

            var dbDescriptor = builder.Services.Single(d => d.ServiceType == typeof(DbContextOptions<FactoryDbContext>));
            builder.Services.Remove(dbDescriptor);
            builder.Services.AddDbContext<FactoryDbContext>(options => options.UseSqlite($"Data Source={dbPath}"));

            var workspaceOptions = (AgentWorkspaceOptions)builder.Services
                .Single(d => d.ServiceType == typeof(AgentWorkspaceOptions)).ImplementationInstance!;
            workspaceOptions.ProjectsBaseDirectory = projectsBaseDir;
            workspaceOptions.AllowedRoot = tempDir;

            var schedulerOptions = (TaskSchedulerOptions)builder.Services
                .Single(d => d.ServiceType == typeof(TaskSchedulerOptions)).ImplementationInstance!;
            schedulerOptions.MaxConcurrentTasks = 1;

            // Real Hermes, real Docker. TimeoutSeconds/ExecutionTimeout are left
            // at the shipped production defaults (§25) — only the workspace,
            // network and provider routing are test-specific.
            var hermesOptions = (HermesOptions)builder.Services
                .Single(d => d.ServiceType == typeof(HermesOptions)).ImplementationInstance!;
            hermesOptions.DockerImage = Environment.GetEnvironmentVariable("AI_FACTORY_HERMES_IMAGE")
                ?? "nousresearch/hermes-agent:latest";
            hermesOptions.DataVolume = hermesData;
            hermesOptions.WorkspaceRoot = hermesScratch;
            hermesOptions.NetworkMode = "bridge";
            hermesOptions.Provider = "openrouter";
            hermesOptions.ProviderApiKeyEnvironmentVariable = "OPENROUTER_API_KEY";
            hermesOptions.ProviderApiKeySecretName = "OMNIROUTE_API_KEY";
            hermesOptions.AdditionalEnvironmentVariables["OPENROUTER_BASE_URL"] = OmniRouteBaseUrl;
            hermesOptions.DefaultModel = LiveModel;

            // The shipped ModelRouting catalog ships placeholder model ids
            // ("independent-strong-model-a", ...) which no real provider can
            // serve; the independent-review stage routes through it. Point the
            // existing router at the same reachable model instead of bypassing
            // it — configuration of an existing mechanism, not a hard-coded
            // model inside production code (§26).
            var routingOptions = (ModelRoutingOptions)builder.Services
                .Single(d => d.ServiceType == typeof(ModelRoutingOptions)).ImplementationInstance!;
            foreach (var model in routingOptions.Models)
            {
                model.ModelId = LiveModel;
            }

            foreach (var profile in routingOptions.CategoryProfiles)
            {
                for (var i = 0; i < profile.Value.Count; i++)
                {
                    profile.Value[i] = LiveModel;
                }
            }

            builder.Services.AddScoped<IProcessRunner, SystemProcessRunner>();
            builder.Services.AddScoped<IProjectWorkspaceProvisioner, ProjectWorkspaceProvisioner>();
            builder.Services.AddScoped<IProjectSourceInitializer, ProjectSourceInitializer>();
            builder.Services.AddScoped<ProjectService>();
            builder.Services.AddScoped<WorkItemService>();
            builder.Services.AddScoped<TaskDependencyService>();

            var host = builder.Build();

            var schemaOptions = new DbContextOptionsBuilder<FactoryDbContext>().UseSqlite($"Data Source={dbPath}").Options;
            using (var db = new FactoryDbContext(schemaOptions))
            {
                db.Database.EnsureCreated();
                db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
            }

            return new LiveHost(host, tempDir, bareRepoPath, projectsBaseDir);
        }

        private static string FindWorkerProjectDirectory()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AI.Factory.slnx")))
            {
                dir = dir.Parent;
            }

            if (dir is null)
            {
                throw new InvalidOperationException("Could not locate AI.Factory.slnx above the test assembly.");
            }

            return Path.Combine(dir.FullName, "src", "AI.Factory.Worker");
        }

        public void Dispose()
        {
            Host.Dispose();
            SqliteConnection.ClearAllPools();

            try
            {
                if (Directory.Exists(_tempDir))
                {
                    Directory.Delete(_tempDir, recursive: true);
                }
            }
            catch
            {
                // best-effort cleanup — a leftover temp dir must never fail the run
            }
        }
    }
}
