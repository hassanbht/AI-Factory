using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Orchestration;
using AI.Factory.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AI.Factory.Application.Tests;

public sealed class ModelTaskPlannerTests
{
    private sealed class FakeContextProvider : IContextProvider
    {
        private readonly string _content;

        public FakeContextProvider(string content = "")
        {
            _content = content;
        }

        public int CallCount { get; private set; }

        public Task<ContextResult> QueryAsync(
            ContextQuery query, CancellationToken cancellationToken = default)
        {
            CallCount++;

            if (string.IsNullOrEmpty(_content))
            {
                return Task.FromResult(new ContextResult(Array.Empty<ContextDocument>()));
            }

            var document = new ContextDocument(
                "requirements-doc", _content, 1.0, new Dictionary<string, string>());

            return Task.FromResult(new ContextResult(new[] { document }));
        }
    }

    private sealed class FakeAgentExecutor : IAgentExecutor
    {
        private readonly Queue<AgentExecutionResult> _responses;

        public FakeAgentExecutor(params AgentExecutionResult[] responses)
        {
            _responses = new Queue<AgentExecutionResult>(responses);
        }

        public List<AgentExecutionRequest> Requests { get; } = new();

        public Task<AgentExecutionResult> ExecuteAsync(
            AgentExecutionRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);

            if (_responses.Count == 0)
            {
                throw new InvalidOperationException("FakeAgentExecutor received more calls than configured responses.");
            }

            return Task.FromResult(_responses.Dequeue());
        }
    }

    private static AgentExecutionResult Ok(string json) => new(true, json, null, 5);

    private static ModelRouter MakeModelRouter()
    {
        var options = new ModelRoutingOptions
        {
            Models = new List<ModelCapability>
            {
                new()
                {
                    ModelId = "planning-model",
                    Provider = "test-provider",
                    ReasoningStrength = 5,
                    MaxContextTokens = 200_000,
                    CostTier = 4,
                    IsCodingOptimized = false,
                    IsAvailable = true
                }
            },
            CategoryProfiles = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["planning"] = new List<string> { "planning-model" }
            }
        };

        return new ModelRouter(options);
    }

    private static ModelTaskPlanner MakePlanner(
        FakeAgentExecutor executor, FakeContextProvider? contextProvider = null, int maxAttempts = 3)
    {
        return new ModelTaskPlanner(
            contextProvider ?? new FakeContextProvider(),
            MakeModelRouter(),
            executor,
            new ModelTaskPlannerOptions { MaxAttempts = maxAttempts },
            NullLogger<ModelTaskPlanner>.Instance);
    }

    private static TaskPlanRequest MakeRequest() =>
        new(Guid.NewGuid(), "Login feature", "Users must be able to log in.");

    private const string ValidPlanJson =
        """
        {
          "tasks": [
            { "id": "TASK-001", "title": "Create entity", "description": "desc", "type": "Implementation", "priority": "Normal", "dependencies": [], "acceptanceCriteria": "compiles", "estimatedComplexity": "Standard" },
            { "id": "TASK-002", "title": "Add tests", "description": "desc2", "type": "Test", "priority": "Normal", "dependencies": ["TASK-001"], "acceptanceCriteria": "tests pass", "estimatedComplexity": "Simple" }
          ]
        }
        """;

    [Fact]
    public async Task CreatePlanAsync_AcceptsValidPlan()
    {
        var executor = new FakeAgentExecutor(Ok(ValidPlanJson));
        var planner = MakePlanner(executor);

        var plan = await planner.CreatePlanAsync(MakeRequest());

        Assert.Equal(2, plan.Tasks.Count);
        var root = plan.Tasks.Single(t => t.Title == "Create entity");
        var dependent = plan.Tasks.Single(t => t.Title == "Add tests");
        Assert.Empty(root.Dependencies);
        Assert.Contains(root.Id, dependent.Dependencies);
        Assert.Equal(WorkItemType.Implementation, root.Type);
        Assert.Equal(TaskComplexity.Simple, dependent.EstimatedComplexity);
    }

    [Fact]
    public async Task CreatePlanAsync_RejectsMalformedJson_AfterExhaustingRetries()
    {
        var executor = new FakeAgentExecutor(
            Ok("this is not json"), Ok("still not json"), Ok("nope"));
        var planner = MakePlanner(executor, maxAttempts: 3);

        var ex = await Assert.ThrowsAsync<TaskPlanningException>(() =>
            planner.CreatePlanAsync(MakeRequest()));

        Assert.Contains("3 attempt", ex.Message);
        Assert.Equal(3, executor.Requests.Count);
    }

    [Fact]
    public async Task CreatePlanAsync_RejectsMissingRequiredFields()
    {
        const string missingType =
            """{"tasks":[{"id":"TASK-001","title":"A task"}]}""";

        var executor = new FakeAgentExecutor(Ok(missingType), Ok(missingType));
        var planner = MakePlanner(executor, maxAttempts: 2);

        await Assert.ThrowsAsync<TaskPlanningException>(() =>
            planner.CreatePlanAsync(MakeRequest()));
    }

    [Fact]
    public async Task CreatePlanAsync_RejectsMissingDependencyReference()
    {
        const string badDependency =
            """{"tasks":[{"id":"TASK-001","title":"A task","type":"Implementation","dependencies":["TASK-999"]}]}""";

        var executor = new FakeAgentExecutor(Ok(badDependency), Ok(badDependency));
        var planner = MakePlanner(executor, maxAttempts: 2);

        var ex = await Assert.ThrowsAsync<TaskPlanningException>(() =>
            planner.CreatePlanAsync(MakeRequest()));

        Assert.Contains("unknown dependency", ex.InnerException?.Message ?? ex.Message);
    }

    [Fact]
    public async Task CreatePlanAsync_RejectsCircularDependency()
    {
        const string circular =
            """
            {
              "tasks": [
                { "id": "A", "title": "A", "type": "Implementation", "dependencies": ["B"] },
                { "id": "B", "title": "B", "type": "Implementation", "dependencies": ["A"] }
              ]
            }
            """;

        var executor = new FakeAgentExecutor(Ok(circular), Ok(circular));
        var planner = MakePlanner(executor, maxAttempts: 2);

        await Assert.ThrowsAsync<TaskPlanningException>(() =>
            planner.CreatePlanAsync(MakeRequest()));
    }

    [Fact]
    public async Task CreatePlanAsync_InvalidPlan_NeverReturnsAResultToPersist()
    {
        // Nothing downstream can persist a plan that was never returned:
        // the method throws instead of yielding a partially-invalid TaskPlan.
        const string invalid = """{"tasks":[]}""";

        var executor = new FakeAgentExecutor(Ok(invalid), Ok(invalid));
        var planner = MakePlanner(executor, maxAttempts: 2);

        await Assert.ThrowsAsync<TaskPlanningException>(async () =>
        {
            var plan = await planner.CreatePlanAsync(MakeRequest());
            Assert.Fail($"Should not have returned a plan: {plan}");
        });
    }

    [Fact]
    public async Task CreatePlanAsync_PassesRetrievedContext_IntoThePlanningPrompt()
    {
        var contextProvider = new FakeContextProvider("Must support SSO login.");
        var executor = new FakeAgentExecutor(Ok(ValidPlanJson));
        var planner = MakePlanner(executor, contextProvider);

        await planner.CreatePlanAsync(MakeRequest());

        Assert.True(contextProvider.CallCount > 0);
        Assert.Contains("Must support SSO login.", executor.Requests[0].Prompt);
    }

    [Fact]
    public async Task CreatePlanAsync_FeedsPreviousValidationErrorBackIntoRetryPrompt()
    {
        const string badDependency =
            """{"tasks":[{"id":"TASK-001","title":"A task","type":"Implementation","dependencies":["TASK-999"]}]}""";

        var executor = new FakeAgentExecutor(Ok(badDependency), Ok(ValidPlanJson));
        var planner = MakePlanner(executor, maxAttempts: 2);

        var plan = await planner.CreatePlanAsync(MakeRequest());

        Assert.Equal(2, plan.Tasks.Count);
        Assert.Equal(2, executor.Requests.Count);
        Assert.Contains("Your previous response was invalid", executor.Requests[1].Prompt);
        Assert.Contains("unknown dependency", executor.Requests[1].Prompt);
    }

    [Fact]
    public async Task CreatePlanAsync_ThrowsImmediately_WithoutRetrying_OnAgentExecutionFailure()
    {
        var executor = new FakeAgentExecutor(new AgentExecutionResult(false, null, "docker not available", 5));
        var planner = MakePlanner(executor, maxAttempts: 3);

        await Assert.ThrowsAsync<TaskPlanningException>(() =>
            planner.CreatePlanAsync(MakeRequest()));

        Assert.Single(executor.Requests);
    }
}
