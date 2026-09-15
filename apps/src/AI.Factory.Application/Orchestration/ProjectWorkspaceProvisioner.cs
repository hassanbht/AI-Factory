using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Configuration;
using AI.Factory.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace AI.Factory.Application.Orchestration;

/// <summary>
/// Phase 18.15 — the one Factory-owned <see cref="IProjectWorkspaceProvisioner"/>
/// implementation. Uses the same <see cref="WorkspacePathResolution"/> helper as
/// <see cref="ProjectWorkspaceResolver"/> to decide WHERE to create the
/// directory, then defers to <see cref="IWorkspaceResolver"/> itself for the
/// final validity determination — "Provisioner creates, Resolver validates"
/// (spec §10), so this component never independently decides whether a
/// workspace is valid beyond "did CreateDirectory succeed".
/// </summary>
public sealed class ProjectWorkspaceProvisioner : IProjectWorkspaceProvisioner
{
    private readonly IWorkspaceResolver _workspaceResolver;
    private readonly AgentWorkspaceOptions _options;
    private readonly ILogger<ProjectWorkspaceProvisioner> _logger;

    public ProjectWorkspaceProvisioner(
        IWorkspaceResolver workspaceResolver, AgentWorkspaceOptions options, ILogger<ProjectWorkspaceProvisioner> logger)
    {
        _workspaceResolver = workspaceResolver;
        _options = options;
        _logger = logger;
    }

    public ProvisioningResult Provision(Project project)
    {
        var (isValid, path, reason) = WorkspacePathResolution.ResolveBoundedPath(project, _options);

        if (!isValid)
        {
            _logger.LogWarning(
                "ProjectWorkspaceProvisioningRejected ProjectId={ProjectId} Reason={Reason}", project.Id, reason);
            return ProvisioningResult.Failed(reason!);
        }

        if (File.Exists(path))
        {
            var message = $"Project '{project.Id}' workspace path '{path}' already exists as a file, not a directory.";
            _logger.LogWarning("ProjectWorkspaceProvisioningRejected ProjectId={ProjectId} Reason={Reason}", project.Id, message);
            return ProvisioningResult.Failed(message);
        }

        try
        {
            Directory.CreateDirectory(path!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            var message = $"Failed to create workspace directory '{path}' for Project '{project.Id}': {ex.Message}";
            _logger.LogError(ex, "ProjectWorkspaceProvisioningFailed ProjectId={ProjectId}", project.Id);
            return ProvisioningResult.Failed(message);
        }

        // Provisioner creates, Resolver validates (spec §10) — never
        // independently decide validity here beyond "did CreateDirectory
        // succeed"; the authoritative answer always comes from the same
        // resolver Agent execution will use.
        var resolution = _workspaceResolver.Resolve(project);

        if (!resolution.IsValid)
        {
            _logger.LogError(
                "ProjectWorkspaceProvisioningInconsistent ProjectId={ProjectId} Reason={Reason}",
                project.Id, resolution.FailureReason);
        }

        return resolution.IsValid
            ? ProvisioningResult.Succeeded(resolution.Path!)
            : ProvisioningResult.Failed(resolution.FailureReason ?? "Workspace could not be validated after provisioning.");
    }
}
