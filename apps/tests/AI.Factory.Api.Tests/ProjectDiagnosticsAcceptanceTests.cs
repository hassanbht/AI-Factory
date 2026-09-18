using System.Net;
using System.Text.Json;
using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Configuration;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using AI.Factory.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AI.Factory.Api.Tests;

/// <summary>
/// Workstream C — Diagnostics Acceptance
/// Validates GET /api/operations/projects/{projectId}/diagnostics
/// Asserts:
/// - Correct operational state exposed (WorkItem status counts, active/stale runs, duration, error info, attempt counts).
/// - Zero leakage of API keys, authentication tokens, passwords, or raw provider credentials.
/// </summary>
public sealed class ProjectDiagnosticsAcceptanceTests : IClassFixture<ApiTestFactory>
{
    private readonly ApiTestFactory _factory;
    private readonly HttpClient _client;

    public ProjectDiagnosticsAcceptanceTests(ApiTestFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task ProjectDiagnosticsEndpoint_ReturnsOperationalState_AndNeverLeaksSecrets()
    {
        var projectId = Guid.NewGuid();
        var workItemId = Guid.NewGuid();
        var agentRunId = Guid.NewGuid();

        // 1. Seed project with WorkItems, AgentRuns, and audit failure containing potential sensitive text
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FactoryDbContext>();

            var project = new Project
            {
                Id = projectId,
                Name = "Diagnostics Acceptance Project",
                Slug = $"diag-acceptance-{projectId:N}",
                RootPath = "/workspace/diag",
                Status = ProjectStatus.Implementation
            };
            db.Projects.Add(project);

            var workItem1 = new WorkItem
            {
                Id = workItemId,
                ProjectId = projectId,
                Title = "Implement Core Feature",
                Status = WorkItemStatus.Repairing.ToString(),
                AttemptCount = 2,
                RepairAttemptCount = 1,
                CurrentError = "Compilation error on line 42: sk-or-v1-fakeleaksecret1234567890",
                UpdatedAtUtc = DateTime.UtcNow
            };

            var workItem2 = new WorkItem
            {
                Id = Guid.NewGuid(),
                ProjectId = projectId,
                Title = "Documentation Task",
                Status = WorkItemStatus.Completed.ToString(),
                AttemptCount = 1,
                RepairAttemptCount = 0,
                UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-5)
            };

            db.WorkItems.AddRange(workItem1, workItem2);

            var agentDefinition = new AgentDefinition
            {
                Id = Guid.NewGuid(),
                Name = "Diagnostics Agent",
                AgentType = "Coding",
                Enabled = true,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            };
            db.AgentDefinitions.Add(agentDefinition);

            var agentRun = new AgentRun
            {
                Id = agentRunId,
                ProjectId = projectId,
                WorkItemId = workItemId,
                AgentDefinitionId = agentDefinition.Id,
                Status = AgentRunStatus.Running,
                Attempt = 2,
                StartedAtUtc = DateTime.UtcNow.AddMinutes(-3),
                UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-2),
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(-3)
            };
            db.AgentRuns.Add(agentRun);

            var auditEntry = new AuditLogEntry
            {
                Id = Guid.NewGuid(),
                ExecutionId = Guid.NewGuid(),
                ProjectId = projectId,
                EventType = AuditEventType.WorkflowFailed,
                Severity = AuditSeverity.Error,
                Message = "Authentication failed: Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9 with key sk-or-v1-secretkey9876543210",
                FailureCategory = FailureCategory.Provider,
                TimestampUtc = DateTime.UtcNow.AddMinutes(-1)
            };
            db.AuditLogEntries.Add(auditEntry);

            await db.SaveChangesAsync();
        }

        // 2. Request the diagnostics endpoint
        var response = await _client.GetAsync($"/api/operations/projects/{projectId}/diagnostics");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var rawJson = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(rawJson);
        var root = doc.RootElement;

        // 3. Assert WorkItem status counts and attempt counts
        Assert.True(root.TryGetProperty("projectId", out var projProp));
        Assert.Equal(projectId, projProp.GetGuid());

        Assert.True(root.TryGetProperty("projectStatus", out var statusProp));
        Assert.Equal("Implementation", statusProp.GetString());

        Assert.True(root.TryGetProperty("repairAttemptCount", out var repairAttemptProp));
        Assert.Equal(1, repairAttemptProp.GetInt32());

        Assert.True(root.TryGetProperty("attemptCount", out var attemptCountProp));
        Assert.Equal(2, attemptCountProp.GetInt32());

        Assert.True(root.TryGetProperty("workItemStatusCounts", out var countsProp));
        Assert.Equal(JsonValueKind.Object, countsProp.ValueKind);
        Assert.True(countsProp.TryGetProperty("Repairing", out var repairingCount));
        Assert.Equal(1, repairingCount.GetInt32());
        Assert.True(countsProp.TryGetProperty("Completed", out var completedCount));
        Assert.Equal(1, completedCount.GetInt32());

        // 4. Assert active/stale runs
        Assert.True(root.TryGetProperty("activeAgentRuns", out var activeRunsProp));
        Assert.Equal(1, activeRunsProp.GetInt32());

        Assert.True(root.TryGetProperty("staleAgentRuns", out var staleRunsProp));
        Assert.Equal(0, staleRunsProp.GetInt32());

        // 5. Assert recent error info is exposed
        Assert.True(root.TryGetProperty("lastFailureMessage", out var errorMsgProp));
        var errorMsg = errorMsgProp.GetString();
        Assert.NotNull(errorMsg);

        // 6. Security & Secret Boundary Assertions:
        // Must NEVER expose API keys, tokens, or credentials
        Assert.DoesNotContain("sk-or-v1-secretkey9876543210", rawJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9", rawJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sk-or-v1-fakeleaksecret1234567890", rawJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Bearer eyJ", rawJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[REDACTED]", rawJson);
    }
}
