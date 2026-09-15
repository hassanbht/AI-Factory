using AI.Factory.Domain.Entities;

namespace AI.Factory.Application.Abstractions;

/// <summary>
/// Phase 18.15 — creates a Project's Factory-owned workspace directory on
/// disk. Single responsibility: directory provisioning. Never touches Git,
/// never builds/tests/executes anything, never decides Project lifecycle.
/// Idempotent — provisioning an already-existing, already-valid workspace
/// succeeds with no side effect.
/// </summary>
public interface IProjectWorkspaceProvisioner
{
    ProvisioningResult Provision(Project project);
}

public sealed record ProvisioningResult(bool Success, string? Path, string? FailureReason)
{
    public static ProvisioningResult Succeeded(string path) => new(true, path, null);

    public static ProvisioningResult Failed(string reason) => new(false, null, reason);
}
