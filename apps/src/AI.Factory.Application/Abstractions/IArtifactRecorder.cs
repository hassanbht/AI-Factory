namespace AI.Factory.Application.Abstractions;

public interface IArtifactRecorder
{
    /// <summary>
    /// Enumerates the files present in an agent's execution workspace and
    /// persists one Artifact row per file ("detect changed files").
    /// </summary>
    Task RecordWorkspaceArtifactsAsync(
        Guid projectId,
        Guid workItemId,
        string workspacePath,
        CancellationToken cancellationToken = default);
}
