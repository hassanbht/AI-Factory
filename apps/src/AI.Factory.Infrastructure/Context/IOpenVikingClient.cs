namespace AI.Factory.Infrastructure.Context;

/// <summary>
/// Thin wire-transport seam so OpenVikingContextProvider's mapping/fallback
/// logic can be unit-tested with a fake, separately from the real HTTP
/// transport (HttpOpenVikingClient), which gets its own integration test.
/// Internal to Infrastructure — the Application layer never sees this.
/// </summary>
public interface IOpenVikingClient
{
    Task<OpenVikingQueryResponseDto> QueryAsync(
        OpenVikingQueryRequestDto request,
        CancellationToken cancellationToken = default);
}

public sealed class OpenVikingQueryRequestDto
{
    public string Category { get; set; } = string.Empty;

    public Guid ProjectId { get; set; }

    public int MaxResults { get; set; }
}

public sealed class OpenVikingQueryResponseDto
{
    public List<OpenVikingDocumentDto> Documents { get; set; } = new();
}

public sealed class OpenVikingDocumentDto
{
    public string Source { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;

    public double Relevance { get; set; }

    public Dictionary<string, string>? Metadata { get; set; }
}
