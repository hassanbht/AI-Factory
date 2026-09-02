using AI.Factory.Api.Middleware;
using AI.Factory.Application.Exceptions;
using AI.Factory.Application.Orchestration;
using AI.Factory.Domain.Enums;
using AI.Factory.Infrastructure.Resilience;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace AI.Factory.Api.Tests;

public sealed class ApiExceptionMapperTests
{
    private readonly ApiExceptionMapper _mapper = new();

    [Fact]
    public void NotFoundException_MapsTo404_ResourceNotFound()
    {
        var mapping = _mapper.Map(new NotFoundException("Project 'abc' was not found."));

        Assert.Equal(StatusCodes.Status404NotFound, mapping.StatusCode);
        Assert.Equal("Resource not found", mapping.Title);
        Assert.Equal("Project 'abc' was not found.", mapping.Detail);
        Assert.Equal("not_found", mapping.Code);
        Assert.Equal(FailureCategory.Validation, mapping.FailureCategory);
        Assert.False(mapping.ShouldAudit);
    }

    [Fact]
    public void KeyNotFoundException_MapsTo404()
    {
        var mapping = _mapper.Map(new KeyNotFoundException("Item missing"));

        Assert.Equal(StatusCodes.Status404NotFound, mapping.StatusCode);
        Assert.Equal("Resource not found", mapping.Title);
    }

    [Fact]
    public void ValidationException_MapsTo400_ValidationFailed()
    {
        var mapping = _mapper.Map(new ValidationException("Name is required."));

        Assert.Equal(StatusCodes.Status400BadRequest, mapping.StatusCode);
        Assert.Equal("Validation failed", mapping.Title);
        Assert.Equal("Name is required.", mapping.Detail);
        Assert.Equal("validation_error", mapping.Code);
        Assert.Equal(FailureCategory.Validation, mapping.FailureCategory);
        Assert.False(mapping.ShouldAudit);
    }

    [Fact]
    public void ArgumentException_MapsTo400()
    {
        var mapping = _mapper.Map(new ArgumentException("Invalid arg"));

        Assert.Equal(StatusCodes.Status400BadRequest, mapping.StatusCode);
        Assert.Equal("Validation failed", mapping.Title);
    }

    [Fact]
    public void ConflictException_MapsTo409_OperationConflict()
    {
        var mapping = _mapper.Map(new ConflictException("Approval '123' was already decided."));

        Assert.Equal(StatusCodes.Status409Conflict, mapping.StatusCode);
        Assert.Equal("Operation conflict", mapping.Title);
        Assert.Equal("Approval '123' was already decided.", mapping.Detail);
        Assert.Equal("conflict", mapping.Code);
        Assert.Equal(FailureCategory.Approval, mapping.FailureCategory);
        Assert.False(mapping.ShouldAudit);
    }

    [Fact]
    public void ForbiddenException_MapsTo403_Forbidden()
    {
        var mapping = _mapper.Map(new ForbiddenException("Access denied."));

        Assert.Equal(StatusCodes.Status403Forbidden, mapping.StatusCode);
        Assert.Equal("Forbidden", mapping.Title);
        Assert.Equal("Access denied.", mapping.Detail);
        Assert.Equal("forbidden", mapping.Code);
        Assert.Equal(FailureCategory.Infrastructure, mapping.FailureCategory);
        Assert.False(mapping.ShouldAudit);
    }

    [Fact]
    public void SecurityViolationException_MapsTo403_Forbidden()
    {
        var mapping = _mapper.Map(new SecurityViolationException("Disallowed path"));

        Assert.Equal(StatusCodes.Status403Forbidden, mapping.StatusCode);
        Assert.Equal("Forbidden", mapping.Title);
        Assert.Equal("forbidden", mapping.Code);
    }

    [Fact]
    public void TimeoutException_MapsTo504_OperationTimedOut()
    {
        var mapping = _mapper.Map(new TimeoutException("Took too long"));

        Assert.Equal(StatusCodes.Status504GatewayTimeout, mapping.StatusCode);
        Assert.Equal("Operation timed out", mapping.Title);
        Assert.Equal("The operation timed out.", mapping.Detail);
        Assert.Equal("timeout", mapping.Code);
        Assert.Equal(FailureCategory.Timeout, mapping.FailureCategory);
        Assert.False(mapping.ShouldAudit);
    }

    [Fact]
    public void OperationCanceledException_MapsTo408_RequestCancelled()
    {
        var mapping = _mapper.Map(new OperationCanceledException());

        Assert.Equal(StatusCodes.Status408RequestTimeout, mapping.StatusCode);
        Assert.Equal("Request cancelled", mapping.Title);
        Assert.Equal("The operation was cancelled.", mapping.Detail);
        Assert.Equal("timeout", mapping.Code);
        Assert.Equal(FailureCategory.Cancelled, mapping.FailureCategory);
        Assert.False(mapping.ShouldAudit);
    }

    [Fact]
    public void CircuitOpenException_MapsTo503_ServiceUnavailable_AndAuditable()
    {
        var mapping = _mapper.Map(new CircuitOpenException("Hermes"));

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, mapping.StatusCode);
        Assert.Equal("Service unavailable", mapping.Title);
        Assert.Equal("The service or an external dependency is temporarily unavailable.", mapping.Detail);
        Assert.Equal("service_unavailable", mapping.Code);
        Assert.Equal(FailureCategory.Provider, mapping.FailureCategory);
        Assert.True(mapping.ShouldAudit);
    }

    [Fact]
    public void UnknownException_MapsTo500_InternalServerError_HidingSensitiveDetails()
    {
        var mapping = _mapper.Map(new InvalidOperationException("Fatal database error: Server=secret;Password=123;"));

        Assert.Equal(StatusCodes.Status500InternalServerError, mapping.StatusCode);
        Assert.Equal("Internal server error", mapping.Title);
        Assert.Equal("An unexpected error occurred.", mapping.Detail);
        Assert.DoesNotContain("secret", mapping.Detail);
        Assert.Equal("internal_error", mapping.Code);
        Assert.Equal(FailureCategory.Unknown, mapping.FailureCategory);
        Assert.True(mapping.ShouldAudit);
    }

    [Fact]
    public void CreateProblemDetails_SetsStandardFields_AndCorrelationId()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/test/resource";

        var (mapping, problemDetails) = _mapper.CreateProblemDetails(
            new ValidationException("Field invalid"),
            context,
            "correlation-xyz-789");

        Assert.Equal(StatusCodes.Status400BadRequest, problemDetails.Status);
        Assert.Equal("Validation failed", problemDetails.Title);
        Assert.Equal("Field invalid", problemDetails.Detail);
        Assert.Equal("/api/test/resource", problemDetails.Instance);
        Assert.Equal("https://httpstatuses.com/400", problemDetails.Type);
        Assert.Equal("correlation-xyz-789", problemDetails.Extensions["correlationId"]);
        Assert.Equal("validation_error", problemDetails.Extensions["code"]);
    }
}
