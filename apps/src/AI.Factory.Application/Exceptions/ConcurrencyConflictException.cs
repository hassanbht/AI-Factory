namespace AI.Factory.Application.Exceptions;

/// <summary>
/// Thrown when an optimistic concurrency conflict occurs (e.g., stale Version token or concurrent modification).
/// Represents the domain/application concept that the resource changed after the caller loaded its state.
/// Contains no SQL queries, table names, or provider metadata to avoid sensitive data leakage.
/// </summary>
public sealed class ConcurrencyConflictException : Exception
{
    public string? ResourceType { get; }
    public string? ResourceId { get; }

    public ConcurrencyConflictException(
        string message = "The resource was changed by another operation. Please reload and retry.",
        string? resourceType = null,
        string? resourceId = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        ResourceType = resourceType;
        ResourceId = resourceId;
    }
}
