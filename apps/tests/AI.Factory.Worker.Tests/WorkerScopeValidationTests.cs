using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace AI.Factory.Worker.Tests;

/// <summary>
/// Phase M — the Worker crashed at startup under Development because OrchestrationRunWorker (a
/// singleton hosted service) took the scoped IAuditLogger: a captive dependency that Production's
/// relaxed validation hid while sharing one scoped DbContext across all iterations. This builds the
/// real composition root with the validation Development applies (ValidateScopes + ValidateOnBuild)
/// and resolves every hosted service from the root provider, exactly as the host does at startup.
/// </summary>
public sealed class WorkerScopeValidationTests
{
    [Fact]
    public void RealWorkerGraph_PassesDevelopmentScopeValidation_AndResolvesEveryHostedService()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ContentRootPath = FindWorkerProjectDirectory(),
            EnvironmentName = "Development"
        });
        Program.ConfigureServices(builder);
        builder.ConfigureContainer(new DefaultServiceProviderFactory(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        }));

        using var host = builder.Build();
        var hostedServices = host.Services.GetServices<IHostedService>().ToList();

        Assert.Contains(hostedServices, s => s is OrchestrationRunWorker);
        Assert.Contains(hostedServices, s => s is AgentRunWorker);
        Assert.Contains(hostedServices, s => s is WorkflowRecoveryHostedService);
        Assert.Contains(hostedServices, s => s is TaskSchedulerHostedService);
    }

    private static string FindWorkerProjectDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AI.Factory.slnx")))
        {
            dir = dir.Parent;
        }

        return dir is null
            ? throw new InvalidOperationException("Could not locate AI.Factory.slnx above the test assembly.")
            : Path.Combine(dir.FullName, "src", "AI.Factory.Worker");
    }
}
