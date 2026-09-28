using System.Reflection;
using AI.Factory.Application.Abstractions.Gateway;
using AI.Factory.Application.Gateway;
using AI.Factory.Infrastructure.Gateways;
using Xunit;

namespace AI.Factory.Application.Tests.Gateway;

public sealed class ModelGatewayArchitectureTests
{
    private static readonly Assembly ApplicationAssembly = typeof(IModelGateway).Assembly;
    private static readonly Assembly InfrastructureAssembly = typeof(OpenRouterGateway).Assembly;

    [Fact]
    public void GatewayAbstractions_DoNotReferenceHttpClientOrHttpTypes()
    {
        var gatewayAbstractions = ApplicationAssembly.GetTypes()
            .Where(t => t.Namespace == "AI.Factory.Application.Abstractions.Gateway")
            .ToArray();

        Assert.NotEmpty(gatewayAbstractions);

        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
            | BindingFlags.Static | BindingFlags.DeclaredOnly;

        foreach (var type in gatewayAbstractions)
        {
            var referenced = type.GetFields(all).Select(f => f.FieldType)
                .Concat(type.GetProperties(all).Select(p => p.PropertyType))
                .Concat(type.GetConstructors(all).SelectMany(c => c.GetParameters()).Select(p => p.ParameterType))
                .Concat(type.GetMethods(all).SelectMany(m => m.GetParameters().Select(p => p.ParameterType).Append(m.ReturnType)));

            foreach (var t in referenced)
            {
                var name = t.FullName ?? t.Name;
                Assert.DoesNotContain("HttpClient", name, StringComparison.Ordinal);
                Assert.DoesNotContain("HttpRequestMessage", name, StringComparison.Ordinal);
                Assert.DoesNotContain("HttpResponseMessage", name, StringComparison.Ordinal);
                Assert.DoesNotContain("OpenRouter", name, StringComparison.Ordinal);
                Assert.DoesNotContain("OmniRoute", name, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void AgentCodeReviewer_HasNoDirectReferenceToGatewaysOrHttpClient()
    {
        var reviewerType = typeof(AI.Factory.Application.Orchestration.AgentCodeReviewer);
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        var referencedTypes = reviewerType.GetFields(all).Select(f => f.FieldType)
            .Concat(reviewerType.GetProperties(all).Select(p => p.PropertyType))
            .Concat(reviewerType.GetConstructors(all).SelectMany(c => c.GetParameters()).Select(p => p.ParameterType))
            .Concat(reviewerType.GetMethods(all).SelectMany(m => m.GetParameters().Select(p => p.ParameterType).Append(m.ReturnType)));

        foreach (var t in referencedTypes)
        {
            var name = t.FullName ?? t.Name;
            Assert.DoesNotContain("OpenRouter", name, StringComparison.Ordinal);
            Assert.DoesNotContain("OmniRoute", name, StringComparison.Ordinal);
            Assert.DoesNotContain("HttpClient", name, StringComparison.Ordinal);
            Assert.DoesNotContain("IModelGateway", name, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ExecutionAdapters_DoNotContainDirectVendorHttpCalls()
    {
        var hermesType = typeof(AI.Factory.Infrastructure.Agents.HermesExecutionAdapter);
        var openHandsType = typeof(AI.Factory.Infrastructure.Agents.OpenHands.OpenHandsExecutionAdapter);

        // Hermes constructor depends only on IAgentExecutor, IFailureClassifier, ILogger
        var hermesParams = hermesType.GetConstructors().Single().GetParameters().Select(p => p.ParameterType).ToArray();
        Assert.DoesNotContain(typeof(HttpClient), hermesParams);
        Assert.DoesNotContain(typeof(IHttpClientFactory), hermesParams);

        // OpenHands depends on OpenHandsClient, IModelGatewayResolver, etc. but not vendor-specific gateways
        var openHandsFields = openHandsType.GetFields(BindingFlags.Instance | BindingFlags.NonPublic).Select(f => f.FieldType).ToArray();
        foreach (var f in openHandsFields)
        {
            var name = f.FullName ?? f.Name;
            Assert.DoesNotContain("OpenRouterGateway", name, StringComparison.Ordinal);
            Assert.DoesNotContain("OmniRouteGateway", name, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Gateways_AreRegisteredInInfrastructure_AndImplementIModelGateway()
    {
        var gatewayTypes = InfrastructureAssembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(IModelGateway).IsAssignableFrom(t))
            .OrderBy(t => t.Name)
            .ToArray();

        var expected = new[] { typeof(OmniRouteGateway), typeof(OpenRouterGateway) }
            .OrderBy(t => t.Name)
            .ToArray();

        Assert.Equal(expected, gatewayTypes);
    }
}
