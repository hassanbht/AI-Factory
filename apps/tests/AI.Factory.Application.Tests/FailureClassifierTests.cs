using AI.Factory.Application.Abstractions;
using AI.Factory.Domain.Enums;
using Xunit;

namespace AI.Factory.Application.Tests;

public sealed class FailureClassifierTests
{
    private readonly FailureClassifier _classifier = new();

    [Fact]
    public void Classify_WithValidationHint_ReturnsValidationCategory()
    {
        var result = _classifier.Classify("field 'Name' is required", hintCategory: "Validation");

        Assert.Equal(FailureCategory.Validation, result.Category);
        Assert.False(result.IsTransient);
    }

    [Fact]
    public void Classify_WithBuildHint_ReturnsBuildCategory()
    {
        var result = _classifier.Classify("CS0103: name does not exist", hintCategory: "Build");

        Assert.Equal(FailureCategory.Build, result.Category);
    }

    [Fact]
    public void Classify_WithTestHint_ReturnsTestCategory()
    {
        var result = _classifier.Classify("3 tests failed", hintCategory: "Test");

        Assert.Equal(FailureCategory.Test, result.Category);
    }

    [Fact]
    public void Classify_UnknownException_ReturnsUnknownCategory_NeverGuessed()
    {
        var result = _classifier.Classify(new FormatException("weird input"));

        Assert.Equal(FailureCategory.Unknown, result.Category);
        Assert.False(result.IsTransient);
    }

    [Fact]
    public void Classify_TimeoutException_ReturnsTransientTimeoutCategory()
    {
        var result = _classifier.Classify(new TimeoutException("took too long"));

        Assert.Equal(FailureCategory.Timeout, result.Category);
        Assert.True(result.IsTransient);
    }

    [Fact]
    public void Classify_OperationCanceledException_ReturnsCancelledCategory()
    {
        var result = _classifier.Classify(new OperationCanceledException());

        Assert.Equal(FailureCategory.Cancelled, result.Category);
    }
}
