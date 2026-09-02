using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Learning;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using AI.Factory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AI.Factory.Application.Tests;

public sealed class LearningServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<FactoryDbContext> _options;
    private readonly Guid _projectId = Guid.NewGuid();

    public LearningServiceTests()
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
            Name = "Learning Test",
            Slug = "learning-test",
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

    private static readonly EvaluationResult FullyValidated = new(
        Guid.NewGuid(), true, true, false, null, 0, RegressionStatus.None, IsFullyValidated: true);

    private static readonly EvaluationResult NotFullyValidated = new(
        Guid.NewGuid(), true, false, false, null, 1, RegressionStatus.None, IsFullyValidated: false);

    [Fact]
    public async Task RecordLessonAsync_ReturnsNull_WhenEvidenceIsMissing()
    {
        await using var db = new FactoryDbContext(_options);
        var service = new LearningService(new LearningStore(db));

        var lesson = await service.RecordLessonAsync(
            _projectId, "Implementation", "problem", "solution",
            evidence: "", confidence: 0.9, source: "agent-run:1", evaluation: FullyValidated);

        Assert.Null(lesson);
        Assert.Empty(await db.Lessons.ToListAsync());
    }

    [Fact]
    public async Task RecordLessonAsync_StoresAsVerified_WhenEvaluationIsFullyValidated()
    {
        await using var db = new FactoryDbContext(_options);
        var service = new LearningService(new LearningStore(db));

        var lesson = await service.RecordLessonAsync(
            _projectId, "Implementation", "problem", "solution",
            evidence: "build+test+review all passed", confidence: 0.95,
            source: "validation-run:1", evaluation: FullyValidated);

        Assert.NotNull(lesson);
        Assert.Equal(KnowledgeLevel.Verified, lesson!.Level);
    }

    [Fact]
    public async Task RecordLessonAsync_StoresAsInferred_WhenEvaluationExistsButIsNotFullyValidated()
    {
        await using var db = new FactoryDbContext(_options);
        var service = new LearningService(new LearningStore(db));

        var lesson = await service.RecordLessonAsync(
            _projectId, "Implementation", "problem", "solution",
            evidence: "tests failed once", confidence: 0.5,
            source: "validation-run:2", evaluation: NotFullyValidated);

        Assert.NotNull(lesson);
        Assert.Equal(KnowledgeLevel.Inferred, lesson!.Level);
    }

    [Fact]
    public async Task RecordLessonAsync_StoresAsObserved_WhenNoEvaluationIsSupplied()
    {
        await using var db = new FactoryDbContext(_options);
        var service = new LearningService(new LearningStore(db));

        var lesson = await service.RecordLessonAsync(
            _projectId, "Implementation", "problem", "solution",
            evidence: "raw agent output", confidence: 0.3,
            source: "agent-run:3", evaluation: null);

        Assert.NotNull(lesson);
        Assert.Equal(KnowledgeLevel.Observed, lesson!.Level);
    }

    [Fact]
    public async Task RecordLessonAsync_NeverMarksVerified_WithoutAFullyValidatedEvaluation()
    {
        // Guards against storing an LLM hallucination as a verified lesson:
        // high confidence alone must never produce Verified.
        await using var db = new FactoryDbContext(_options);
        var service = new LearningService(new LearningStore(db));

        var lesson = await service.RecordLessonAsync(
            _projectId, "Implementation", "problem", "solution",
            evidence: "the agent said it was confident", confidence: 0.99,
            source: "agent-run:4", evaluation: null);

        Assert.NotEqual(KnowledgeLevel.Verified, lesson!.Level);
    }

    [Fact]
    public async Task GetByProjectAsync_ReturnsLessons_AcrossNewDbContextInstance()
    {
        await using (var writeDb = new FactoryDbContext(_options))
        {
            var service = new LearningService(new LearningStore(writeDb));
            await service.RecordLessonAsync(
                _projectId, "Implementation", "p1", "s1", "evidence1", 0.9, "src1", FullyValidated);
            await service.RecordLessonAsync(
                _projectId, "Testing", "p2", "s2", "evidence2", 0.4, "src2", null);
        }

        await using var readDb = new FactoryDbContext(_options);
        var readService = new LearningService(new LearningStore(readDb));

        var lessons = await readService.GetByProjectAsync(_projectId);

        Assert.Equal(2, lessons.Count);
    }

    [Fact]
    public async Task RecordLessonAsync_IsIncremental_MultipleRecordsDoNotOverwriteEachOther()
    {
        await using var db = new FactoryDbContext(_options);
        var service = new LearningService(new LearningStore(db));

        var first = await service.RecordLessonAsync(
            _projectId, "Implementation", "p1", "s1", "evidence1", 0.9, "src1", FullyValidated);
        var second = await service.RecordLessonAsync(
            _projectId, "Implementation", "p2", "s2", "evidence2", 0.9, "src2", FullyValidated);

        var all = await db.Lessons.ToListAsync();

        Assert.Equal(2, all.Count);
        Assert.Contains(all, l => l.Id == first!.Id);
        Assert.Contains(all, l => l.Id == second!.Id);
    }
}
