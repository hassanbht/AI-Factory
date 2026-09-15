using AI.Factory.Domain.Entities;

namespace AI.Factory.Application.Abstractions;

/// <summary>
/// Phase 18.16 — obtains a Project's source and places it into the
/// already-provisioned <see cref="Project.RootPath"/>. Single
/// responsibility: source acquisition and placement into the Factory-owned
/// workspace. Never touches Agent execution, WorkItem creation, build/test,
/// verification, or Project lifecycle transitions — those remain the
/// caller's (<c>ProjectService</c>) responsibility.
/// </summary>
public interface IProjectSourceInitializer
{
    Task<SourceInitializationResult> InitializeAsync(
        Project project, SourceSpecification source, CancellationToken cancellationToken = default);
}

/// <summary>
/// Minimal source model: exactly two supported source types (spec §4/§22 —
/// no GitHub/GitLab/ADO-specific fields, no template source). <c>Location</c>
/// and <c>Branch</c> apply only to <see cref="ProjectSourceType.GitRepository"/>.
/// </summary>
public enum ProjectSourceType
{
    Empty,
    GitRepository
}

public sealed record SourceSpecification(ProjectSourceType SourceType, string? Location = null, string? Branch = null);

/// <summary>
/// <see cref="FailureKind"/> lets the caller map a failure to the correct
/// HTTP status without parsing <see cref="FailureReason"/> text: Validation
/// -> 400, Conflict -> 409, Infrastructure -> 503 (via the existing
/// IOException/FailureClassifier convention <c>ProjectService.CreateAsync</c>
/// already uses for workspace-provisioning failures).
/// </summary>
public enum SourceInitializationFailureKind
{
    None,
    Validation,
    Conflict,
    Infrastructure
}

public sealed record SourceInitializationResult(
    bool Success, string? FailureReason, SourceInitializationFailureKind FailureKind = SourceInitializationFailureKind.None)
{
    public static SourceInitializationResult Succeeded() => new(true, null);

    public static SourceInitializationResult ValidationFailed(string reason) =>
        new(false, reason, SourceInitializationFailureKind.Validation);

    public static SourceInitializationResult Conflict(string reason) =>
        new(false, reason, SourceInitializationFailureKind.Conflict);

    public static SourceInitializationResult InfrastructureFailed(string reason) =>
        new(false, reason, SourceInitializationFailureKind.Infrastructure);
}
