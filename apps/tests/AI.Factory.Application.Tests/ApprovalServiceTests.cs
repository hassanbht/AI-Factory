using AI.Factory.Application.Approvals;
using AI.Factory.Application.Exceptions;
using AI.Factory.Application.Orchestration;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using AI.Factory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AI.Factory.Application.Tests;

public sealed class ApprovalServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<FactoryDbContext> _options;
    private readonly Guid _projectId = Guid.NewGuid();

    public ApprovalServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<FactoryDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var db = new FactoryDbContext(_options);
        db.Database.EnsureCreated();

        db.Projects.Add(new Project
        {
            Id = _projectId,
            Name = "Approval Test",
            Slug = "approval-test",
            Status = ProjectStatus.Implementation,
            RootPath = "unused",
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        });

        db.SaveChanges();
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    private ApprovalService MakeService(FactoryDbContext db)
    {
        var workflowEngine = new WorkflowEngine(new WorkflowStateStore(db), new WorkflowTransitionValidator());
        return new ApprovalService(new ApprovalRepository(db), workflowEngine);
    }

    [Fact]
    public async Task RequestApprovalAsync_PersistsRecordAndPausesWorkflow_AcrossNewDbContextInstance()
    {
        await using (var writeDb = new FactoryDbContext(_options))
        {
            var service = MakeService(writeDb);

            await service.RequestApprovalAsync(
                _projectId, "Repair attempts exhausted.", "system");
        }

        // A brand-new DbContext instance stands in for a process restart.
        await using var readDb = new FactoryDbContext(_options);

        var project = await readDb.Projects.SingleAsync(x => x.Id == _projectId);
        Assert.Equal(ProjectStatus.HumanReview, project.Status);

        var approval = await readDb.ApprovalRequests.SingleAsync(x => x.ProjectId == _projectId);
        Assert.Equal(ApprovalDecision.PendingApproval, approval.Decision);
        Assert.Equal(ProjectStatus.Implementation, approval.Workflow);
        Assert.Equal("Repair attempts exhausted.", approval.Reason);
        Assert.Equal("system", approval.RequestedBy);
        Assert.Null(approval.DecidedAtUtc);
    }

    [Fact]
    public async Task Workflow_RemainsPausedInHumanReview_UntilAnExplicitDecisionIsMade()
    {
        await using (var db1 = new FactoryDbContext(_options))
        {
            await MakeService(db1).RequestApprovalAsync(_projectId, "needs review", "system");
        }

        // Read again, independently, after the "pause" — nothing in the
        // system automatically advances a HumanReview project. Repeated
        // fresh reads all show it still paused.
        for (var i = 0; i < 3; i++)
        {
            await using var db = new FactoryDbContext(_options);
            var project = await db.Projects.SingleAsync(x => x.Id == _projectId);
            Assert.Equal(ProjectStatus.HumanReview, project.Status);

            var pending = await MakeService(db).GetPendingAsync();
            Assert.Single(pending);
        }
    }

    [Fact]
    public async Task ApproveAsync_TransitionsWorkflowToCompleted_AndPersistsDecision()
    {
        Guid approvalId;

        await using (var db1 = new FactoryDbContext(_options))
        {
            var request = await MakeService(db1).RequestApprovalAsync(_projectId, "needs review", "system");
            approvalId = request.Id;
        }

        await using (var db2 = new FactoryDbContext(_options))
        {
            await MakeService(db2).ApproveAsync(approvalId, "looks good");
        }

        await using var readDb = new FactoryDbContext(_options);

        var project = await readDb.Projects.SingleAsync(x => x.Id == _projectId);
        Assert.Equal(ProjectStatus.Completed, project.Status);

        var approval = await readDb.ApprovalRequests.SingleAsync(x => x.Id == approvalId);
        Assert.Equal(ApprovalDecision.Approved, approval.Decision);
        Assert.Equal("looks good", approval.Comment);
        Assert.NotNull(approval.DecidedAtUtc);

        var pending = await MakeService(readDb).GetPendingAsync();
        Assert.Empty(pending);
    }

    [Fact]
    public async Task RejectAsync_TransitionsWorkflowToFailed_AndPersistsDecision()
    {
        Guid approvalId;

        await using (var db1 = new FactoryDbContext(_options))
        {
            var request = await MakeService(db1).RequestApprovalAsync(_projectId, "needs review", "system");
            approvalId = request.Id;
        }

        await using (var db2 = new FactoryDbContext(_options))
        {
            await MakeService(db2).RejectAsync(approvalId, "not acceptable");
        }

        await using var readDb = new FactoryDbContext(_options);

        var project = await readDb.Projects.SingleAsync(x => x.Id == _projectId);
        Assert.Equal(ProjectStatus.Failed, project.Status);

        var approval = await readDb.ApprovalRequests.SingleAsync(x => x.Id == approvalId);
        Assert.Equal(ApprovalDecision.Rejected, approval.Decision);
        Assert.Equal("not acceptable", approval.Comment);
    }

    [Fact]
    public async Task ApproveAsync_Throws_WhenAlreadyDecided()
    {
        Guid approvalId;

        await using (var db1 = new FactoryDbContext(_options))
        {
            var request = await MakeService(db1).RequestApprovalAsync(_projectId, "needs review", "system");
            approvalId = request.Id;
        }

        await using var db = new FactoryDbContext(_options);
        var service = MakeService(db);

        await service.ApproveAsync(approvalId, "first decision");

        await Assert.ThrowsAsync<ConflictException>(() =>
            service.ApproveAsync(approvalId, "second decision"));
    }
}
