using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Abstractions.Execution;
using AI.Factory.Application.CostAccounting;
using AI.Factory.Domain.Entities;
using AI.Factory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AI.Factory.Application.Tests.CostAccounting;

/// <summary>
/// Phase Q.3 — durable generation-to-AgentRun correlation, authoritative usage
/// validation, fail-closed budget guard, redaction, resume and the no-live-egress
/// invariant. All provider HTTP is mocked; no test here may contact OpenRouter.
/// </summary>
public sealed partial class PhaseQ3CostCaptureTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<FactoryDbContext> _options;

    public PhaseQ3CostCaptureTests()
    {
        PhaseQ3LiveEgressGuard.AssertNoLiveEgress();
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<FactoryDbContext>().UseSqlite(_connection).Options;
        using var db = new FactoryDbContext(_options);
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private ProviderGenerationLedger Ledger(FactoryDbContext db) => new(db);

    private static ProviderGeneration Row(string generation, Guid agentRun, string? epoch = null, string? identity = null) =>
        new()
        {
            CertificationEpoch = epoch,
            BenchmarkIdentity = identity,
            AgentRunId = agentRun,
            GenerationId = generation,
            ModelId = "deepseek/deepseek-v4-flash-0731",
            Provider = "openrouter",
            RequestedAtUtc = DateTime.UtcNow,
            AccountingStatus = ProviderGenerationAccountingStatus.PendingUsage
        };

    private static ProviderGeneration VerifiedRow(string generation, decimal cost) =>
        new()
        {
            AgentRunId = Guid.NewGuid(),
            GenerationId = generation,
            RequestedAtUtc = DateTime.UtcNow,
            AccountingStatus = ProviderGenerationAccountingStatus.Verified,
            AuthoritativeCostUsd = cost
        };

    [Fact]
    public async Task ValidCapture_And_ValidUsage_Verifies()
    {
        PhaseQ3LiveEgressGuard.AssertNoLiveEgress();
        var captured = OpenRouterGenerationCapture.CaptureFromResponse(
            new Dictionary<string, string> { ["X-Generation-Id"] = "gen-AbC12345" },
            """{"id":"gen-AbC12345","model":"m","usage":{"prompt_tokens":10,"completion_tokens":5,"total_tokens":15}}""",
            false);
        Assert.Single(captured);

        var validation = OpenRouterGenerationUsageValidator.Validate(
            "gen-AbC12345", 200,
            """{"data":{"id":"gen-AbC12345","model":"m","tokens_prompt":10,"tokens_completion":5,"total_cost":0.0015}}""");
        Assert.True(validation.Ok);
        Assert.Equal(0.0015m, validation.Usage!.TotalCostUsd);

        await using var db = new FactoryDbContext(_options);
        var row = await Ledger(db).RecordGenerationAsync(Row("gen-AbC12345", Guid.NewGuid()));
        var applied = await Ledger(db).ApplyAuthoritativeUsageAsync("gen-AbC12345",
            new ProviderGenerationUsageOutcome("gen-AbC12345", "gen-AbC12345", "m",
                10, 5, 15, validation.Usage.TotalCostUsd, ProviderGenerationAccountingStatus.Verified));
        Assert.Equal(ProviderGenerationAccountingStatus.Verified, applied.AccountingStatus);
        Assert.Equal(0.0015m, applied.AuthoritativeCostUsd);
    }

    [Fact]
    public async Task RecordGeneration_PreservesEpochIdentityAndRunCorrelation()
    {
        PhaseQ3LiveEgressGuard.AssertNoLiveEgress();
        var agentRun = Guid.NewGuid();
        var workItem = Guid.NewGuid();
        await using var db = new FactoryDbContext(_options);
        await Ledger(db).RecordGenerationAsync(new ProviderGeneration
        {
            CertificationEpoch = "PhaseQ-Certification-Epoch-2",
            BenchmarkIdentity = "Q-A-1",
            WorkItemId = workItem,
            AgentRunId = agentRun,
            GenerationId = "gen-epochmap1",
            ModelId = "m",
            Provider = "openrouter",
            RequestedAtUtc = DateTime.UtcNow,
            AccountingStatus = ProviderGenerationAccountingStatus.PendingUsage
        });
        var byRun = await Ledger(db).ListByAgentRunAsync(agentRun);
        Assert.Single(byRun);
        Assert.Equal("Q-A-1", byRun[0].BenchmarkIdentity);
    }

    [Fact]
    public async Task OneAgentRun_MayOwnMultipleGenerations()
    {
        PhaseQ3LiveEgressGuard.AssertNoLiveEgress();
        var agentRun = Guid.NewGuid();
        await using var db = new FactoryDbContext(_options);
        await Ledger(db).RecordGenerationAsync(Row("gen-multi-aa", agentRun));
        await Ledger(db).RecordGenerationAsync(Row("gen-multi-ab", agentRun));
        Assert.Equal(2, (await Ledger(db).ListByAgentRunAsync(agentRun)).Count);
    }

    [Fact]
    public async Task DuplicateGenerationId_IsIdempotent_ButNeverReassigned()
    {
        PhaseQ3LiveEgressGuard.AssertNoLiveEgress();
        var agentRun = Guid.NewGuid();
        await using var db = new FactoryDbContext(_options);
        var first = await Ledger(db).RecordGenerationAsync(Row("gen-dup-001", agentRun, "E", "Q-A-1"));
        var second = await Ledger(db).RecordGenerationAsync(Row("gen-dup-001", agentRun, "E", "Q-A-1"));
        Assert.Equal(first.Id, second.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Ledger(db).RecordGenerationAsync(Row("gen-dup-001", Guid.NewGuid(), "E", "Q-A-2")));
    }
}
