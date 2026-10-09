using AI.Factory.Application.Exceptions;
using AI.Factory.Domain.Common;
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

    public DbSet<HermesExecution> HermesExecutions => Set<HermesExecution>();

    public DbSet<AgentDefinition> AgentDefinitions => Set<AgentDefinition>();

    public DbSet<AgentRun> AgentRuns => Set<AgentRun>();

    public DbSet<Artifact> Artifacts => Set<Artifact>();

    public DbSet<AgentMemoryRecord> AgentMemories => Set<AgentMemoryRecord>();

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

    /// <summary>Phase Q.3 — durable authoritative provider-generation ledger.</summary>
    public DbSet<ProviderGeneration> ProviderGenerations => Set<ProviderGeneration>();

    /// <summary>Phase Q.3 — Factory-owned capture sessions on the real execution route.</summary>
    public DbSet<ProviderCaptureSession> ProviderCaptureSessions => Set<ProviderCaptureSession>();

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

            entity.Property(x => x.Version)
                .IsConcurrencyToken();
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

            entity.Property(x => x.PlanKey)
                .HasMaxLength(200);

            entity.HasIndex(x => new
            {
                x.ProjectId,
                x.Status
            });

            entity.HasIndex(x => new
            {
                x.ProjectId,
                x.PlanKey
            }).IsUnique();

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

            entity.Property(x => x.Version)
                .IsConcurrencyToken();
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

        modelBuilder.Entity<HermesExecution>(entity =>
        {
            entity.ToTable("hermes_executions");

            entity.HasKey(x => x.Id);

            entity.HasOne(x => x.WorkItem)
                .WithMany(x => x.HermesExecutions)
                .HasForeignKey(x => x.WorkItemId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(x => new
            {
                x.WorkItemId,
                x.AttemptNumber
            });
        });

        modelBuilder.Entity<AgentDefinition>(entity =>
        {
            entity.ToTable("agent_definitions");

            entity.HasKey(x => x.Id);

            entity.Property(x => x.Name)
                .HasMaxLength(200)
                .IsRequired();

            entity.Property(x => x.AgentType)
                .HasMaxLength(100)
                .IsRequired();

            entity.HasIndex(x => x.Enabled);

            entity.Property(x => x.Version)
                .IsConcurrencyToken();
        });

        modelBuilder.Entity<AgentRun>(entity =>
        {
            entity.ToTable("agent_runs");

            entity.HasKey(x => x.Id);

            entity.Property(x => x.Status)
                .HasConversion<string>()
                .HasMaxLength(30)
                .IsRequired();

            entity.Property(x => x.FailureCategory)
                .HasConversion<string>()
                .HasMaxLength(30);

            entity.HasOne(x => x.AgentDefinition)
                .WithMany(x => x.Runs)
                .HasForeignKey(x => x.AgentDefinitionId)
                .OnDelete(DeleteBehavior.Restrict);

            // No FK to WorkItem/Project — an AgentRun's own history should
            // never vanish because of an unrelated cascade delete elsewhere,
            // matching the OrchestrationRun precedent.
            entity.HasIndex(x => x.AgentDefinitionId);
            entity.HasIndex(x => x.ProjectId);
            entity.HasIndex(x => x.WorkItemId);
            entity.HasIndex(x => x.Status);
            entity.HasIndex(x => x.CreatedAtUtc);

            entity.HasIndex(x => x.CorrelationId)
                .IsUnique();

            entity.Property(x => x.Version)
                .IsConcurrencyToken();
        });

        modelBuilder.Entity<Artifact>(entity =>
        {
            entity.ToTable("artifacts");

            entity.HasKey(x => x.Id);

            entity.HasIndex(x => x.ProjectId);

            entity.HasIndex(x => x.WorkItemId);
        });

        modelBuilder.Entity<AgentMemoryRecord>(entity =>
        {
            entity.ToTable("agent_memory");

            entity.HasKey(x => x.Id);

            entity.Property(x => x.Type)
                .HasMaxLength(100)
                .IsRequired();

            entity.Property(x => x.Content)
                .IsRequired();

            entity.HasIndex(x => x.ProjectId);
            entity.HasIndex(x => x.AgentDefinitionId);
            entity.HasIndex(x => x.AgentRunId);
            entity.HasIndex(x => x.Type);
            entity.HasIndex(x => x.CreatedAtUtc);
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

            entity.Property(x => x.Version)
                .IsConcurrencyToken();
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

            entity.Property(x => x.Version)
                .IsConcurrencyToken();
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

        modelBuilder.Entity<ProviderGeneration>(entity =>
        {
            entity.ToTable("provider_generations");

            entity.HasKey(x => x.Id);

            entity.Property(x => x.GenerationId).HasMaxLength(128).IsRequired();
            entity.Property(x => x.CertificationEpoch).HasMaxLength(100);
            entity.Property(x => x.BenchmarkIdentity).HasMaxLength(50);
            entity.Property(x => x.ModelId).HasMaxLength(300);
            entity.Property(x => x.Provider).HasMaxLength(100);
            entity.Property(x => x.AccountingStatus).HasConversion<string>().HasMaxLength(30);
            entity.Property(x => x.AccountingNote).HasMaxLength(500);

            entity.HasIndex(x => x.GenerationId).IsUnique();
            entity.HasIndex(x => x.AgentRunId);
            entity.HasIndex(x => x.CertificationEpoch);
            entity.HasIndex(x => new { x.CertificationEpoch, x.BenchmarkIdentity });
            entity.HasIndex(x => x.AccountingStatus);
        });

        modelBuilder.Entity<ProviderCaptureSession>(entity =>
        {
            entity.ToTable("provider_capture_sessions");

            entity.HasKey(x => x.Id);

            entity.Property(x => x.CertificationEpoch).HasMaxLength(100);
            entity.Property(x => x.BenchmarkIdentity).HasMaxLength(50);
            entity.Property(x => x.GatewayKey).HasMaxLength(100);
            entity.Property(x => x.ModelId).HasMaxLength(300);
            entity.Property(x => x.ListenPrefix).HasMaxLength(300);
            entity.Property(x => x.OverrideBaseUrl).HasMaxLength(500);
            entity.Property(x => x.Status).HasMaxLength(30);
            entity.Property(x => x.Note).HasMaxLength(500);

            entity.HasIndex(x => x.AgentRunId).IsUnique();
        });
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        return SaveChangesAsync(acceptAllChangesOnSuccess: true, cancellationToken);
    }

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        IncrementVersions();

        try
        {
            return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            var entityName = ex.Entries.FirstOrDefault()?.Entity.GetType().Name;
            throw new ConcurrencyConflictException(
                "The resource was changed by another operation. Please reload and retry.",
                resourceType: entityName,
                innerException: ex);
        }
    }

    public override int SaveChanges()
    {
        return SaveChanges(acceptAllChangesOnSuccess: true);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        IncrementVersions();

        try
        {
            return base.SaveChanges(acceptAllChangesOnSuccess);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            var entityName = ex.Entries.FirstOrDefault()?.Entity.GetType().Name;
            throw new ConcurrencyConflictException(
                "The resource was changed by another operation. Please reload and retry.",
                resourceType: entityName,
                innerException: ex);
        }
    }

    private void IncrementVersions()
    {
        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.Entity is IVersionedEntity versioned && entry.State == EntityState.Modified)
            {
                versioned.Version++;
            }
        }
    }
}
