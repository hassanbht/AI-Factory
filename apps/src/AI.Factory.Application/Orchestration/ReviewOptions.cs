namespace AI.Factory.Application.Orchestration;

public sealed class ReviewOptions
{
    /// <summary>Whether Medium-severity findings also block completion (routed through repair), or only inform.</summary>
    public bool BlockOnMediumFindings { get; set; }
}
