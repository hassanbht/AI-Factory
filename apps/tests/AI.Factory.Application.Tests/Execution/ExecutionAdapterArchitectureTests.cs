using System.Reflection;
using AI.Factory.Application.Abstractions.Execution;
using AI.Factory.Application.Execution;
using AI.Factory.Infrastructure.Agents;
using Xunit;

namespace AI.Factory.Application.Tests.Execution;

/// <summary>
/// Execution-adapter Phases B/C — structural guarantees for the seam, checked by
/// reflection over the built assemblies and by scanning source files, so the
/// boundary is enforced rather than merely intended.
/// </summary>
public sealed class ExecutionAdapterArchitectureTests
{
    private const string AbstractionsNamespace = "AI.Factory.Application.Abstractions.Execution";
    private const string ExecutionNamespace = "AI.Factory.Application.Execution";

    private static readonly Assembly ApplicationAssembly = typeof(AdapterDispatchingAgentRuntime).Assembly;
    private static readonly Assembly InfrastructureAssembly = typeof(HermesExecutionAdapter).Assembly;

    private static IEnumerable<Type> NewSeamTypes() => ApplicationAssembly.GetTypes().Where(t =>
        t.Namespace is AbstractionsNamespace or ExecutionNamespace);

    [Fact]
    public void ApplicationAssembly_DoesNotReferenceInfrastructureOrEfCore()
    {
        var references = ApplicationAssembly.GetReferencedAssemblies().Select(a => a.Name!).ToArray();

        Assert.DoesNotContain(references, n => n.StartsWith("AI.Factory.Infrastructure", StringComparison.Ordinal));
        Assert.DoesNotContain(references, n => n.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal));
    }

    [Fact]
    public void NewSeamTypes_ExposeNoInfrastructureRuntimeOrPersistenceTypes()
    {
        var forbidden = new[] { "Hermes", "OpenHands", "Docker", "DbContext", "EntityFrameworkCore", "Infrastructure", "Process" };
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
            | BindingFlags.Static | BindingFlags.DeclaredOnly;

        var offenders = new List<string>();

        foreach (var type in NewSeamTypes())
        {
            var referenced = type.GetFields(all).Select(f => f.FieldType)
                .Concat(type.GetProperties(all).Select(p => p.PropertyType))
                .Concat(type.GetConstructors(all).SelectMany(c => c.GetParameters()).Select(p => p.ParameterType))
                .Concat(type.GetMethods(all).SelectMany(m => m.GetParameters().Select(p => p.ParameterType).Append(m.ReturnType)));

            foreach (var t in referenced.SelectMany(Flatten))
            {
                var name = t.FullName ?? t.Name;
                if (forbidden.Any(f => name.Contains(f, StringComparison.Ordinal)))
                {
                    offenders.Add($"{type.FullName} -> {name}");
                }
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void Abstractions_DependOnlyOnBclAndDomain()
    {
        var offenders = ApplicationAssembly.GetTypes()
            .Where(t => t.Namespace == AbstractionsNamespace)
            .SelectMany(t => t.GetProperties().Select(p => p.PropertyType)
                .Concat(t.GetConstructors().SelectMany(c => c.GetParameters()).Select(p => p.ParameterType)))
            .SelectMany(Flatten)
            .Where(t => t.Namespace is not null
                && !t.Namespace.StartsWith("System", StringComparison.Ordinal)
                && !t.Namespace.StartsWith("AI.Factory.Domain", StringComparison.Ordinal)
                && t.Namespace != AbstractionsNamespace)
            .Select(t => t.FullName)
            .Distinct()
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Dispatcher_DependsOnlyOnTheResolverTheFailureClassifierAndALogger()
    {
        var parameters = typeof(AdapterDispatchingAgentRuntime).GetConstructors().Single().GetParameters()
            .Select(p => p.ParameterType)
            .ToArray();

        Assert.Equal(3, parameters.Length);
        Assert.Contains(typeof(ExecutionProfileResolver), parameters);
        Assert.Contains(typeof(AI.Factory.Application.Abstractions.IFailureClassifier), parameters);
        Assert.Contains(parameters, p => p.IsGenericType && p.GetGenericTypeDefinition().Name == "ILogger`1");
    }

    [Fact]
    public void NewApplicationSourceFiles_ContainNoRuntimeSpecificsUrlsOrPersistence()
    {
        var root = FindApplicationSourceDirectory();
        var files = Directory.GetFiles(Path.Combine(root, "Abstractions", "Execution"), "*.cs")
            .Concat(Directory.GetFiles(Path.Combine(root, "Execution"), "*.cs"))
            .ToArray();

        Assert.NotEmpty(files);

        var forbidden = new[]
        {
            "hermes", "openhands", "docker", "dbcontext", "http://", "https://", "processstartinfo",
            "system.diagnostics.process", "--provider", "-z", "openrouter", "omniroute"
        };

        var offenders = files
            .SelectMany(f => forbidden
                .Where(term => File.ReadAllText(f).Contains(term, StringComparison.OrdinalIgnoreCase))
                .Select(term => $"{Path.GetFileName(f)}: '{term}'"))
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void HermesExecutionAdapter_IsTheOnlyAdapter_NoFuturePlaceholders()
    {
        var adapters = new[] { ApplicationAssembly, InfrastructureAssembly }
            .SelectMany(a => a.GetTypes())
            .Where(t => !t.IsAbstract && !t.IsInterface && typeof(IExecutionAdapter).IsAssignableFrom(t))
            .ToArray();

        Assert.Equal(new[] { typeof(HermesExecutionAdapter) }, adapters);
    }

    [Fact]
    public void TransitionalAndLegacyRuntimeLayers_AreGone()
    {
        var names = new[] { ApplicationAssembly, InfrastructureAssembly }
            .SelectMany(a => a.GetTypes())
            .Select(t => t.Name)
            .ToArray();

        Assert.DoesNotContain("LegacyHermesRuntimeAdapter", names);
        Assert.DoesNotContain("HermesAgentRuntime", names);
        Assert.DoesNotContain("IHermesAgentRuntime", names);
    }

    [Fact]
    public void TheDispatcher_IsTheOnlyAgentRuntime()
    {
        var runtimes = new[] { ApplicationAssembly, InfrastructureAssembly }
            .SelectMany(a => a.GetTypes())
            .Where(t => !t.IsAbstract && !t.IsInterface
                && typeof(AI.Factory.Application.Abstractions.IAgentRuntime).IsAssignableFrom(t))
            .ToArray();

        Assert.Equal(new[] { typeof(AdapterDispatchingAgentRuntime) }, runtimes);
    }

    [Fact]
    public void HermesAdapter_DependsOnlyOnTheHermesExecutorChainTheClassifierAndALogger()
    {
        var parameters = typeof(HermesExecutionAdapter).GetConstructors().Single().GetParameters()
            .Select(p => p.ParameterType)
            .ToArray();

        Assert.Equal(3, parameters.Length);
        Assert.Contains(typeof(AI.Factory.Application.Abstractions.IAgentExecutor), parameters);
        Assert.Contains(typeof(AI.Factory.Application.Abstractions.IFailureClassifier), parameters);
        Assert.Contains(parameters, p => p.IsGenericType && p.GetGenericTypeDefinition().Name == "ILogger`1");

        // No persistence, artifact store or lifecycle state is reachable from it.
        var fieldTypes = typeof(HermesExecutionAdapter)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Select(f => f.FieldType)
            .ToArray();
        Assert.All(fieldTypes, t => Assert.True(
            t == typeof(AI.Factory.Application.Abstractions.IAgentExecutor)
                || t == typeof(AI.Factory.Application.Abstractions.IFailureClassifier)
                || (t.IsGenericType && t.GetGenericTypeDefinition().Name == "ILogger`1"),
            $"Unexpected dependency {t.FullName}"));
    }

    [Fact]
    public void HermesAdapterSource_DoesNotTouchPersistenceArtifactsLifecycleOrDefinitionPolicy()
    {
        var source = File.ReadAllText(Path.Combine(
            FindSourceRoot(), "AI.Factory.Infrastructure", "Agents", "HermesExecutionAdapter.cs"));

        var forbidden = new[]
        {
            "DbContext", "IArtifactRecorder", "IArtifactRepository", "ArtifactRepository", "RecordWorkspaceArtifacts",
            "Repository", "TransitionTo", "AgentRunStatus", "WorkItemStatus", "ProjectStatus", ".Enabled",
            "IWorkspaceResolver", "ProjectWorkspaceResolver"
        };

        foreach (var term in forbidden)
        {
            Assert.DoesNotContain(term, source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AgentOrchestrator_NeverConstructsRuntimeSpecificTypes()
    {
        var source = File.ReadAllText(Path.Combine(
            FindSourceRoot(), "AI.Factory.Application", "Agents", "AgentOrchestrator.cs"));

        Assert.DoesNotContain("new Hermes", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ExecutionAdapter(", source, StringComparison.Ordinal);
        Assert.DoesNotContain(
            typeof(AI.Factory.Application.Agents.AgentOrchestrator).Assembly.GetReferencedAssemblies(),
            a => a.Name!.StartsWith("AI.Factory.Infrastructure", StringComparison.Ordinal));
    }

    private static IEnumerable<Type> Flatten(Type type)
    {
        if (type.HasElementType)
        {
            foreach (var inner in Flatten(type.GetElementType()!))
            {
                yield return inner;
            }

            yield break;
        }

        yield return type;

        if (type.IsGenericType)
        {
            foreach (var argument in type.GetGenericArguments().SelectMany(Flatten))
            {
                yield return argument;
            }
        }
    }

    private static string FindApplicationSourceDirectory() => Path.Combine(FindSourceRoot(), "AI.Factory.Application");

    private static string FindSourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AI.Factory.slnx")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            throw new InvalidOperationException("Could not locate AI.Factory.slnx above the test assembly.");
        }

        return Path.Combine(dir.FullName, "src");
    }
}
