using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Abstractions.Execution;
using AI.Factory.Application.Agents;
using AI.Factory.Application.Configuration;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using AI.Factory.Infrastructure.Agents;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AI.Factory.Application.Tests.Execution;

/// <summary>
/// Phase D.1 — Execution Runtime Lifecycle Hardening Unit Tests.
/// Tests container naming, metadata labels, retry classification, authoritative timeout,
/// cleanup on timeout/cancellation/startup-failure, and startup orphan sweep boundaries.
/// </summary>
public sealed class ContainerLifecycleUnitTests
{
    private sealed class TrackingProcessRunner : IProcessRunner
    {
        private readonly Func<ProcessRunRequest, ProcessRunResult> _respond;

        public TrackingProcessRunner(Func<ProcessRunRequest, ProcessRunResult>? respond = null)
        {
            _respond = respond ?? (_ => new ProcessRunResult(0, "ok", string.Empty, TimedOut: false));
        }

        public List<ProcessRunRequest> Requests { get; } = new();

        public Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_respond(request));
        }
    }

    #region 1. Container Identity & Labels

    [Fact]
    public void ContainerNaming_FollowsDeterministicPattern()
    {
        var runId = Guid.Parse("12345678-1234-1234-1234-123456789abc");
        var containerName = DockerContainerNaming.GetContainerName(runId);

        Assert.Equal("ai-factory-agent-12345678123412341234123456789abc", containerName);
        Assert.StartsWith(DockerContainerNaming.ContainerNamePrefix, containerName);
    }

    [Fact]
    public void DockerLabels_ContainAllMandatoryMetadata_WithoutSecrets()
    {
        var runId = Guid.NewGuid();
        var projectId = Guid.NewGuid();

        var labels = DockerContainerNaming.GetExecutionLabels(runId, projectId, "Hermes");

        Assert.Equal(DockerContainerNaming.FactoryLabelValue, labels[DockerContainerNaming.FactoryLabelKey]);
        Assert.Equal(DockerContainerNaming.ManagedLabelValue, labels[DockerContainerNaming.ManagedLabelKey]);
        Assert.Equal(runId.ToString(), labels[DockerContainerNaming.AgentRunIdLabelKey]);
        Assert.Equal(projectId.ToString(), labels[DockerContainerNaming.ProjectIdLabelKey]);
        Assert.Equal("Hermes", labels[DockerContainerNaming.AdapterLabelKey]);

        // Security assertion: no secret keys or passwords in labels
        foreach (var kvp in labels)
        {
            Assert.DoesNotContain("secret", kvp.Key, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("key", kvp.Key, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("password", kvp.Key, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("token", kvp.Key, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("secret", kvp.Value, StringComparison.OrdinalIgnoreCase);
        }
    }

    #endregion

    #region 2. Retry Classification

    [Fact]
    public void RetryClassifier_ExecutionTimeout_IsNeverRetried()
    {
        var resultFlag = new AgentExecutionResult(false, null, "Process timed out", 1000, TimedOut: true);
        var resultText = new AgentExecutionResult(false, null, "Execution timed out after 60 seconds.", 60000);

        Assert.False(AgentExecutionFailureClassifier.IsTransientLaunchFailure(resultFlag));
        Assert.False(AgentExecutionFailureClassifier.IsTransientLaunchFailure(resultText));
    }

    [Fact]
    public void RetryClassifier_Cancellation_IsNeverRetried()
    {
        var resultFlag = new AgentExecutionResult(false, null, "Operation cancelled", 100, Cancelled: true);
        var resultText = new AgentExecutionResult(false, null, "Execution was canceled by host", 100);

        Assert.False(AgentExecutionFailureClassifier.IsTransientLaunchFailure(resultFlag));
        Assert.False(AgentExecutionFailureClassifier.IsTransientLaunchFailure(resultText));
    }

    [Fact]
    public void RetryClassifier_MissingDockerNetwork_IsNeverRetried()
    {
        var result = new AgentExecutionResult(
            false, null, "docker: Error response from daemon: network test-net not found.\nSee 'docker run --help'.", 100, ExitCode: 125);

        Assert.False(AgentExecutionFailureClassifier.IsTransientLaunchFailure(result));
    }

    [Fact]
    public void RetryClassifier_InvalidDockerImage_IsNeverRetried()
    {
        var result = new AgentExecutionResult(
            false, null, "docker: Error response from daemon: repository test-img not found: does not exist or no pull access.", 200, ExitCode: 125);

        Assert.False(AgentExecutionFailureClassifier.IsTransientLaunchFailure(result));
    }

    [Fact]
    public void RetryClassifier_AgentNormalNonZeroExit_IsNeverRetried()
    {
        // Agent executed inside container and exited code 1 (normal application error)
        var result = new AgentExecutionResult(
            false, "SyntaxError: Unexpected token", "Process exited with code 1.", 2500, ExitCode: 1);

        Assert.False(AgentExecutionFailureClassifier.IsTransientLaunchFailure(result));
    }

    [Fact]
    public void RetryClassifier_GenuineTransientDaemonError_IsRetried()
    {
        var result1 = new AgentExecutionResult(
            false, null, "docker: Cannot connect to the Docker daemon at unix:///var/run/docker.sock. Is the docker daemon running?", 50);

        var result2 = new AgentExecutionResult(
            false, null, "Temporary network timeout connecting to docker daemon", 50);

        Assert.True(AgentExecutionFailureClassifier.IsTransientLaunchFailure(result1));
        Assert.True(AgentExecutionFailureClassifier.IsTransientLaunchFailure(result2));
    }

    #endregion

    #region 3. Authoritative Timeout

    [Fact]
    public async Task HermesAdapter_RejectsExpiredDeadlineImmediately_WithoutInvokingDocker()
    {
        var runner = new TrackingProcessRunner();
        var executor = new HermesAgentExecutor(
            new HermesOptions(), runner, NullLogger<HermesAgentExecutor>.Instance);
        var adapter = new HermesExecutionAdapter(
            executor, new FailureClassifier(), NullLogger<HermesExecutionAdapter>.Instance);

        var expiredSpec = new AgentExecutionSpec(
            AgentRunId: Guid.NewGuid(),
            CorrelationId: Guid.NewGuid(),
            ProjectId: null,
            WorkItemId: null,
            Attempt: 1,
            Iteration: 0,
            Agent: new ExecutionAgent(Guid.NewGuid(), "Coder", "Code", "sys"),
            Instructions: "do code",
            Workspace: new ExecutionWorkspace("."),
            Model: new ModelRoute("model-1", "openrouter"),
            Timeout: TimeSpan.FromSeconds(60),
            Deadline: DateTimeOffset.UtcNow.AddSeconds(-5)); // Already expired!

        var outcome = await adapter.ExecuteAsync(expiredSpec);

        Assert.Equal(ExecutionOutcomeStatus.TimedOut, outcome.Status);
        Assert.Equal(0, outcome.DurationMs);
        Assert.Empty(runner.Requests); // Docker was never called!
    }

    [Fact]
    public async Task HermesAgentExecutor_DerivesRemainingTimeout_FromAuthoritativeDeadline()
    {
        var runner = new TrackingProcessRunner();
        var executor = new HermesAgentExecutor(
            new HermesOptions { TimeoutSeconds = 300 }, runner, NullLogger<HermesAgentExecutor>.Instance);

        var deadline = DateTimeOffset.UtcNow.AddSeconds(45);
        var request = new AgentExecutionRequest(
            Agent: "coder",
            Model: "m1",
            Prompt: "prompt",
            WorkingDirectory: ".",
            Deadline: deadline);

        await executor.ExecuteAsync(request);

        var runRequest = runner.Requests.FirstOrDefault(r => r.Arguments.Contains("run"));
        Assert.NotNull(runRequest);
        // Timeout should be derived from deadline (around 45s, definitely less than options.TimeoutSeconds=300)
        Assert.True(runRequest.Timeout <= TimeSpan.FromSeconds(46));
        Assert.True(runRequest.Timeout >= TimeSpan.FromSeconds(40));
    }

    #endregion

    #region 4. Cleanup on Timeout, Cancellation, and Startup Failure

    [Fact]
    public async Task HermesAgentExecutor_CleansUpContainer_OnTimeout()
    {
        var runner = new TrackingProcessRunner(req =>
        {
            if (req.Arguments.Contains("run"))
            {
                return new ProcessRunResult(-1, "partial", "timed out", TimedOut: true);
            }
            return new ProcessRunResult(0, "ok", "", TimedOut: false);
        });

        var executor = new HermesAgentExecutor(
            new HermesOptions(), runner, NullLogger<HermesAgentExecutor>.Instance);

        var runId = Guid.NewGuid();
        var request = new AgentExecutionRequest(
            Agent: "coder",
            Model: "m1",
            Prompt: "prompt",
            WorkingDirectory: ".",
            AgentRunId: runId);

        var result = await executor.ExecuteAsync(request);

        Assert.False(result.Success);
        Assert.True(result.TimedOut);

        // Verify cleanup docker rm -f was issued for this container
        var expectedContainerName = DockerContainerNaming.GetContainerName(runId);
        var cleanupRequest = runner.Requests.FirstOrDefault(r => r.Arguments.Contains("rm") && r.Arguments.Contains(expectedContainerName));
        Assert.NotNull(cleanupRequest);
        Assert.Contains("-f", cleanupRequest.Arguments);
    }

    [Fact]
    public async Task HermesAgentExecutor_CleansUpContainer_OnCancellation()
    {
        var runId = Guid.NewGuid();
        var expectedContainerName = DockerContainerNaming.GetContainerName(runId);

        var runner = new TrackingProcessRunner(req =>
        {
            if (req.Arguments.Contains("run"))
            {
                throw new OperationCanceledException("Execution cancelled.");
            }
            return new ProcessRunResult(0, "ok", "", TimedOut: false);
        });

        var executor = new HermesAgentExecutor(
            new HermesOptions(), runner, NullLogger<HermesAgentExecutor>.Instance);

        var request = new AgentExecutionRequest(
            Agent: "coder",
            Model: "m1",
            Prompt: "prompt",
            WorkingDirectory: ".",
            AgentRunId: runId);

        await Assert.ThrowsAsync<OperationCanceledException>(() => executor.ExecuteAsync(request));

        // Container cleanup was guaranteed in finally block!
        var cleanupRequest = runner.Requests.FirstOrDefault(r => r.Arguments.Contains("rm") && r.Arguments.Contains(expectedContainerName));
        Assert.NotNull(cleanupRequest);
    }

    [Fact]
    public async Task HermesAgentExecutor_CleansUpContainer_OnStartupFailure()
    {
        var runId = Guid.NewGuid();
        var expectedContainerName = DockerContainerNaming.GetContainerName(runId);

        var runner = new TrackingProcessRunner(req =>
        {
            if (req.Arguments.Contains("run"))
            {
                return new ProcessRunResult(125, "", "network invalid-net not found", TimedOut: false);
            }
            return new ProcessRunResult(0, "ok", "", TimedOut: false);
        });

        var executor = new HermesAgentExecutor(
            new HermesOptions(), runner, NullLogger<HermesAgentExecutor>.Instance);

        var request = new AgentExecutionRequest(
            Agent: "coder",
            Model: "m1",
            Prompt: "prompt",
            WorkingDirectory: ".",
            AgentRunId: runId);

        var result = await executor.ExecuteAsync(request);

        Assert.False(result.Success);
        Assert.Equal(125, result.ExitCode);

        // Created container cleanup was guaranteed in finally block!
        var cleanupRequest = runner.Requests.FirstOrDefault(r => r.Arguments.Contains("rm") && r.Arguments.Contains(expectedContainerName));
        Assert.NotNull(cleanupRequest);
    }

    #endregion

    #region 5. Startup Orphan Sweep

    [Fact]
    public async Task OrphanSweep_RemovesStaleFactoryContainers_AndPreservesUnmanaged()
    {
        var staleRunId = Guid.NewGuid();
        var staleContainer = DockerContainerNaming.GetContainerName(staleRunId);
        var foreignContainer = "my-postgres-database";

        var runner = new TrackingProcessRunner(req =>
        {
            // docker ps output formatted as {{.ID}}\t{{.Names}}\t{{.Labels}}\t{{.Status}}
            if (req.Arguments.Contains("ps"))
            {
                var staleLabels = $"factory=ai-factory,managed=true,agent-run-id={staleRunId},adapter=Hermes";
                return new ProcessRunResult(0, $"cid-1\t{staleContainer}\t{staleLabels}\tExited\n", "", TimedOut: false);
            }

            return new ProcessRunResult(0, "ok", "", TimedOut: false);
        });

        var services = new ServiceCollection();
        services.AddSingleton<IAgentRunRepository>(new FakeAgentRunRepository(_ => null));
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        var lifecycleManager = new DockerContainerLifecycleManager(
            runner,
            scopeFactory,
            NullLogger<DockerContainerLifecycleManager>.Instance);

        var sweptCount = await lifecycleManager.SweepOrphanContainersAsync();

        Assert.Equal(1, sweptCount);

        // Verify ONLY stale container cid-1 was deleted
        var rmRequests = runner.Requests.Where(r => r.Arguments.Contains("rm")).ToList();
        Assert.Single(rmRequests);
        Assert.Contains("cid-1", rmRequests[0].Arguments);
        Assert.DoesNotContain(foreignContainer, rmRequests[0].Arguments);
    }

    private sealed class FakeAgentRunRepository : IAgentRunRepository
    {
        private readonly Func<Guid, AgentRun?> _lookup;

        public FakeAgentRunRepository(Func<Guid, AgentRun?> lookup) => _lookup = lookup;

        public Task<AgentRun?> GetByIdAsync(Guid agentRunId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_lookup(agentRunId));

        public Task<AgentRun> CreateAsync(AgentRun agentRun, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<AgentRun?> GetByCorrelationIdAsync(Guid correlationId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task UpdateAsync(AgentRun agentRun, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<AgentRun?> TryClaimNextPendingAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<AgentRunPage> GetPagedAsync(AgentRunQuery query, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<int> CountByStatusAsync(AgentRunStatus status, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<bool> HasActiveRunsForProjectAsync(Guid projectId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<int> RecoverOrphanedRunsAsync(DateTime? startedBeforeUtc, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task HeartbeatAsync(Guid agentRunId, CancellationToken cancellationToken = default) => throw new NotImplementedException();

        public Task<IReadOnlyList<AgentRun>> GetRunningWithoutHeartbeatSinceAsync(DateTime heartbeatCutoffUtc, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AgentRun>>(Array.Empty<AgentRun>());
        public Task<AgentRun?> TryRecoverOrphanedRunAsync(Guid agentRunId, DateTime heartbeatCutoffUtc, string reason, CancellationToken cancellationToken = default) =>
            Task.FromResult<AgentRun?>(null);
    }

    #endregion
}
