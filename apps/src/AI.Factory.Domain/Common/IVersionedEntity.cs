namespace AI.Factory.Domain.Common;

/// <summary>
/// Marks an entity as participating in optimistic concurrency tracking via a Version token.
/// Provider-neutral (works seamlessly with SQLite, PostgreSQL, SQL Server, and in-memory test providers).
/// </summary>
public interface IVersionedEntity
{
    long Version { get; set; }
}
