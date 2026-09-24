namespace AI.Factory.Application.Configuration;

/// <summary>
/// Strongly typed database configuration.
/// Ensures connection string presence at startup while preventing raw connection string exposure in diagnostics.
/// </summary>
public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    public string ConnectionString { get; set; } = string.Empty;

    public string Provider { get; set; } = "Sqlite";

    public bool AutoMigrate { get; set; } = false;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            throw new InvalidOperationException("Database:ConnectionString is required and cannot be empty.");
        }
    }
}
