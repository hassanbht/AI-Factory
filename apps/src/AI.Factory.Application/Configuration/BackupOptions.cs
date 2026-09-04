namespace AI.Factory.Application.Configuration;

/// <summary>
/// Strongly typed configuration for the SQLite backup/restore capability. Disabled by default —
/// an operator must opt in with a real <see cref="BackupDirectory"/> before backups can run.
/// Compression is intentionally not offered here: SQLite database files already compress poorly
/// (mixed binary/text pages) and adding it would trade a meaningful restore-safety surface for a
/// marginal disk saving — out of scope until an operator actually needs it.
/// </summary>
public sealed class BackupOptions
{
    public const string SectionName = "Backup";

    public bool Enabled { get; set; } = false;

    public string BackupDirectory { get; set; } = string.Empty;

    public int RetentionCount { get; set; } = 7;

    public bool ValidateAfterBackup { get; set; } = true;

    public TimeSpan BackupTimeout { get; set; } = TimeSpan.FromSeconds(60);

    public void Validate()
    {
        if (!Enabled)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(BackupDirectory))
        {
            throw new InvalidOperationException("Backup:BackupDirectory is required when Backup:Enabled is true.");
        }

        if (RetentionCount < 1)
        {
            throw new InvalidOperationException(
                $"Backup:RetentionCount must be at least 1. Value was '{RetentionCount}'.");
        }

        if (BackupTimeout <= TimeSpan.Zero || BackupTimeout > TimeSpan.FromMinutes(30))
        {
            throw new InvalidOperationException(
                $"Backup:BackupTimeout must be between 1 second and 30 minutes. Value was '{BackupTimeout.TotalSeconds}' seconds.");
        }
    }
}
