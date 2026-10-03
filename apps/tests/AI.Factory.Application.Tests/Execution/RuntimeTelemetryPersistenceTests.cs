using System.Text.Json;
using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Abstractions.Execution;
using AI.Factory.Application.Agents;
using AI.Factory.Application.Configuration;
using AI.Factory.Application.Orchestration;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using AI.Factory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AI.Factory.Application.Tests.Execution;

/// <summary>
/// Phase M — usage/cost/session/request metadata the runtime actually exposed is persisted on the AgentRun
/// (token columns + structured-result metadata); what it did not expose stays absent — never zero, never
/// inferred. The benchmark scored the Factory's audit 2/2 but noted "no token/cost data".
/// </summary>
public sealed class RuntimeTelemetryPersistenceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<FactoryDbContext> _options;

    public RuntimeTelemetryPersistenceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<FactoryDbContext>().UseSqlite(_connection).Options;
        using var db = new FactoryDbContext(_options);
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private sealed class ReportingRuntime : IAgentRuntime
    {
        private readonly ExecutionRuntimeReport? _report;
        private readonly (int? In, int? Out, int? Total) _tokens;

        public ReportingRuntime(ExecutionRuntimeReport? report, (int?, int?, int?) tokens)
        {
            _report = report;
            _tokens = tokens;
        }

        public Task<AgentRunExecutionResult> ExecuteAsync(AgentRun agentRun, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AgentRunExecutionResult(
                Success: true, Output: "{}", PromptTokens: _tokens.In, CompletionTokens: _tokens.Out, TotalTokens: _tokens.Total,
                DurationMs: 5, ArtifactReferences: Array.Empty<string>(), ToolCallNames: Array.Empty<string>(),
                ErrorMessage: null, FailureCategory: null, Provider: "anthropic", Model: "claude-haiku-4-5",
                ExecutionAdapter: "ClaudeCode", ExitReason: "exit:0", RuntimeReport: _report));
    }

    private async Task<AgentRun> ExecuteWithAsync(IAgentRuntime runtime)
    {
        Guid runId;
        await using (var db = new FactoryDbContext(_options))
        {
            var definition = await new AgentDefinitionRepository(db).CreateAsync(
                new AgentDefinition { Name = $"d-{Guid.NewGuid():N}", AgentType = "Implementation", SystemPrompt = "x" });
            var run = await new AgentRunRepository(db).CreateAsync(new AgentRun { AgentDefinitionId = definition.Id, Model = "factory/model-a", Input = "go" });
            run.TransitionTo(AgentRunStatus.Queued);
            await db.SaveChangesAsync();
            runId = run.Id;
        }

        await using (var db = new FactoryDbContext(_options))
        {
            await new AgentOrchestrator(
                new AgentRunRepository(db), new ProjectRepository(db), new FakeAgentToolRegistry("ReadArtifact"),
                new FakeAgentMemory(), runtime, new ProjectWorkspaceResolver(new AgentWorkspaceOptions()),
                new AgentInstructionBuilder(), new StructuredAgentResultParser(),
                NullLogger<AgentOrchestrator>.Instance).ExecuteAsync(runId);
        }

        await using var verify = new FactoryDbContext(_options);
        return await verify.AgentRuns.AsNoTracking().SingleAsync(x => x.Id == runId);
    }

    private static IReadOnlyDictionary<string, string> Metadata(AgentRun run) =>
        JsonSerializer.Deserialize<StructuredAgentResult>(run.StructuredResult!, StructuredAgentResult.JsonOptions)!.Metadata!;

    [Fact]
    public async Task ExposedTelemetry_IsPersistedOnTheAgentRun()
    {
        var started = new DateTimeOffset(2026, 10, 2, 10, 0, 0, TimeSpan.Zero);
        var run = await ExecuteWithAsync(new ReportingRuntime(
            new ExecutionRuntimeReport(
                "ClaudeCode", RuntimeVersion: "2.1.121", SessionId: "sess-1", StartedAt: started, CompletedAt: started.AddSeconds(42),
                ExitCode: 0, CostUsd: 0.0123m, CostSource: "claude:total_cost_usd", UpstreamRequestId: "req_1", Turns: 3,
                ReportedModel: "claude-haiku-4-5"),
            (100, 20, 120)));

        Assert.Equal(AgentRunStatus.Succeeded, run.Status);
        Assert.Equal((100, 20, 120), (run.PromptTokens, run.CompletionTokens, run.TotalTokens));

        var metadata = Metadata(run);
        Assert.Equal("ClaudeCode", metadata["ExecutionAdapter"]);
        Assert.Equal("anthropic", metadata["Gateway"]);
        Assert.Equal("sess-1", metadata["RuntimeSessionId"]);
        Assert.Equal("0.0123", metadata["CostUsd"]);
        Assert.Equal("claude:total_cost_usd", metadata["CostSource"]);
        Assert.Equal("req_1", metadata["UpstreamRequestId"]);
        Assert.Equal("3", metadata["RuntimeTurns"]);
        Assert.Equal("2.1.121", metadata["RuntimeVersion"]);
        Assert.Equal("0", metadata["RuntimeExitCode"]);
        Assert.Equal("2026-10-02T10:00:00.0000000Z", metadata["RuntimeStartedAtUtc"]);
        Assert.Equal("2026-10-02T10:00:42.0000000Z", metadata["RuntimeCompletedAtUtc"]);
    }

    [Fact]
    public async Task UnexposedTelemetry_StaysAbsent_NeverZero()
    {
        var run = await ExecuteWithAsync(new ReportingRuntime(new ExecutionRuntimeReport("Codex"), (null, null, null)));

        Assert.Null(run.PromptTokens);
        Assert.Null(run.TotalTokens);

        var metadata = Metadata(run);
        Assert.False(metadata.ContainsKey("CostUsd"));
        Assert.False(metadata.ContainsKey("CostSource"));
        Assert.False(metadata.ContainsKey("RuntimeSessionId"));
        Assert.False(metadata.ContainsKey("UpstreamRequestId"));
    }
}
