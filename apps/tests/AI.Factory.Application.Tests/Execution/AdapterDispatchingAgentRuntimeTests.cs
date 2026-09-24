using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Abstractions.Execution;
using AI.Factory.Application.Execution;
using AI.Factory.Domain.Entities;
using AI.Factory.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AI.Factory.Application.Tests.Execution;

public sealed class AdapterDispatchingAgentRuntimeTests
{
    private static ExecutionOptions DefaultOptions(Action<ExecutionOptions>? configure = null)
    {
        var options = new ExecutionOptions();
        configure?.Invoke(options);
        options.ApplyBuiltInDefault("Hermes");
        options.Validate();
        return options;
    }

    private static AdapterDispatchingAgentRuntime Dispatcher(ExecutionOptions options, params IExecutionAdapter[] adapters) =>
        new(new ExecutionProfileResolver(options, new ExecutionAdapterRegistry(adapters)),
            new FailureClassifier(),
            NullLogger<AdapterDispatchingAgentRuntime>.Instance);

    private static AgentRun MakeRun(
        string agentType = "Implementation", string? model = "m-1", string? provider = "gw-1", bool enabled = true)
    {
        var definition = new AgentDefinition
        {
            Name = "Coder",
            AgentType = agentType,
            SystemPrompt = "You are a coding agent.",
            Enabled = enabled
        };

        return new AgentRun
        {
            AgentDefinitionId = definition.Id,
            AgentDefinition = definition,
            ProjectId = Guid.NewGuid(),
            WorkItemId = Guid.NewGuid(),
            Attempt = 2,
            Iteration = 3,
            Provider = provider,
            Model = model,
            Input = "instruction text",
            WorkspacePath = "/ws/project"
        };
    }

    [Fact]
    public async Task DefaultProfile_DispatchesToTheHermesAdapter_WithAFaithfulSpec()
    {
        var hermes = new FakeExecutionAdapter("Hermes");
        var run = MakeRun();

        await Dispatcher(DefaultOptions(), hermes).ExecuteAsync(run);

        Assert.Equal(1, hermes.CallCount);
        var spec = hermes.LastSpec!;
        Assert.Equal(run.Id, spec.AgentRunId);
        Assert.Equal(run.CorrelationId, spec.CorrelationId);
        Assert.Equal(run.ProjectId, spec.ProjectId);
        Assert.Equal(run.WorkItemId, spec.WorkItemId);
        Assert.Equal(2, spec.Attempt);
        Assert.Equal(3, spec.Iteration);
        Assert.Equal(run.AgentDefinition!.Id, spec.Agent.DefinitionId);
        Assert.Equal("Coder", spec.Agent.Name);
        Assert.Equal("Implementation", spec.Agent.AgentType);
        Assert.Equal("You are a coding agent.", spec.Agent.SystemPrompt);
        Assert.Equal("instruction text", spec.Instructions);
        Assert.Equal("/ws/project", spec.Workspace.Path);
        Assert.Equal(new ModelRoute("m-1", "gw-1"), spec.Model);
    }

    [Fact]
    public async Task Succeeded_MapsToSuccess_WithOutputUsageArtifactsToolsAndModel()
    {
        var adapter = new FakeExecutionAdapter("Hermes", execute: (spec, _) => Task.FromResult(new ExecutionOutcome(
            ExecutionOutcomeStatus.Succeeded, "done", null, null, 42,
            new ExecutionUsage(10, 20, 30),
            new[] { "artifact-1" }, new[] { "ReadArtifact" },
            new ModelRoute("reported-model", "reported-gateway"))));

        var result = await Dispatcher(DefaultOptions(), adapter).ExecuteAsync(MakeRun());

        Assert.True(result.Success);
        Assert.Equal("done", result.Output);
        Assert.Null(result.ErrorMessage);
        Assert.Null(result.FailureCategory);
        Assert.Equal(42, result.DurationMs);
        Assert.Equal(10, result.PromptTokens);
        Assert.Equal(20, result.CompletionTokens);
        Assert.Equal(30, result.TotalTokens);
        Assert.Equal(new[] { "artifact-1" }, result.ArtifactReferences);
        Assert.Equal(new[] { "ReadArtifact" }, result.ToolCallNames);
        Assert.Equal("reported-model", result.Model);
        Assert.Equal("reported-gateway", result.Provider);
        Assert.Equal("Hermes", result.ExecutionAdapter);
        Assert.Null(result.ExitReason);
    }

    [Theory]
    [InlineData(ExecutionOutcomeStatus.Succeeded, "exit:0")]
    [InlineData(ExecutionOutcomeStatus.Failed, "exit:1")]
    [InlineData(ExecutionOutcomeStatus.TimedOut, "timeout")]
    [InlineData(ExecutionOutcomeStatus.Cancelled, "cancelled")]
    public async Task EveryExecutedOutcome_CarriesTheAdapterKeyAndItsExitReason(ExecutionOutcomeStatus status, string exitReason)
    {
        var adapter = new FakeExecutionAdapter("Hermes", execute: (spec, _) =>
            Task.FromResult(FakeExecutionAdapter.Outcome(status, spec) with { ExitReason = exitReason }));

        var result = await Dispatcher(DefaultOptions(), adapter).ExecuteAsync(MakeRun());

        Assert.Equal("Hermes", result.ExecutionAdapter);
        Assert.Equal(exitReason, result.ExitReason);
    }

    // ---- Phase C: AgentDefinition policy is enforced here, before any adapter ----

    [Fact]
    public async Task DisabledDefinition_IsRejectedBeforeAnyAdapterRuns_WithTheUnchangedValidationResult()
    {
        var hermes = new FakeExecutionAdapter("Hermes");
        var run = MakeRun(enabled: false);

        var result = await Dispatcher(DefaultOptions(), hermes).ExecuteAsync(run);

        Assert.Equal(0, hermes.CallCount);
        Assert.False(result.Success);
        Assert.Equal(FailureCategory.Validation, result.FailureCategory);
        Assert.Equal($"AgentDefinition 'Coder' ({run.AgentDefinition!.Id}) is disabled.", result.ErrorMessage);
        Assert.Null(result.Output);
        Assert.Equal(0, result.DurationMs);
        Assert.Empty(result.ArtifactReferences);
        Assert.Empty(result.ToolCallNames);
        Assert.Null(result.PromptTokens);
        Assert.Equal(run.Model, result.Model);
        Assert.Equal(run.Provider, result.Provider);
        Assert.Null(result.ExecutionAdapter);
        Assert.Null(result.ExitReason);
    }

    [Fact]
    public async Task DisabledDefinition_IsRejectedEvenWhenItsProfileCouldNotBeResolved()
    {
        var run = MakeRun(enabled: false);

        var result = await Dispatcher(DefaultOptions()).ExecuteAsync(run);

        Assert.Equal(FailureCategory.Validation, result.FailureCategory);
        Assert.Contains("is disabled", result.ErrorMessage);
    }

    [Fact]
    public async Task EnabledDefinition_ReachesTheAdapter()
    {
        var hermes = new FakeExecutionAdapter("Hermes");

        var result = await Dispatcher(DefaultOptions(), hermes).ExecuteAsync(MakeRun(enabled: true));

        Assert.Equal(1, hermes.CallCount);
        Assert.True(result.Success);
    }

    [Fact]
    public async Task DisabledDefinition_UsesTheFactoryFailureClassifier_ValidationHint()
    {
        var classifier = new RecordingClassifier();
        var dispatcher = new AdapterDispatchingAgentRuntime(
            new ExecutionProfileResolver(DefaultOptions(), new ExecutionAdapterRegistry(new[] { new FakeExecutionAdapter("Hermes") })),
            classifier,
            NullLogger<AdapterDispatchingAgentRuntime>.Instance);

        var result = await dispatcher.ExecuteAsync(MakeRun(enabled: false));

        Assert.Equal("validation", classifier.LastHint);
        Assert.Equal(FailureCategory.Validation, result.FailureCategory);
    }

    private sealed class RecordingClassifier : IFailureClassifier
    {
        private readonly FailureClassifier _inner = new();

        public string? LastHint { get; private set; }

        public FailureClassificationResult Classify(Exception exception) => _inner.Classify(exception);

        public FailureClassificationResult Classify(string reason, string? hintCategory = null)
        {
            LastHint = hintCategory;
            return _inner.Classify(reason, hintCategory);
        }
    }

    [Theory]
    [InlineData(ExecutionOutcomeStatus.Failed, FailureCategory.Agent, FailureCategory.Agent)]
    [InlineData(ExecutionOutcomeStatus.Failed, FailureCategory.Provider, FailureCategory.Provider)]
    [InlineData(ExecutionOutcomeStatus.Failed, null, null)]
    [InlineData(ExecutionOutcomeStatus.TimedOut, FailureCategory.Timeout, FailureCategory.Timeout)]
    [InlineData(ExecutionOutcomeStatus.TimedOut, null, FailureCategory.Timeout)]
    [InlineData(ExecutionOutcomeStatus.Cancelled, FailureCategory.Cancelled, FailureCategory.Cancelled)]
    [InlineData(ExecutionOutcomeStatus.Cancelled, null, FailureCategory.Cancelled)]
    public async Task NonSuccessOutcomes_MapToFailure_PreservingOrDerivingTheCategory(
        ExecutionOutcomeStatus status, FailureCategory? reported, FailureCategory? expected)
    {
        var adapter = new FakeExecutionAdapter("Hermes", execute: (spec, _) =>
            Task.FromResult(FakeExecutionAdapter.Outcome(status, spec, output: "partial", error: "went wrong", category: reported)));

        var result = await Dispatcher(DefaultOptions(), adapter).ExecuteAsync(MakeRun());

        Assert.False(result.Success);
        Assert.Equal(expected, result.FailureCategory);
        Assert.Equal("went wrong", result.ErrorMessage);
        Assert.Equal("partial", result.Output);
        Assert.Null(result.PromptTokens);
        Assert.Null(result.TotalTokens);
    }

    [Fact]
    public async Task CancellationToken_IsPropagatedToTheAdapter()
    {
        using var cts = new CancellationTokenSource();
        var adapter = new FakeExecutionAdapter("Hermes");

        await Dispatcher(DefaultOptions(), adapter).ExecuteAsync(MakeRun(), cts.Token);

        Assert.Equal(cts.Token, adapter.LastToken);
    }

    [Fact]
    public async Task AdapterThrowingCancellation_PropagatesUnchanged()
    {
        var adapter = new FakeExecutionAdapter("Hermes", execute: (_, ct) => throw new OperationCanceledException(ct));

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => Dispatcher(DefaultOptions(), adapter).ExecuteAsync(MakeRun()));
    }

    [Fact]
    public async Task AdapterThrowingAnyException_PropagatesUnchanged()
    {
        var adapter = new FakeExecutionAdapter("Hermes", execute: (_, _) => throw new InvalidOperationException("boom"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Dispatcher(DefaultOptions(), adapter).ExecuteAsync(MakeRun()));
        Assert.Equal("boom", ex.Message);
    }

    [Fact]
    public async Task MissingAgentDefinition_ThrowsTheSameArgumentExceptionAsBefore()
    {
        var run = MakeRun();
        run.AgentDefinition = null;

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => Dispatcher(DefaultOptions(), new FakeExecutionAdapter("Hermes")).ExecuteAsync(run));
        Assert.Equal("agentRun", ex.ParamName);
    }

    [Fact]
    public async Task UnknownConfiguredAdapter_FailsClosed_AsValidation_WithoutInvokingAnyAdapter()
    {
        var hermes = new FakeExecutionAdapter("Hermes");
        var options = DefaultOptions(o =>
        {
            o.DefaultProfile = "Hermes";
            o.Profiles["Hermes"] = new ExecutionProfileOptions { Adapter = "Hermes" };
            o.Profiles["Next"] = new ExecutionProfileOptions { Adapter = "OpenHands" };
            o.AgentTypeProfiles["Implementation"] = "Next";
        });
        var run = MakeRun();

        var result = await Dispatcher(options, hermes).ExecuteAsync(run);

        Assert.Equal(0, hermes.CallCount);
        Assert.False(result.Success);
        Assert.Equal(FailureCategory.Validation, result.FailureCategory);
        Assert.Contains("'OpenHands'", result.ErrorMessage);
        Assert.Equal(0, result.DurationMs);
        Assert.Equal(run.Model, result.Model);
        Assert.Equal(run.Provider, result.Provider);
        Assert.Null(result.ExecutionAdapter);
    }

    [Fact]
    public async Task DisabledAdapter_FailsClosed_AsValidation()
    {
        var hermes = new FakeExecutionAdapter("Hermes");
        var options = DefaultOptions(o => o.Adapters["Hermes"] = new ExecutionAdapterOptions { Enabled = false });

        var result = await Dispatcher(options, hermes).ExecuteAsync(MakeRun());

        Assert.Equal(0, hermes.CallCount);
        Assert.Equal(FailureCategory.Validation, result.FailureCategory);
        Assert.Contains("disabled", result.ErrorMessage);
    }

    [Fact]
    public async Task NoRegisteredAdapter_FailsClosed_AsValidation()
    {
        var result = await Dispatcher(DefaultOptions()).ExecuteAsync(MakeRun());

        Assert.False(result.Success);
        Assert.Equal(FailureCategory.Validation, result.FailureCategory);
        Assert.Contains("not registered", result.ErrorMessage);
    }

    [Fact]
    public async Task RoutedModel_OnAdapterWithoutFactoryRoutedModel_FailsClosed()
    {
        var adapter = new FakeExecutionAdapter("Hermes", ExecutionCapabilities.None);

        var result = await Dispatcher(DefaultOptions(), adapter).ExecuteAsync(MakeRun(model: "m-1"));

        Assert.Equal(0, adapter.CallCount);
        Assert.Equal(FailureCategory.Validation, result.FailureCategory);
        Assert.Contains("'m-1'", result.ErrorMessage);
    }

    [Fact]
    public async Task NoRoutedModel_OnAdapterWithoutFactoryRoutedModel_IsDispatched()
    {
        var adapter = new FakeExecutionAdapter("Hermes", ExecutionCapabilities.None);

        var result = await Dispatcher(DefaultOptions(), adapter).ExecuteAsync(MakeRun(model: null));

        Assert.Equal(1, adapter.CallCount);
        Assert.True(result.Success);
    }
}
