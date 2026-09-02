using AI.Factory.Application.Abstractions;

namespace AI.Factory.Application.Orchestration;

public sealed class ValidationEngine : IValidationEngine
{
    private static readonly string[] FailureKeywords = { "error", "fail", "fehler" };

    private readonly IBuildExecutor _buildExecutor;
    private readonly ITestExecutor _testExecutor;
    private readonly ExecutionLimitsOptions _executionLimits;

    public ValidationEngine(
        IBuildExecutor buildExecutor, ITestExecutor testExecutor, ExecutionLimitsOptions executionLimits)
    {
        _buildExecutor = buildExecutor;
        _testExecutor = testExecutor;
        _executionLimits = executionLimits;
    }

    public async Task<ValidationResult> ValidateAsync(
        ValidationRequest request,
        CancellationToken cancellationToken = default)
    {
        var build = await _buildExecutor.BuildAsync(
            new BuildRequest(request.ProjectPath, TimeSpan.FromMinutes(_executionLimits.BuildTimeoutMinutes)),
            cancellationToken);

        if (!build.Success)
        {
            var buildFailures = Normalize("Build", build.StandardError, build.StandardOutput);

            return new ValidationResult(
                false, false, build.StandardOutput, null, null, buildFailures);
        }

        var test = await _testExecutor.TestAsync(
            new TestRequest(request.ProjectPath, TimeSpan.FromMinutes(_executionLimits.TestTimeoutMinutes)),
            cancellationToken);

        var testFailures = test.Success
            ? Array.Empty<ValidationFailure>()
            : Normalize("Test", test.StandardError, test.StandardOutput);

        return new ValidationResult(
            test.Success, true, build.StandardOutput, test.Success, test.StandardOutput, testFailures);
    }

    private static IReadOnlyList<ValidationFailure> Normalize(string category, params string[] texts)
    {
        return texts
            .SelectMany(text => text.Split('\n'))
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && FailureKeywords.Any(
                keyword => line.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
            .Select(line => new ValidationFailure(category, line))
            .ToList();
    }
}
