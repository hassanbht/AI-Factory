namespace AI.Factory.Application.Orchestration;

/// <summary>Thrown when an agent-execution security boundary is violated (workspace escape, disallowed command). Never a silent failure.</summary>
public sealed class SecurityViolationException : Exception
{
    public SecurityViolationException(string message)
        : base(message)
    {
    }
}
