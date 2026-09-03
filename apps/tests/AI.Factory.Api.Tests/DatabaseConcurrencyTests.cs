using AI.Factory.Application.Exceptions;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using AI.Factory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AI.Factory.Api.Tests;

public sealed class DatabaseConcurrencyTests
{
    private static DbContextOptions<FactoryDbContext> CreateOptions(SqliteConnection connection) =>
        new DbContextOptionsBuilder<FactoryDbContext>()
            .UseSqlite(connection)
            .Options;

    [Fact]
    public async Task ConcurrentUpdate_OnProject_ThrowsConcurrencyConflictException_AndPreventsLostUpdate()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = CreateOptions(connection);

        // 1. Initialize schema and seed project
        Guid projectId;
        await using (var dbInit = new FactoryDbContext(options))
        {
            await dbInit.Database.EnsureCreatedAsync();
            var project = new Project
            {
                Name = "Initial Name",
                Slug = "initial-slug",
                RootPath = "/initial"
            };
            await dbInit.Projects.AddAsync(project);
            await dbInit.SaveChangesAsync();
            projectId = project.Id;
            Assert.Equal(1L, project.Version);
        }

        // 2. Open Context A and Context B, both reading the same entity
        await using var contextA = new FactoryDbContext(options);
        await using var contextB = new FactoryDbContext(options);

        var projectA = await contextA.Projects.SingleAsync(x => x.Id == projectId);
        var projectB = await contextB.Projects.SingleAsync(x => x.Id == projectId);

        Assert.Equal(1L, projectA.Version);
        Assert.Equal(1L, projectB.Version);

        // 3. Context A modifies and saves first
        projectA.Name = "Updated by Context A";
        await contextA.SaveChangesAsync();
        Assert.Equal(2L, projectA.Version);

        // 4. Context B attempts to modify its stale copy and save
        projectB.Name = "Overwritten by Context B";

        var ex = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => contextB.SaveChangesAsync());

        Assert.Equal("Project", ex.ResourceType);
        Assert.Contains("The resource was changed by another operation", ex.Message);

        // 5. Verify database retained Context A's update and was NOT overwritten
        await using var contextVerify = new FactoryDbContext(options);
        var finalProject = await contextVerify.Projects.SingleAsync(x => x.Id == projectId);
        Assert.Equal("Updated by Context A", finalProject.Name);
        Assert.Equal(2L, finalProject.Version);
    }

    [Fact]
    public async Task ConcurrentUpdate_OnWorkItem_ThrowsConcurrencyConflictException()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = CreateOptions(connection);

        Guid workItemId;
        await using (var dbInit = new FactoryDbContext(options))
        {
            await dbInit.Database.EnsureCreatedAsync();

            var project = new Project
            {
                Name = "Project for WorkItem",
                Slug = "wi-project",
                RootPath = "/root"
            };
            await dbInit.Projects.AddAsync(project);

            var item = new WorkItem
            {
                ProjectId = project.Id,
                Title = "Test WorkItem",
                Type = WorkItemType.Implementation,
                Status = "Draft"
            };
            await dbInit.WorkItems.AddAsync(item);
            await dbInit.SaveChangesAsync();
            workItemId = item.Id;
        }

        await using var contextA = new FactoryDbContext(options);
        await using var contextB = new FactoryDbContext(options);

        var itemA = await contextA.WorkItems.SingleAsync(x => x.Id == workItemId);
        var itemB = await contextB.WorkItems.SingleAsync(x => x.Id == workItemId);

        itemA.Status = "Running";
        await contextA.SaveChangesAsync();

        itemB.Status = "Cancelled";
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => contextB.SaveChangesAsync());

        await using var contextVerify = new FactoryDbContext(options);
        var finalItem = await contextVerify.WorkItems.SingleAsync(x => x.Id == workItemId);
        Assert.Equal("Running", finalItem.Status);
    }

    [Fact]
    public async Task ConcurrentUpdate_OnApprovalRequest_ThrowsConcurrencyConflictException()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = CreateOptions(connection);

        Guid approvalId;
        await using (var dbInit = new FactoryDbContext(options))
        {
            await dbInit.Database.EnsureCreatedAsync();

            var project = new Project { Name = "Proj", Slug = "p", RootPath = "/r" };
            await dbInit.Projects.AddAsync(project);

            var approval = new ApprovalRequest
            {
                ProjectId = project.Id,
                Reason = "Review required",
                RequestedBy = "system",
                Workflow = ProjectStatus.HumanReview
            };
            await dbInit.ApprovalRequests.AddAsync(approval);
            await dbInit.SaveChangesAsync();
            approvalId = approval.Id;
        }

        await using var contextA = new FactoryDbContext(options);
        await using var contextB = new FactoryDbContext(options);

        var approvalA = await contextA.ApprovalRequests.SingleAsync(x => x.Id == approvalId);
        var approvalB = await contextB.ApprovalRequests.SingleAsync(x => x.Id == approvalId);

        approvalA.Decision = ApprovalDecision.Approved;
        await contextA.SaveChangesAsync();

        approvalB.Decision = ApprovalDecision.Rejected;
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => contextB.SaveChangesAsync());
    }
}
