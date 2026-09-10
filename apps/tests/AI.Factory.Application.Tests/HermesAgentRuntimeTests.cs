using AI.Factory.Application.Abstractions;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using AI.Factory.Infrastructure.Agents;
using AI.Factory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AI.Factory.Application.Tests;

public sealed class HermesAgentRuntimeTests : IDisposable
{
    private sealed class FakeAgentExecutor : IAgentExecutor
    {
        private readonly Func<AgentExecutionRequest, AgentExecutionResult>? _respond;
        private readonly Exception? _throwOnExecute;

        public FakeAgentExecutor(Func<AgentExecutionRequest, AgentExecutionResult> respond)
        {
            _respond = respond;
        }

        public FakeAgentExecutor(Exception throwOnExecute)
        {
            _throwOnExecute = throwOnExecute;
        }

        public bool WasCalled { get; private set; }

        public AgentExecutionRequest? LastRequest { get; private set; }

        public Task<AgentExecutionResult> ExecuteAsync(
            AgentExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            LastRequest = request;

            if (_throwOnExecute is not null)
            {
                throw _throwOnExecute;
            }

            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_respond!(request));
        }
    }

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<FactoryDbContext> _options;
    private readonly IFailureClassifier _failureClassifier = new FailureClassifier();

    public HermesAgentRuntimeTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<FactoryDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var db = new FactoryDbContext(_options);
        db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    private static AgentDefinition MakeDefinition(bool enabled = true) => new()
    {
        Name = "Coder",
        Description = "Implements things",
        AgentType = "Coding",
        SystemPrompt = "You are a coding agent.",
        Enabled = enabled
    };

    private static AgentRun MakeRun(AgentDefinition definition, Guid? projectId = null, Guid? workItemId = null) => new()
    {
        AgentDefinitionId = definition.Id,
        AgentDefinition = definition,
        Provider = "test-provider",
        Model = "test-model",
        Input = "do the thing",
        ProjectId = projectId,
        WorkItemId = workItemId
    };

    private HermesAgentRuntime MakeRuntime(IAgentExecutor executor, FactoryDbContext db) =>
        new(
            executor,
            _failureClassifier,
            new FileSystemArtifactRecorder(db),
            new ArtifactRepository(db),
            NullLogger<HermesAgentRuntime>.Instance);

    [Fact]
    public async Task ExecuteAsync_OnSuccess_ReturnsSuccessWithOutputProviderModelAndDuration()
    {
        var definition = MakeDefinition();
        var run = MakeRun(definition);
        var executor = new FakeAgentExecutor(_ => new AgentExecutionResult(true, "hello world", null, 42, "/workspace/x"));

        await using var db = new FactoryDbContext(_options);
        var runtime = MakeRuntime(executor, db);

        var result = await runtime.ExecuteAsync(run);

        Assert.True(result.Success);
        Assert.Equal("hello world", result.Output);
        Assert.Equal("test-provider", result.Provider);
        Assert.Equal("test-model", result.Model);
        Assert.Equal(42, result.DurationMs);
        Assert.Null(result.ErrorMessage);
        Assert.Null(result.FailureCategory);
        Assert.True(executor.WasCalled);
    }

    [Fact]
    public async Task ExecuteAsync_BuildsPrompt_FromSystemPromptAndInput()
    {
        var definition = MakeDefinition();
        var run = MakeRun(definition);
        var executor = new FakeAgentExecutor(_ => new AgentExecutionResult(true, "ok", null, 1));

        await using var db = new FactoryDbContext(_options);
        var runtime = MakeRuntime(executor, db);

        await runtime.ExecuteAsync(run);

        Assert.NotNull(executor.LastRequest);
        Assert.Contains(definition.SystemPrompt, executor.LastRequest!.Prompt);
        Assert.Contains(run.Input!, executor.LastRequest.Prompt);
        Assert.Equal(definition.Name, executor.LastRequest.Agent);
    }

    [Fact]
    public async Task ExecuteAsync_OnNonZeroExit_ReturnsFailureWithAgentCategory()
    {
        var definition = MakeDefinition();
        var run = MakeRun(definition);
        var executor = new FakeAgentExecutor(_ => new AgentExecutionResult(false, null, "Process exited with code 1.", 15));

        await using var db = new FactoryDbContext(_options);
        var runtime = MakeRuntime(executor, db);

        var result = await runtime.ExecuteAsync(run);

        Assert.False(result.Success);
        Assert.Equal("Process exited with code 1.", result.ErrorMessage);
        Assert.Equal(FailureCategory.Agent, result.FailureCategory);
        Assert.Equal(15, result.DurationMs);
    }

    [Fact]
    public async Task ExecuteAsync_OnTimeoutErrorText_ClassifiesAsTimedOut_NotGenericFailure()
    {
        var definition = MakeDefinition();
        var run = MakeRun(definition);
        var executor = new FakeAgentExecutor(_ => new AgentExecutionResult(
            false, null, "Execution timed out after 300 seconds.", 300_000));

        await using var db = new FactoryDbContext(_options);
        var runtime = MakeRuntime(executor, db);

        var result = await runtime.ExecuteAsync(run);

        Assert.False(result.Success);
        Assert.Equal(FailureCategory.Timeout, result.FailureCategory);
    }

    [Fact]
    public async Task ExecuteAsync_OnCancellation_ReturnsCancelledResult_DoesNotThrow()
    {
        var definition = MakeDefinition();
        var run = MakeRun(definition);
        var executor = new FakeAgentExecutor(_ => new AgentExecutionResult(true, "unreachable", null, 0));

        await using var db = new FactoryDbContext(_options);
        var runtime = MakeRuntime(executor, db);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await runtime.ExecuteAsync(run, cts.Token);

        Assert.False(result.Success);
        Assert.Equal(FailureCategory.Cancelled, result.FailureCategory);
        Assert.Equal("Agent execution was cancelled.", result.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteAsync_WhenAgentDefinitionDisabled_ReturnsValidationFailure_WithoutCallingExecutor()
    {
        var definition = MakeDefinition(enabled: false);
        var run = MakeRun(definition);
        var executor = new FakeAgentExecutor(_ => throw new InvalidOperationException("should not be called"));

        await using var db = new FactoryDbContext(_options);
        var runtime = MakeRuntime(executor, db);

        var result = await runtime.ExecuteAsync(run);

        Assert.False(result.Success);
        Assert.Equal(FailureCategory.Validation, result.FailureCategory);
        Assert.Contains(definition.Name, result.ErrorMessage);
        Assert.False(executor.WasCalled);
    }

    [Fact]
    public async Task ExecuteAsync_WithoutLoadedAgentDefinition_ThrowsArgumentException()
    {
        var run = new AgentRun { AgentDefinitionId = Guid.NewGuid(), AgentDefinition = null };
        var executor = new FakeAgentExecutor(_ => throw new InvalidOperationException("should not be called"));

        await using var db = new FactoryDbContext(_options);
        var runtime = MakeRuntime(executor, db);

        await Assert.ThrowsAsync<ArgumentException>(() => runtime.ExecuteAsync(run));
    }

    [Fact]
    public async Task ExecuteAsync_WithoutProjectOrWorkItemId_ReturnsEmptyArtifactReferences()
    {
        var definition = MakeDefinition();
        var run = MakeRun(definition);
        var executor = new FakeAgentExecutor(_ => new AgentExecutionResult(true, "ok", null, 1, "/workspace/x"));

        await using var db = new FactoryDbContext(_options);
        var runtime = MakeRuntime(executor, db);

        var result = await runtime.ExecuteAsync(run);

        Assert.Empty(result.ArtifactReferences);
    }

    [Fact]
    public async Task ExecuteAsync_WithProjectAndWorkItemId_RecordsAndReturnsArtifactReferences()
    {
        var workspaceDir = Path.Combine(Path.GetTempPath(), "ai-factory-hermes-runtime-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspaceDir);
        await File.WriteAllTextAsync(Path.Combine(workspaceDir, "output.cs"), "// generated");

        var projectId = Guid.NewGuid();
        var workItemId = Guid.NewGuid();

        await using (var seedDb = new FactoryDbContext(_options))
        {
            seedDb.Projects.Add(new Project
            {
                Id = projectId,
                Name = "Runtime Test",
                Slug = "runtime-test",
                RootPath = workspaceDir,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            });
            await seedDb.SaveChangesAsync();
        }

        var definition = MakeDefinition();
        var run = MakeRun(definition, projectId, workItemId);
        var executor = new FakeAgentExecutor(_ => new AgentExecutionResult(true, "ok", null, 1, workspaceDir));

        await using var db = new FactoryDbContext(_options);
        var runtime = MakeRuntime(executor, db);

        var result = await runtime.ExecuteAsync(run);

        Assert.Single(result.ArtifactReferences);

        Directory.Delete(workspaceDir, recursive: true);
    }
}
