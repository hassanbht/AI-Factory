using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using AI.Factory.Application.Orchestration;

namespace AI.Factory.Infrastructure.Agents;

public sealed class SystemProcessRunner : IProcessRunner
{
    private readonly CommandPolicyOptions _commandPolicy;

    public SystemProcessRunner(CommandPolicyOptions? commandPolicy = null)
    {
        _commandPolicy = commandPolicy ?? new CommandPolicyOptions();
    }

    public async Task<ProcessRunResult> RunAsync(
        ProcessRunRequest request,
        CancellationToken cancellationToken = default)
    {
        EnforceCommandPolicy(request.FileName);

        var startInfo = new ProcessStartInfo
        {
            FileName = request.FileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (!string.IsNullOrWhiteSpace(request.WorkingDirectory))
        {
            startInfo.WorkingDirectory = request.WorkingDirectory;
        }

        if (request.EnvironmentVariables is not null)
        {
            // Replaces the inherited environment entirely — an explicit
            // allow-list, not an addition on top of the full parent
            // environment, so a caller can enforce least privilege.
            startInfo.EnvironmentVariables.Clear();

            foreach (var (key, value) in request.EnvironmentVariables)
            {
                startInfo.EnvironmentVariables[key] = value;
            }
        }

        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };

        var standardOutput = new StringBuilder();
        var standardError = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                standardOutput.AppendLine(e.Data);
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                standardError.AppendLine(e.Data);
            }
        };

        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            throw new ProcessLaunchException(
                $"Failed to start process '{request.FileName}': {ex.Message}",
                ex);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = new CancellationTokenSource(request.Timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCts.Token);

        var timedOut = false;

        try
        {
            await process.WaitForExitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw;
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
            TryKill(process);
        }

        return new ProcessRunResult(
            timedOut ? -1 : process.ExitCode,
            standardOutput.ToString(),
            standardError.ToString(),
            timedOut);
    }

    private void EnforceCommandPolicy(string fileName)
    {
        var command = Path.GetFileNameWithoutExtension(fileName);

        if (_commandPolicy.DeniedCommands.Any(denied => string.Equals(denied, command, StringComparison.OrdinalIgnoreCase)))
        {
            throw new SecurityViolationException($"Command '{command}' is explicitly denied by policy.");
        }

        if (!_commandPolicy.AllowedCommands.Any(allowed => string.Equals(allowed, command, StringComparison.OrdinalIgnoreCase)))
        {
            throw new SecurityViolationException($"Command '{command}' is not in the allowed-commands list.");
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best-effort: the process may have exited between the check and the kill.
        }
    }
}
