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
            RedirectStandardInput = true,
            // Phase M — CLIs emit/consume UTF-8 (JSON events, prompts); the Windows default would be
            // the OEM code page.
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
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

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        // Phase N — "the root process exited" is observed on its own, not through WaitForExitAsync: with
        // redirected output that call also waits for the pipes to reach EOF, and a process the CLI started
        // (a build server, an MSBuild node) can keep them open long after the CLI itself finished, which
        // turned a completed run into a TimedOut one.
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        process.Exited += (_, _) => exited.TrySetResult();

        var standardOutput = new StringBuilder();
        var standardError = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            standardOutput.AppendLine(e.Data);

            try
            {
                request.OnStandardOutputLine?.Invoke(e.Data);
            }
            catch (Exception)
            {
                // Runs on a thread-pool callback: an exception here would crash the whole Worker.
                // A misbehaving observer only loses its live view; the full output is still captured.
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

        using var timeoutCts = new CancellationTokenSource(request.Timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCts.Token);

        var timedOut = false;
        Task stdin = Task.CompletedTask;

        // Phase M — from here on the child is running: whatever happens, it never outlives this call.
        try
        {
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            request.OnStarted?.Invoke(process);

            // Written concurrently with the wait, so a child that never reads stdin (hung start-up) cannot
            // block us past the timeout/cancellation: killing it breaks the pipe and ends the write.
            stdin = WriteStandardInputAsync(process, request.StandardInput);

            try
            {
                await exited.Task.WaitAsync(linkedCts.Token);
                // Only the caller's cancellation applies here: the process already finished inside its deadline.
                await DrainOutputAsync(process, cancellationToken);
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
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
        finally
        {
            await stdin;
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

    /// <summary>
    /// Kills the whole process tree, then waits (bounded) for the root to exit so its handles and
    /// output are released before the caller cleans up. ponytail: tree walk at kill time — a
    /// grandchild that was already re-parented (its parent exited earlier) is not found here; the
    /// per-run reaper (LocalProcessLifecycleManager) and container removal cover the crash cases.
    /// </summary>
    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            process.WaitForExit(KillWaitMilliseconds);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or AggregateException or NotSupportedException)
        {
            // The process exited between the check and the kill, or part of the tree could not be killed
            // (Kill(entireProcessTree) reports that as AggregateException); the caller's outcome stands.
        }
    }

    /// <summary>
    /// After the root process exited, gives the redirected pipes a bounded time to deliver what is still
    /// buffered. A descendant that keeps them open (it inherited the handles) no longer holds the run
    /// hostage: the root's exit code and everything already read stand.
    /// </summary>
    private static async Task DrainOutputAsync(Process process, CancellationToken cancellationToken)
    {
        using var grace = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        grace.CancelAfter(PipeDrainGrace);

        try
        {
            await process.WaitForExitAsync(grace.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Grace elapsed with a descendant still holding the pipes; nothing more to wait for.
        }
    }

    private static async Task WriteStandardInputAsync(Process process, string? text)
    {
        try
        {
            if (!string.IsNullOrEmpty(text))
            {
                await process.StandardInput.WriteAsync(text);
            }

            process.StandardInput.Close();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The child exited, was killed, or closed stdin before reading it; its exit code tells the story.
        }
    }

    private const int KillWaitMilliseconds = 10_000;

    private static readonly TimeSpan PipeDrainGrace = TimeSpan.FromSeconds(5);
}
