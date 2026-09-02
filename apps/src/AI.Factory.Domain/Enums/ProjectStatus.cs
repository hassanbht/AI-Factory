namespace AI.Factory.Domain.Enums;

public enum ProjectStatus
{
    Created = 0,
    Analyzing = 10,
    Specification = 20,
    Architecture = 30,
    Planning = 40,
    Implementation = 50,
    Testing = 60,
    Repairing = 70,
    Reviewing = 80,
    HumanReview = 90,
    Completed = 100,
    Failed = 900,
    Cancelled = 910
}
