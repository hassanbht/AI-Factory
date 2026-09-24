using AI.Factory.Application.Abstractions.Execution;

namespace AI.Factory.Application.Tests.Execution;

/// <summary>Deterministic IExecutionAdapter test double — records every spec/token it receives.</summary>
public sealed class FakeExecutionAdapter : IExecutionAdapter
{
    private readonly Func<AgentExecutionSpec, CancellationToken, Task<ExecutionOutcome>> _execute;

    public FakeExecutionAdapter(
        string key,
        ExecutionCapabilities capabilities = ExecutionCapabilities.FactoryRoutedModel,
        Func<AgentExecutionSpec, CancellationToken, Task<ExecutionOutcome>>? execute = null)
    {
        Key = key;
        Capabilities = capabilities;
        _execute = execute ?? ((spec, _) => Task.FromResult(Outcome(ExecutionOutcomeStatus.Succeeded, spec)));
    }

    public string Key { get; }

    public ExecutionCapabilities Capabilities { get; }

    public int CallCount { get; private set; }

    public AgentExecutionSpec? LastSpec { get; private set; }

    public CancellationToken LastToken { get; private set; }

    public Task<ExecutionOutcome> ExecuteAsync(AgentExecutionSpec spec, CancellationToken cancellationToken = default)
    {
        CallCount++;
        LastSpec = spec;
        LastToken = cancellationToken;
        return _execute(spec, cancellationToken);
    }

    public static ExecutionOutcome Outcome(
        ExecutionOutcomeStatus status,
        AgentExecutionSpec spec,
        string? output = "fake output",
        string? error = null,
        AI.Factory.Domain.Enums.FailureCategory? category = null,
        ExecutionUsage? usage = null) => new(
            status, output, error, category, 17, usage,
            Array.Empty<string>(), Array.Empty<string>(), spec.Model);
}
