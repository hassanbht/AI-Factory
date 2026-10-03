using AI.Factory.Application.Abstractions.Execution;
using AI.Factory.Infrastructure.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AI.Factory.Infrastructure.Agents.Cli;

public static class CliExecutionServiceCollectionExtensions
{
    /// <summary>
    /// Phase M — registers the Claude Code and Codex execution adapters and the local-process reaper, in one
    /// place for both composition roots. Registering an adapter selects nothing: only an Execution profile that
    /// names it does. Options are validated here, at startup.
    ///
    /// Call this BEFORE registering <see cref="DockerContainerLifecycleManager"/>: a single
    /// <see cref="IContainerLifecycleManager"/> resolves to the last registration, and the OpenHands adapter
    /// needs the Docker one. Orphan recovery enumerates all of them.
    /// </summary>
    public static IServiceCollection AddCliExecutionAdapters(
        this IServiceCollection services, CliRuntimeOptions cli, ClaudeCodeOptions claude, CodexOptions codex)
    {
        cli.StateRoot = DeploymentPaths.Expand(cli.StateRoot) ?? cli.StateRoot;
        claude.Validate();
        codex.Validate();

        services.AddSingleton(cli);
        services.AddSingleton(claude);
        services.AddSingleton(codex);
        services.AddScoped<IContainerLifecycleManager, LocalProcessLifecycleManager>();
        services.AddScoped<IExecutionAdapter, ClaudeCodeExecutionAdapter>();
        services.AddScoped<IExecutionAdapter, CodexExecutionAdapter>();
        return services;
    }
}
