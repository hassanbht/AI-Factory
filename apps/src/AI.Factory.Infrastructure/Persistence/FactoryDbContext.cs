using AI.Factory.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AI.Factory.Infrastructure.Persistence;

public sealed class FactoryDbContext : DbContext
{
    public FactoryDbContext(DbContextOptions<FactoryDbContext> options)
        : base(options)
    {
    }

    public DbSet<Project> Projects => Set<Project>();

    public DbSet<Requirement> Requirements => Set<Requirement>();

    public DbSet<Feature> Features => Set<Feature>();

    public DbSet<WorkItem> WorkItems => Set<WorkItem>();

    public DbSet<WorkItemDependency> WorkItemDependencies =>
        Set<WorkItemDependency>();

    public DbSet<AgentRun> AgentRuns => Set<AgentRun>();

    public DbSet<Artifact> Artifacts => Set<Artifact>();

    public DbSet<WorkflowTransition> WorkflowTransitions => Set<WorkflowTransition>();

    public DbSet<ValidationRun> ValidationRuns => Set<ValidationRun>();

    public DbSet<ReviewRun> ReviewRuns => Set<ReviewRun>();

    public DbSet<ApprovalRequest> ApprovalRequests => Set<ApprovalRequest>();

    public DbSet<Lesson> Lessons => Set<Lesson>();

    public DbSet<EvaluationRun> EvaluationRuns => Set<EvaluationRun>();

    public DbSet<AuditLogEntry> AuditLogEntries => Set<AuditLogEntry>();

    public DbSet<OrchestrationRun> OrchestrationRuns => Set<OrchestrationRun>();

    public DbSet<OrchestrationRunAttempt> OrchestrationRunAttempts => Set<OrchestrationRunAttempt>();

    public DbSet<OrchestrationProgressEvent> OrchestrationProgressEvents => Set<OrchestrationProgressEvent>();

    public DbSet<OrchestrationRunStep> OrchestrationRunSteps => Set<OrchestrationRunStep>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Project>(entity =>
        {
            entity.ToTable("projects");

            entity.HasKey(x => x.Id);

            entity.Property(x => x.Name)
                .HasMaxLength(200)
                .IsRequired();

            entity.Property(x => x.Slug)
                .HasMaxLength(200)
                .IsRequired();

            entity.HasIndex(x => x.Slug)
                .IsUnique();

            entity.Property(x => x.Status)
                .HasConversion<string>()
                .HasMaxLength(50)
                .IsRequired();

            entity.Property(x => x.RootPath)
                .HasMaxLength(1000)
                .IsRequired();
        });

        modelBuilder.Entity<Requirement>(entity =>
        {
            entity.ToTable("requirements");

            entity.HasKey(x => x.Id);

            entity.Property(x => x.Title)
                .HasMaxLength(300)
                .IsRequired();

            entity.HasOne(x => x.Project)
                .WithMany()
                .HasForeignKey(x => x.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Feature>(entity =>
        {
            entity.ToTable("features");

            entity.HasKey(x => x.Id);

            entity.Property(x => x.Name)
                .HasMaxLength(300)
                .IsRequired();

            entity.HasOne(x => x.Project)
                .WithMany()
                .HasForeignKey(x => x.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(x => x.Requirement)
                .WithMany()
                .HasForeignKey(x => x.RequirementId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<WorkItem>(entity =>
        {
            entity.ToTable("work_items");

            entity.HasKey(x => x.Id);

            entity.Property(x => x.Title)
                .HasMaxLength(300)
                .IsRequired();

            entity.Property(x => x.Type)
                .HasConversion<string>()
                .HasMaxLength(50)
                .IsRequired();

            entity.Property(x => x.Status)
                .HasMaxLength(50)
                .IsRequired();

            entity.Property(x => x.Priority)
                .HasMaxLength(50)
                .IsRequired();

            entity.HasIndex(x => new
            {
                x.ProjectId,
                x.Status
            });

            entity.HasOne(x => x.Project)
                .WithMany()
                .HasForeignKey(x => x.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(x => x.Feature)
                .WithMany()
                .HasForeignKey(x => x.FeatureId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasOne(x => x.ParentWorkItem)
                .WithMany()
                .HasForeignKey(x => x.ParentWorkItemId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<WorkItemDependency>(entity =>
        {
            entity.ToTable("work_item_dependencies");

            entity.HasKey(x => new
            {
                x.WorkItemId,
                x.DependsOnWorkItemId
            });

            entity.HasOne(x => x.WorkItem)
                .WithMany(x => x.Dependencies)
                .HasForeignKey(x => x.WorkItemId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(x => x.DependsOnWorkItem)
                .WithMany()
                .HasForeignKey(x => x.DependsOnWorkItemId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AgentRun>(entity =>
        {
            entity.ToTable("agent_runs");

            entity.HasKey(x => x.Id);

            entity.HasOne(x => x.WorkItem)
                .WithMany(x => x.Runs)
                .HasForeignKey(x => x.WorkItemId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(x => new
            {
                x.WorkItemId,
                x.AttemptNumber
            });
        });

        modelBuilder.Entity<Artifact>(entity =>
        {
            entity.ToTable("artifacts");

            entity.HasKey(x => x.Id);

            entity.HasIndex(x => x.ProjectId);

            entity.HasIndex(x => x.WorkItemId);
        });

        modelBuilder.Entity<WorkflowTransition>(entity =>
        {
            entity.ToTable("workflow_transitions");

            entity.HasKey(x => x.Id);

            entity.Property(x => x.FromStatus)
                .HasConversion<string>()
                .HasMaxLength(50)
                .IsRequired();

            entity.Property(x => x.ToStatus)
                .HasConversion<string>()
                .HasMaxLength(50)
                .IsRequired();

            entity.HasOne(x => x.Project)
                .WithMany()
                .HasForeignKey(x => x.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(x => x.ProjectId);
        });

        modelBuilder.Entity<ValidationRun>(entity =>
        {
            entity.ToTable("validation_runs");

            entity.HasKey(x => x.Id);

            entity.Property(x => x.BuildOutput)
                .IsRequired();

            entity.HasOne(x => x.WorkItem)
                .WithMany(x => x.ValidationRuns)
                .HasForeignKey(x => x.WorkItemId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(x => x.WorkItemId);
        });

        modelBuilder.Entity<ReviewRun>(entity =>
        {
            entity.ToTable("review_runs");

            entity.HasKey(x => x.Id);

            entity.Property(x => x.FindingsJson)
                .IsRequired();

            entity.HasOne(x => x.WorkItem)
                .WithMany(x => x.ReviewRuns)
                .HasForeignKey(x => x.WorkItemId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(x => x.WorkItemId);
        });

        modelBuilder.Entity<ApprovalRequest>(entity =>
        {
            entity.ToTable("approval_requests");

            entity.HasKey(x => x.Id);

            entity.Property(x => x.Workflow)
                .HasConversion<string>()
                .HasMaxLength(50)
                .IsRequired();

            entity.Property(x => x.Decision)
                .HasConversion<string>()
                .HasMaxLength(50)
                .IsRequired();

            entity.HasOne(x => x.Project)
                .WithMany()
                .HasForeignKey(x => x.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(x => x.ProjectId);

            entity.HasIndex(x => x.Decision);
        });

        modelBuilder.Entity<Lesson>(entity =>
        {
            entity.ToTable("lessons");

            entity.HasKey(x => x.Id);

            entity.Property(x => x.Level)
                .HasConversion<string>()
                .HasMaxLength(50)
                .IsRequired();

            entity.HasOne(x => x.Project)
                .WithMany()
                .HasForeignKey(x => x.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(x => x.ProjectId);
        });

        modelBuilder.Entity<EvaluationRun>(entity =>
        {
            entity.ToTable("evaluation_runs");

            entity.HasKey(x => x.Id);

            entity.Property(x => x.HumanApproval)
                .HasConversion<string>()
                .HasMaxLength(50);

            entity.HasOne(x => x.WorkItem)
                .WithMany(x => x.EvaluationRuns)
                .HasForeignKey(x => x.WorkItemId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(x => x.WorkItemId);
        });

        modelBuilder.Entity<AuditLogEntry>(entity =>
        {
            entity.ToTable("audit_log_entries");

            entity.HasKey(x => x.Id);

            entity.Property(x => x.EventType)
                .HasConversion<string>()
                .HasMaxLength(50);

            entity.Property(x => x.Severity)
                .HasConversion<string>()
                .HasMaxLength(20);

            entity.Property(x => x.FailureCategory)
                .HasConversion<string>()
                .HasMaxLength(30);

            entity.Property(x => x.ActorType)
                .HasMaxLength(20)
                .HasDefaultValue("System");

            // No FK/navigation on purpose — an audit trail must survive
            // deletion of the Project/WorkItem it references.
            entity.HasIndex(x => x.ExecutionId);
            entity.HasIndex(x => x.ProjectId);
            entity.HasIndex(x => x.TimestampUtc);
            entity.HasIndex(x => x.CorrelationId);
            entity.HasIndex(x => x.RunId);
            entity.HasIndex(x => x.Severity);
        });

        modelBuilder.Entity<OrchestrationRun>(entity =>
        {
            entity.ToTable("orchestration_runs");

            entity.HasKey(x => x.Id);

            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(30);
            entity.Property(x => x.CurrentStage).HasConversion<string>().HasMaxLength(30);
            entity.Property(x => x.FailureCategory).HasConversion<string>().HasMaxLength(30);

            // No FK to WorkItem/Project — a run's own history should never
            // vanish because of an unrelated cascade delete elsewhere.
            entity.HasIndex(x => x.ProjectId);
            entity.HasIndex(x => x.WorkItemId);
            entity.HasIndex(x => x.Status);
        });

        modelBuilder.Entity<OrchestrationRunAttempt>(entity =>
        {
            entity.ToTable("orchestration_run_attempts");

            entity.HasKey(x => x.Id);

            entity.Property(x => x.FailureCategory).HasConversion<string>().HasMaxLength(30);

            entity.HasOne(x => x.OrchestrationRun)
                .WithMany(x => x.Attempts)
                .HasForeignKey(x => x.OrchestrationRunId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(x => x.OrchestrationRunId);
        });

        modelBuilder.Entity<OrchestrationProgressEvent>(entity =>
        {
            entity.ToTable("orchestration_progress_events");

            entity.HasKey(x => x.Id);

            entity.Property(x => x.Stage).HasConversion<string>().HasMaxLength(30);

            entity.HasOne(x => x.OrchestrationRun)
                .WithMany(x => x.ProgressEvents)
                .HasForeignKey(x => x.OrchestrationRunId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(x => x.OrchestrationRunId);
        });

        modelBuilder.Entity<OrchestrationRunStep>(entity =>
        {
            entity.ToTable("orchestration_run_steps");

            entity.HasKey(x => x.Id);

            entity.Property(x => x.Stage).HasConversion<string>().HasMaxLength(30);
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);

            entity.HasOne(x => x.OrchestrationRun)
                .WithMany(x => x.Steps)
                .HasForeignKey(x => x.OrchestrationRunId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(x => x.OrchestrationRunId);
            entity.HasIndex(x => x.Stage);
            entity.HasIndex(x => x.Status);
            entity.HasIndex(x => x.StartedAtUtc);
        });
    }
}
