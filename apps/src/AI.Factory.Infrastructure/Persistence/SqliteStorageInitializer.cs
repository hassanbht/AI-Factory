using AI.Factory.Infrastructure.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace AI.Factory.Infrastructure.Persistence;

/// <summary>
/// Makes the configured SQLite location deterministic and usable before the
/// first query runs.
///
/// SQLite reports every location problem — missing directory, no write
/// permission, wrong drive — as the same opaque
/// "SQLite Error 14: 'unable to open database file'", raised wherever the first
/// query happens to be (startup recovery, a health check, an HTTP request). Two
/// deployment realities make that failure easy to hit outside Docker, where the
/// data directory is a mounted volume that always exists:
/// <list type="bullet">
///   <item>a relative <c>Data Source</c> resolves against the process current
///   directory, which under IIS is not the application directory;</item>
///   <item>an absolute container path (<c>/var/lib/ai-factory/data</c>) resolves
///   against the current drive on Windows, where nothing created it.</item>
/// </list>
/// Both are resolved here: the connection string is normalized at composition
/// time, and the directory is prepared (and probed for writability) at startup,
/// failing with a message that names the actual path and the setting to change.
/// </summary>
public static class SqliteStorageInitializer
{
    /// <summary>
    /// Returns <paramref name="connectionString"/> with its <c>Data Source</c>
    /// environment-variable expanded and fully qualified against
    /// <paramref name="basePath"/> (the content root). In-memory and URI data
    /// sources, and anything unparseable, are returned untouched so EF still
    /// reports its own error for them.
    /// </summary>
    public static string NormalizeConnectionString(string connectionString, string basePath)
    {
        if (string.IsNullOrWhiteSpace(connectionString) || string.IsNullOrWhiteSpace(basePath))
        {
            return connectionString;
        }

        if (!TryParseFileBacked(connectionString, out var builder))
        {
            return connectionString;
        }

        builder.DataSource = DeploymentPaths.ResolveAgainst(basePath, builder.DataSource);

        return builder.ConnectionString;
    }

    /// <summary>
    /// The absolute database file path a connection string points at, or null
    /// when it is not file-backed (in-memory, URI, unparseable).
    /// </summary>
    public static string? GetDatabaseFilePath(string connectionString)
    {
        if (!TryParseFileBacked(connectionString, out var builder))
        {
            return null;
        }

        return Path.GetFullPath(DeploymentPaths.Expand(builder.DataSource)!);
    }

    /// <summary>
    /// Creates the database directory when missing and verifies the hosting
    /// identity can write to it. Throws <see cref="InvalidOperationException"/>
    /// with an actionable message instead of letting the deployment fail later as
    /// SQLite error 14.
    /// </summary>
    public static void EnsureDataDirectoryExists(string connectionString, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        var databaseFilePath = GetDatabaseFilePath(connectionString);
        if (databaseFilePath is null)
        {
            return;
        }

        var directory = Path.GetDirectoryName(databaseFilePath);
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        if (!Directory.Exists(directory))
        {
            try
            {
                Directory.CreateDirectory(directory);
                logger.LogInformation(
                    "SqliteDataDirectoryCreated Directory={Directory} DatabaseFile={DatabaseFile}",
                    directory, Path.GetFileName(databaseFilePath));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                throw new InvalidOperationException(
                    BuildFailureMessage(
                        $"The SQLite data directory '{directory}' does not exist and could not be created",
                        connectionString),
                    ex);
            }
        }

        EnsureWritable(directory, connectionString);

        logger.LogInformation(
            "SqliteDataDirectoryReady Directory={Directory} DatabaseFile={DatabaseFile} Exists={Exists}",
            directory, Path.GetFileName(databaseFilePath), File.Exists(databaseFilePath));
    }

    /// <summary>
    /// A directory that exists but is not writable fails exactly like a missing
    /// one — same SQLite error 14, at the first query rather than at startup.
    /// Probing costs one file create/delete per process start.
    /// </summary>
    private static void EnsureWritable(string directory, string connectionString)
    {
        var probePath = Path.Combine(directory, $".ai-factory-write-probe-{Guid.NewGuid():N}");

        try
        {
            using (File.Create(probePath, 1, FileOptions.DeleteOnClose))
            {
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new InvalidOperationException(
                BuildFailureMessage(
                    $"The SQLite data directory '{directory}' exists but is not writable by the hosting identity " +
                    $"('{Environment.UserName}')",
                    connectionString),
                ex);
        }
    }

    private static string BuildFailureMessage(string problem, string connectionString)
    {
        var message =
            $"{problem}, so the Factory database cannot be opened. " +
            "Point Database:ConnectionString (or ConnectionStrings:FactoryDatabase) at a writable location " +
            "and grant the account the application runs as write access to it.";

        if (DeploymentPaths.IsContainerPathOnWindows(RawDataSource(connectionString)))
        {
            message +=
                " The configured Data Source is an absolute Linux/container path; on Windows it resolves against " +
                "the current drive root. A Windows deployment overrides it in appsettings.<Environment>.Windows.json " +
                "(see docs/deployment/windows-iis.md).";
        }

        return message;
    }

    private static string? RawDataSource(string connectionString) =>
        TryParseFileBacked(connectionString, out var builder) ? builder.DataSource : null;

    /// <summary>
    /// True only for a plain file-backed data source — the single case where a
    /// directory on disk has to exist.
    /// </summary>
    private static bool TryParseFileBacked(string connectionString, out SqliteConnectionStringBuilder builder)
    {
        builder = null!;

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return false;
        }

        try
        {
            builder = new SqliteConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException)
        {
            return false;
        }

        var dataSource = builder.DataSource;

        if (string.IsNullOrWhiteSpace(dataSource) ||
            builder.Mode is SqliteOpenMode.Memory ||
            dataSource.Equals(":memory:", StringComparison.OrdinalIgnoreCase) ||
            dataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }
}
