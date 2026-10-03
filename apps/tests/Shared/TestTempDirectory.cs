using Xunit.Abstractions;

// Compiled into every test project (linked file), like TestGating.cs: one definition of "remove the scratch
// directory this test created", so a default `dotnet test` never leaves directories behind in %TEMP%.
namespace AI.Factory.TestSupport;

/// <summary>
/// Removes a test's scratch directory reliably on Windows. A plain <see cref="Directory.Delete(string, bool)"/>
/// fails on the read-only object files git writes under <c>.git/objects</c>, and on a file a just-killed child
/// process still holds for a moment. This clears read-only attributes first, retries briefly on a held handle,
/// and — when the directory still cannot be removed — reports it instead of swallowing the failure.
/// </summary>
public static class TestTempDirectory
{
    private const int MaxAttempts = 10;
    private static readonly TimeSpan RetryStep = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Deletes <paramref name="path"/> recursively. Returns true when the directory is gone. Never throws for an
    /// I/O or permission failure: the final failure is written to <paramref name="output"/> (or stderr when no
    /// test output is available) and false is returned.
    /// </summary>
    public static bool Delete(string path, ITestOutputHelper? output = null)
    {
        Exception? last = null;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                if (!Directory.Exists(path))
                {
                    return true;
                }

                ClearReadOnly(path);
                Directory.Delete(path, recursive: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                last = ex;
                if (attempt < MaxAttempts)
                {
                    Thread.Sleep(RetryStep * attempt);
                }
            }
        }

        Report(output, $"[TestTempDirectory] could not delete '{path}' after {MaxAttempts} attempts: {last?.GetType().Name}: {last?.Message}");
        return false;
    }

    private static void ClearReadOnly(string root)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.None,
            IgnoreInaccessible = true,
        };

        foreach (var entry in new DirectoryInfo(root).EnumerateFileSystemInfos("*", options))
        {
            if ((entry.Attributes & FileAttributes.ReadOnly) != 0)
            {
                entry.Attributes &= ~FileAttributes.ReadOnly;
            }
        }
    }

    private static void Report(ITestOutputHelper? output, string message)
    {
        if (output is not null)
        {
            try
            {
                output.WriteLine(message);
                return;
            }
            catch (InvalidOperationException)
            {
                // xunit rejects writes once the test has finished (e.g. cleanup running too late); fall through to stderr.
            }
        }

        Console.Error.WriteLine(message);
    }
}
