using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace backend;

public class GlobalExceptionHandler : IExceptionHandler
{
    private readonly ILogger<GlobalExceptionHandler> _logger;

    public GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger)
    {
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        // 1. Log the exception with correlation context
        _logger.LogError(exception, "An unhandled exception occurred.");

        // 2. Construct the standard Problem Details response
        var problemDetails = new ProblemDetails
        {
            Status = StatusCodes.Status500InternalServerError,
            Title = "Server Error",
            Type = "https://datatracker.ietf.org/doc/html/rfc7231#section-6.6.1",
            Detail = "An unexpected error occurred while processing your request.",
            Instance = httpContext.Request.Path
        };

        // You can conditionally format based on exception type (e.g. ValidationException -> 400)
        // if (exception is ValidationException) { ... }

        httpContext.Response.StatusCode = problemDetails.Status.Value;
        
        // 3. Write the ProblemDetails object as application/problem+json
        await httpContext.Response.WriteAsJsonAsync(problemDetails, cancellationToken);

        // Return true to signal that the exception has been handled
        return true;
    }
}
