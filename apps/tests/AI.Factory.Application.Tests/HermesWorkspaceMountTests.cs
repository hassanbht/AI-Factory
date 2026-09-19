using AI.Factory.Application.Abstractions;
using AI.Factory.Infrastructure.Agents;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AI.Factory.Application.Tests;

/// <summary>
/// Phase 18.29.1 §A — how the Factory-resolved workspace reaches /workspace inside
/// the Hermes container.
///
/// This is release blocker 1's real substance. Adding a Docker CLI to the Worker
/// image is only half the fix: once the Worker issues `docker run` over a socket
/// belonging to the HOST daemon, a bind-mount path that exists only inside the
/// Worker container silently resolves to an unrelated (empty) host directory, and
/// the agent runs against nothing at all — a failure mode that looks like a
/// mysteriously unproductive agent rather than a mount bug.
/// </summary>
public sealed class HermesWorkspaceMountTests
{
    private sealed class CapturingProcessRunner : IProcessRunner
    {
        public ProcessRunRequest? LastRequest { get; private set; }

        public Task<ProcessRunResult> RunAsync(
            ProcessRunRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(new ProcessRunResult(0, "ok", string.Empty, TimedOut: false));
        }
    }

    private static string CreateWorkspaceRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "ai-factory-mount-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    [Fact]
    public async Task HostTopology_KeepsTheOriginalBindMount()
    {
        var root = CreateWorkspaceRoot();
        var runner = new CapturingProcessRunner();

        var options = new HermesOptions
        {
            DockerImage = "test/image:latest",
            DataVolume = "/host/data",
            WorkspaceRoot = root
            // WorkspaceVolumeName deliberately unset — the host/dev/test topology.
        };

        var executor = new HermesAgentExecutor(options, runner, NullLogger<HermesAgentExecutor>.Instance);

        await executor.ExecuteAsync(new AgentExecutionRequest("coder", null, "task", string.Empty));

        var args = runner.LastRequest!.Arguments;

        Assert.Contains("-v", args);
        Assert.Contains(args, a => a.EndsWith(":/workspace", StringComparison.Ordinal));
        Assert.DoesNotContain(args, a => a.Contains("volume-subpath", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ContainerTopology_MountsTheWorkspaceAsASubpathOfTheSharedVolume()
    {
        var root = CreateWorkspaceRoot();
        var workspace = Path.Combine(root, "project-a");
        Directory.CreateDirectory(workspace);

        var runner = new CapturingProcessRunner();

        var options = new HermesOptions
        {
            DockerImage = "test/image:latest",
            DataVolume = "ai_factory_production_hermes",
            WorkspaceRoot = root,
            WorkspaceVolumeName = "ai_factory_production_workspace"
        };

        var executor = new HermesAgentExecutor(options, runner, NullLogger<HermesAgentExecutor>.Instance);

        await executor.ExecuteAsync(new AgentExecutionRequest("coder", null, "task", workspace));

        var args = runner.LastRequest!.Arguments;

        Assert.Contains("--mount", args);
        Assert.Contains(
            "type=volume,source=ai_factory_production_workspace,target=/workspace,volume-subpath=project-a",
            args);

        // The host-path bind mount must be gone, not merely accompanied — leaving it
        // in would re-create the empty-host-directory bug alongside the correct mount.
        Assert.DoesNotContain(args, a => a.EndsWith($"{workspace}:/workspace", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ContainerTopology_RefusesAWorkspaceOutsideTheVolumeRoot()
    {
        var root = CreateWorkspaceRoot();

        // A sibling directory: real, existing, and entirely outside the volume.
        var outside = Path.Combine(Path.GetTempPath(), "ai-factory-mount-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);

        var runner = new CapturingProcessRunner();

        var options = new HermesOptions
        {
            DockerImage = "test/image:latest",
            DataVolume = "ai_factory_production_hermes",
            WorkspaceRoot = root,
            WorkspaceVolumeName = "ai_factory_production_workspace"
        };

        var executor = new HermesAgentExecutor(options, runner, NullLogger<HermesAgentExecutor>.Instance);

        var result = await executor.ExecuteAsync(
            new AgentExecutionRequest("coder", null, "task", outside));

        // Refused outright rather than mounted: subpath mounting must never become a
        // way to hand Hermes a directory the workspace boundary would have rejected.
        Assert.False(result.Success);
        Assert.Null(runner.LastRequest);
        Assert.Contains("outside the configured workspace root", result.Error);
    }

    [Fact]
    public async Task FileOwnershipIsControlledByHermesUidEnv_NotByDockerUser()
    {
        var root = CreateWorkspaceRoot();
        var runner = new CapturingProcessRunner();

        var options = new HermesOptions
        {
            DockerImage = "test/image:latest",
            DataVolume = "ai_factory_production_hermes",
            WorkspaceRoot = root,
            AdditionalEnvironmentVariables =
            {
                ["HERMES_UID"] = "1654",
                ["HERMES_GID"] = "1654"
            }
        };

        var executor = new HermesAgentExecutor(options, runner, NullLogger<HermesAgentExecutor>.Instance);

        await executor.ExecuteAsync(new AgentExecutionRequest("coder", null, "task", string.Empty));

        var args = runner.LastRequest!.Arguments;

        Assert.Contains("HERMES_UID=1654", args);
        Assert.Contains("HERMES_GID=1654", args);

        // `docker run --user` is the obvious-looking way to make agent output
        // land under the Worker's UID, and it is the wrong one: the Hermes image
        // runs an s6-overlay supervision tree that must bootstrap as root and
        // refuses to start under a pinned arbitrary UID. Its supported mechanism
        // is the HERMES_UID/HERMES_GID remap above. Asserted so nobody
        // reintroduces --user as a "simplification".
        Assert.DoesNotContain("--user", args);
    }
}
