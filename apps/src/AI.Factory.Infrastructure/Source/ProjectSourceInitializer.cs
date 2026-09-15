using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Orchestration;
using AI.Factory.Domain.Entities;
using AI.Factory.Infrastructure.Agents;
using Microsoft.Extensions.Logging;

namespace AI.Factory.Infrastructure.Source;

/// <summary>
/// Phase 18.16 — the one Factory-owned <see cref="IProjectSourceInitializer"/>
/// implementation. Lives in Infrastructure (not Application) because it
/// depends on <see cref="IProcessRunner"/>, which is itself an
/// Infrastructure-layer concern (mirrors DotNetBuildExecutor/DotNetTestExecutor,
/// which implement Application's IBuildExecutor/ITestExecutor the same way).
/// Resolves the workspace through the SAME IWorkspaceResolver Agent execution
/// and Verification use (no second workspace authority), never accepts a
/// destination path from the caller, and executes git only via
/// IProcessRunner's argument-list API — never a shell string.
/// </summary>
public sealed class ProjectSourceInitializer : IProjectSourceInitializer
{
    private readonly IWorkspaceResolver _workspaceResolver;
    private readonly IProcessRunner _processRunner;
    private readonly ExecutionLimitsOptions _executionLimits;
    private readonly ILogger<ProjectSourceInitializer> _logger;

    public ProjectSourceInitializer(
        IWorkspaceResolver workspaceResolver,
        IProcessRunner processRunner,
        ExecutionLimitsOptions executionLimits,
        ILogger<ProjectSourceInitializer> logger)
    {
        _workspaceResolver = workspaceResolver;
        _processRunner = processRunner;
        _executionLimits = executionLimits;
        _logger = logger;
    }

    public async Task<SourceInitializationResult> InitializeAsync(
        Project project, SourceSpecification source, CancellationToken cancellationToken = default)
    {
        // The workspace must already exist — ProjectWorkspaceProvisioner
        // ran during ProjectService.CreateAsync. Resolving it again here
        // (rather than trusting project.RootPath directly) keeps this
        // component from becoming a second, independently-deciding
        // workspace authority (spec §9/§25).
        var workspace = _workspaceResolver.Resolve(project);

        if (!workspace.IsValid)
        {
            _logger.LogError(
                "ProjectSourceInitializationWorkspaceInvalid ProjectId={ProjectId} Reason={Reason}",
                project.Id, workspace.FailureReason);
            return SourceInitializationResult.InfrastructureFailed(
                workspace.FailureReason ?? $"Project '{project.Id}' workspace could not be resolved.");
        }

        if (source.SourceType == ProjectSourceType.Empty)
        {
            // Explicitly Empty means "no source repository" — a valid,
            // deterministic decision. No fake files, no placeholder source,
            // no filesystem/process work at all.
            _logger.LogInformation("ProjectSourceInitializedEmpty ProjectId={ProjectId}", project.Id);
            return SourceInitializationResult.Succeeded();
        }

        return await InitializeGitRepositoryAsync(project, source, workspace.Path!, cancellationToken);
    }

    private async Task<SourceInitializationResult> InitializeGitRepositoryAsync(
        Project project, SourceSpecification source, string resolvedPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(source.Location))
        {
            return SourceInitializationResult.ValidationFailed("Git repository location is required.");
        }

        // Argument-injection hardening: even through IProcessRunner's
        // argument LIST (no shell interpreter is ever involved), a value
        // like "--upload-pack=/some/command" passed as a positional git
        // argument can still be parsed by GIT ITSELF as an option flag.
        // Reject outright rather than attempt to escape it.
        if (LooksLikeFlag(source.Location) || LooksLikeFlag(source.Branch))
        {
            return SourceInitializationResult.ValidationFailed(
                "Git repository location and branch must not start with '-'.");
        }

        // Existing-directory policy (spec §10): never clone into a non-empty
        // workspace, never auto-delete pre-existing files. The workspace was
        // just provisioned for a new Project, so a non-empty directory here
        // means either a repeated initialization attempt or pre-existing
        // user content — both a deterministic refusal, not an overwrite.
        if (Directory.EnumerateFileSystemEntries(resolvedPath).Any())
        {
            return SourceInitializationResult.Conflict(
                $"Project '{project.Id}' workspace '{resolvedPath}' is not empty; refusing to clone into an existing non-empty workspace.");
        }

        var arguments = new List<string> { "clone" };

        if (!string.IsNullOrWhiteSpace(source.Branch))
        {
            arguments.Add("--branch");
            arguments.Add(source.Branch);
            arguments.Add("--single-branch");
        }

        // "--" separates options from positional arguments — defense in
        // depth alongside the leading-dash check above, and clones directly
        // into the Factory-owned resolved workspace (never a scratch
        // directory + copy, never a caller-supplied destination).
        arguments.Add("--");
        arguments.Add(source.Location);
        arguments.Add(resolvedPath);

        _logger.LogInformation(
            "ProjectSourceGitCloneStarted ProjectId={ProjectId} Branch={Branch}",
            project.Id, source.Branch ?? "(default)");

        ProcessRunResult result;

        try
        {
            // Reuses BuildTimeoutMinutes rather than inventing a new
            // configuration knob (spec instruction) — a git clone is a
            // similarly-bounded, one-shot external process invocation.
            result = await _processRunner.RunAsync(
                new ProcessRunRequest("git", arguments, TimeSpan.FromMinutes(_executionLimits.BuildTimeoutMinutes)),
                cancellationToken);
        }
        catch (ProcessLaunchException ex)
        {
            _logger.LogError(ex, "ProjectSourceGitUnavailable ProjectId={ProjectId}", project.Id);
            return SourceInitializationResult.InfrastructureFailed($"git executable is unavailable: {ex.Message}");
        }

        if (result.TimedOut)
        {
            _logger.LogError("ProjectSourceGitCloneTimedOut ProjectId={ProjectId}", project.Id);
            await CleanupPartialCloneAsync(resolvedPath, project.Id);
            return SourceInitializationResult.InfrastructureFailed("git clone timed out.");
        }

        if (result.ExitCode != 0)
        {
            _logger.LogError(
                "ProjectSourceGitCloneFailed ProjectId={ProjectId} ExitCode={ExitCode}", project.Id, result.ExitCode);
            await CleanupPartialCloneAsync(resolvedPath, project.Id);
            return SourceInitializationResult.InfrastructureFailed(
                $"git clone failed (exit code {result.ExitCode}): {Truncate(result.StandardError)}");
        }

        _logger.LogInformation("ProjectSourceGitCloneSucceeded ProjectId={ProjectId}", project.Id);
        return SourceInitializationResult.Succeeded();
    }

    /// <summary>
    /// Spec §11 "partially initialized checkout" — a failed/timed-out git
    /// clone can leave a partial ".git" (or partial working-tree files)
    /// behind. Safe to remove HERE ONLY, because this method is only reached
    /// after this same call already confirmed the workspace was completely
    /// empty immediately before invoking git — so anything found now was
    /// created by THIS failed attempt, never pre-existing user content
    /// (spec §10/§11: never delete pre-existing files). Best-effort: a
    /// cleanup failure must never mask the original clone failure result.
    /// </summary>
    private async Task CleanupPartialCloneAsync(string resolvedPath, Guid projectId)
    {
        // A few short, bounded retries — immediately after the git process
        // exits, Windows can briefly hold a file handle open (AV scanning,
        // deferred handle release) causing a spurious UnauthorizedAccessException
        // on the very next delete attempt. Best-effort only: this never masks
        // the original clone failure result, and gives up quietly after 3
        // attempts rather than retrying forever.
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            if (TryCleanupOnce(resolvedPath, projectId) == 0)
            {
                return;
            }

            if (attempt < 3)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt));
            }
        }
    }

    /// <summary>Returns the number of entries that could not be removed this attempt.</summary>
    private int TryCleanupOnce(string resolvedPath, Guid projectId)
    {
        IReadOnlyList<string> entries;

        try
        {
            entries = Directory.EnumerateFileSystemEntries(resolvedPath).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "ProjectSourcePartialCloneCleanupFailed ProjectId={ProjectId}", projectId);
            return 1;
        }

        var remaining = 0;

        // Best-effort, per-entry: git's own failed-clone teardown can delete
        // its temporary lock/pack files concurrently with this cleanup pass
        // (a benign race, not a real error) — one vanished entry must never
        // abort cleanup of the rest, and cleanup failing at all must never
        // mask the original clone failure result.
        foreach (var entry in entries)
        {
            try
            {
                ClearReadOnlyRecursive(entry);

                if (Directory.Exists(entry))
                {
                    Directory.Delete(entry, recursive: true);
                }
                else if (File.Exists(entry))
                {
                    File.Delete(entry);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(
                    ex, "ProjectSourcePartialCloneCleanupFailed ProjectId={ProjectId} Entry={Entry}", projectId, entry);
                remaining++;
            }
        }

        return remaining;
    }

    /// <summary>Git marks its own object-store files read-only on Windows; deletion needs this attribute cleared first. Tolerates entries that vanish mid-walk (see CleanupPartialClone).</summary>
    private static void ClearReadOnlyRecursive(string path)
    {
        if (Directory.Exists(path))
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
                catch (IOException)
                {
                    // Vanished between enumeration and SetAttributes — fine,
                    // there is nothing left to clear the attribute on.
                }
            }
        }
        else if (File.Exists(path))
        {
            try
            {
                File.SetAttributes(path, FileAttributes.Normal);
            }
            catch (IOException)
            {
            }
        }
    }

    private static bool LooksLikeFlag(string? value) => !string.IsNullOrEmpty(value) && value.StartsWith('-');

    private static string Truncate(string value) => value.Length <= 2000 ? value : value[..2000] + " …(truncated)";
}
