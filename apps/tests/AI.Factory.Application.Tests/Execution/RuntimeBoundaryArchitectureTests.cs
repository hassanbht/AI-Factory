using System.Reflection;
using AI.Factory.Application.Abstractions;
using AI.Factory.Application.Abstractions.Execution;
using AI.Factory.Infrastructure.Agents;
using AI.Factory.Infrastructure.Agents.OpenHands;
using Xunit;

namespace AI.Factory.Application.Tests.Execution;

/// <summary>
/// Phase L — the runtime-state boundary, enforced structurally:
///   Application (Factory lifecycle, AgentRun state) knows no runtime, container or transport;
///   execution adapters and runtimes never touch Factory persistence or domain entities — they
///   return an ExecutionOutcome / AgentExecutionResult and the Factory alone records it;
///   and no model path bypasses the adapters (no Application type holds the raw IAgentExecutor).
/// </summary>
public sealed class RuntimeBoundaryArchitectureTests
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
        | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static readonly Assembly Application = typeof(IExecutionAdapter).Assembly;

    [Fact]
    public void ApplicationLayer_ReferencesNoInfrastructure_AndNamesNoRuntimeOrContainerEngine()
    {
        Assert.DoesNotContain(Application.GetReferencedAssemblies(), a =>
            a.Name!.StartsWith("AI.Factory.Infrastructure", StringComparison.Ordinal)
            || a.Name.Contains("Docker", StringComparison.OrdinalIgnoreCase));

        Assert.DoesNotContain(Application.GetTypes(), t =>
            t.Name.Contains("Docker", StringComparison.OrdinalIgnoreCase)
            || t.Name.Contains("Hermes", StringComparison.OrdinalIgnoreCase)
            || t.Name.Contains("OpenHands", StringComparison.OrdinalIgnoreCase)
            || t.Name.Contains("Claude", StringComparison.OrdinalIgnoreCase)
            || t.Name.Contains("Codex", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NoApplicationType_HoldsTheRawAgentExecutor_SoNoModelPathBypassesTheExecutionAdapters()
    {
        Assert.DoesNotContain(Application.GetTypes(), t => ReferencedTypes(t).Contains(typeof(IAgentExecutor)));
    }

    [Theory]
    [InlineData(typeof(HermesExecutionAdapter))]
    [InlineData(typeof(HermesAgentExecutor))]
    [InlineData(typeof(OpenHandsExecutionAdapter))]
    [InlineData(typeof(AI.Factory.Infrastructure.Agents.Cli.ClaudeCodeExecutionAdapter))]
    [InlineData(typeof(AI.Factory.Infrastructure.Agents.Cli.CodexExecutionAdapter))]
    [InlineData(typeof(AI.Factory.Infrastructure.Agents.Cli.CliExecutionAdapter))]
    public void ExecutionAdaptersAndRuntimes_NeverTouchFactoryPersistenceOrDomainEntities(Type runtimeType)
    {
        Assert.All(ReferencedTypes(runtimeType), t =>
        {
            Assert.NotEqual("AI.Factory.Infrastructure.Persistence", t.Namespace);
            Assert.NotEqual("AI.Factory.Domain.Entities", t.Namespace);
            Assert.False(t.Name.EndsWith("Repository", StringComparison.Ordinal), $"{runtimeType.Name} depends on {t.Name}");
            Assert.NotEqual(typeof(IArtifactRecorder), t);
        });
    }

    /// <summary>Field, property and constructor-parameter types, generic arguments flattened.</summary>
    private static HashSet<Type> ReferencedTypes(Type type)
    {
        var direct = type.GetFields(All).Select(f => f.FieldType)
            .Concat(type.GetProperties(All).Select(p => p.PropertyType))
            .Concat(type.GetConstructors(All).SelectMany(c => c.GetParameters()).Select(p => p.ParameterType));

        var result = new HashSet<Type>();
        var pending = new Stack<Type>(direct);
        while (pending.TryPop(out var t))
        {
            if (result.Add(t))
            {
                foreach (var argument in t.IsGenericType ? t.GetGenericArguments() : Type.EmptyTypes)
                {
                    pending.Push(argument);
                }
            }
        }

        return result;
    }
}
