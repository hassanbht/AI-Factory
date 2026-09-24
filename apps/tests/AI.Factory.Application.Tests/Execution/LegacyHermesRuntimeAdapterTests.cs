using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Abstractions.Execution;
using AI.Factory.Domain.Enums;
using AI.Factory.Infrastructure.Agents;
using AI.Factory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AI.Factory.Application.Tests.Execution;

/// <summary>
/// The transitional adapter's own mapping, asserted directly on
/// <see cref="ExecutionOutcome"/>. (End to end, the orchestrator derives
/// TimedOut/Cancelled from FailureCategory, so a wrong Status alone would be
/// invisible there — the golden equivalence test cannot see it.)
/// </summary>
public sealed class LegacyHermesRuntimeAdapterTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<FactoryDbContext> _options;

    public LegacyHermesRuntimeAdapterTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<FactoryDbContext>().UseSqlite(_connection).Options;
        using var db = new FactoryDbContext(_options);
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private sealed class StubExecutor : IAgentExecutor
    {
        private readonly Func<CancellationToken, AgentExecutionResult> _respond;

        public StubExecutor(Func<CancellationToken, AgentExecutionResult> respond) => _respond = respond;

        public AgentExecutionRequest? LastRequest { get; private set; }

        public Task<AgentExecutionResult> ExecuteAsync(AgentExecutionRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(_respond(cancellationToken));
        }
    }

    private static AgentExecutionSpec Spec(bool enabled = true, string? model = "m-1", string? workspace = null) => new(
        AgentRunId: Guid.NewGuid(),
        CorrelationId: Guid.NewGuid(),
        ProjectId: null,
        WorkItemId: null,
        Attempt: 1,
        Iteration: 0,
        Agent: new ExecutionAgent(Guid.NewGuid(), "Coder", "Implementation", "SYSTEM", enabled),
        Instructions: "INSTRUCTIONS",
        Workspace: new ExecutionWorkspace(workspace),
        Model: new ModelRoute(model, "gw-1"));

    private async Task<(ExecutionOutcome Outcome, StubExecutor Executor)> ExecuteAsync(
        AgentExecutionSpec spec, Func<CancellationToken, AgentExecutionResult> respond)
    {
        await using var db = new FactoryDbContext(_options);
        var executor = new StubExecutor(respond);
        var adapter = new LegacyHermesRuntimeAdapter(new HermesAgentRuntime(
            executor, new FailureClassifier(), new FileSystemArtifactRecorder(db), new ArtifactRepository(db),
            NullLogger<HermesAgentRuntime>.Instance));

        return (await adapter.ExecuteAsync(spec), executor);
    }

    [Fact]
    public void Identity_IsTheHermesKey_WithFactoryRoutedModelCapability()
    {
        Assert.Equal("Hermes", LegacyHermesRuntimeAdapter.AdapterKey);
        var adapter = new LegacyHermesRuntimeAdapter(null!);
        Assert.Equal("Hermes", adapter.Key);
        Assert.Equal(ExecutionCapabilities.FactoryRoutedModel, adapter.Capabilities);
    }

    [Fact]
    public async Task Success_MapsToSucceeded_AndDelegatesTheUnchangedHermesRequest()
    {
        var (outcome, executor) = await ExecuteAsync(
            Spec(workspace: "/ws"), _ => new AgentExecutionResult(true, "out", null, 12, "/ws"));

        Assert.Equal(ExecutionOutcomeStatus.Succeeded, outcome.Status);
        Assert.Equal("out", outcome.Output);
        Assert.Null(outcome.FailureCategory);
        Assert.Equal(12, outcome.DurationMs);
        Assert.Null(outcome.Usage);
        Assert.Equal(new ModelRoute("m-1", "gw-1"), outcome.Model);

        // HermesAgentRuntime's own prompt/model/workspace construction, untouched.
        Assert.Equal("Coder", executor.LastRequest!.Agent);
        Assert.Equal("m-1", executor.LastRequest.Model);
        Assert.Equal("SYSTEM\n\nINSTRUCTIONS", executor.LastRequest.Prompt);
        Assert.Equal("/ws", executor.LastRequest.WorkingDirectory);
    }

    [Fact]
    public async Task NoWorkspace_StillReachesHermesAsEmptyWorkingDirectory()
    {
        var (_, executor) = await ExecuteAsync(Spec(workspace: null), _ => new AgentExecutionResult(true, "o", null, 1));

        Assert.Equal(string.Empty, executor.LastRequest!.WorkingDirectory);
    }

    [Fact]
    public async Task Timeout_MapsToTimedOut()
    {
        var (outcome, _) = await ExecuteAsync(
            Spec(), _ => new AgentExecutionResult(false, null, "Execution timed out after 300 seconds.", 300_000));

        Assert.Equal(ExecutionOutcomeStatus.TimedOut, outcome.Status);
        Assert.Equal(FailureCategory.Timeout, outcome.FailureCategory);
    }

    [Fact]
    public async Task ProcessFailure_MapsToFailed_WithAgentCategory()
    {
        var (outcome, _) = await ExecuteAsync(Spec(), _ => new AgentExecutionResult(false, "o", "stderr", 5));

        Assert.Equal(ExecutionOutcomeStatus.Failed, outcome.Status);
        Assert.Equal(FailureCategory.Agent, outcome.FailureCategory);
        Assert.Equal("stderr", outcome.ErrorMessage);
    }

    [Fact]
    public async Task CancelledExecution_MapsToCancelled()
    {
        using var cts = new CancellationTokenSource();
        await using var db = new FactoryDbContext(_options);
        var adapter = new LegacyHermesRuntimeAdapter(new HermesAgentRuntime(
            new StubExecutor(ct =>
            {
                cts.Cancel();
                ct.ThrowIfCancellationRequested();
                return new AgentExecutionResult(true, "unreachable", null, 1);
            }),
            new FailureClassifier(), new FileSystemArtifactRecorder(db), new ArtifactRepository(db),
            NullLogger<HermesAgentRuntime>.Instance));

        var outcome = await adapter.ExecuteAsync(Spec(), cts.Token);

        Assert.Equal(ExecutionOutcomeStatus.Cancelled, outcome.Status);
        Assert.Equal(FailureCategory.Cancelled, outcome.FailureCategory);
    }

    [Fact]
    public async Task DisabledDefinition_IsStillRejectedByHermesAgentRuntime_AsValidation()
    {
        var (outcome, executor) = await ExecuteAsync(Spec(enabled: false), _ => new AgentExecutionResult(true, "o", null, 1));

        Assert.Equal(ExecutionOutcomeStatus.Failed, outcome.Status);
        Assert.Equal(FailureCategory.Validation, outcome.FailureCategory);
        Assert.Null(executor.LastRequest);
    }
}
