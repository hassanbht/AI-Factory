# Phase 16.1 — Global Exception Handling

Implement production-grade global exception handling for AI.Factory.

Current architecture and layer boundaries must be preserved.

Requirements:

1. Do not expose stack traces or internal exception details to API clients.

2. Create domain/application-level exception types where appropriate:
   - NotFoundException
   - ConflictException
   - ValidationException
   - ForbiddenException

3. Implement a GlobalExceptionMiddleware in AI.Factory.Api.

4. Map exceptions to HTTP responses:

   ValidationException -> 400
   NotFoundException -> 404
   ConflictException -> 409
   UnauthorizedAccessException / ForbiddenException -> 403
   OperationCanceledException / TimeoutException -> appropriate timeout response
   unexpected exceptions -> 500

5. Return a consistent JSON error envelope:

   {
     "traceId": "...",
     "status": 400,
     "code": "...",
     "message": "..."
   }

6. Use ASP.NET Core logging for unexpected exceptions.

7. Do not silently catch exceptions in controllers/services.

8. Register the middleware in Program.cs in the correct pipeline order.

9. Update existing API endpoints so InvalidOperationException cases that represent expected business failures are converted to explicit application exceptions where appropriate.

10. Add tests:
    - validation error
    - not found
    - conflict
    - unexpected exception
    - traceId presence

Use WebApplicationFactory for HTTP-level integration tests where practical.

Run:

dotnet build apps\AI.Factory.slnx
dotnet test apps\AI.Factory.slnx

Do not change unrelated architecture.