using Microsoft.AspNetCore.Mvc;

namespace UserManagementAPI.Middleware;

/// <summary>
/// First middleware in the pipeline. Catches any unhandled exception thrown by later middleware or
/// endpoints, logs it, and returns a consistent JSON error instead of crashing the request:
/// <code>{ "error": "Internal server error.", "status": 500, "traceId": "..." , ... }</code>
/// Exception details are logged but never sent to the client.
/// </summary>
public sealed partial class ErrorHandlingMiddleware(
    RequestDelegate next,
    IProblemDetailsService problemDetails,
    ILogger<ErrorHandlingMiddleware> logger)
{
    public const string InternalServerError = "Internal server error.";

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The client disconnected; nobody is waiting for a response, so this isn't an error.
            LogClientDisconnected(logger, context.Request.Method, context.Request.Path);
        }
        catch (Exception exception)
        {
            await HandleAsync(context, exception);
        }
    }

    private async Task HandleAsync(HttpContext context, Exception exception)
    {
        var (status, message) = exception switch
        {
            // e.g. request body too large or malformed at the server level
            BadHttpRequestException bad => (bad.StatusCode, "Bad request."),
            _ => (StatusCodes.Status500InternalServerError, InternalServerError)
        };

        LogUnhandledException(logger, exception, context.Request.Method, context.Request.Path, status);

        if (context.Response.HasStarted)
        {
            // Headers were already sent, so the status can't be changed. Rethrow so the server aborts the response.
            throw new InvalidOperationException("The response had already started when an error occurred.", exception);
        }

        context.Response.Clear();
        context.Response.StatusCode = status;

        await problemDetails.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Status = status,
                Title = status == StatusCodes.Status500InternalServerError ? "Server error" : "Bad request",
                Detail = message
            }
        });
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception for {Method} {Path}; returning {StatusCode}.")]
    private static partial void LogUnhandledException(ILogger logger, Exception exception, string method, string path, int statusCode);

    [LoggerMessage(Level = LogLevel.Information, Message = "Client disconnected during {Method} {Path}.")]
    private static partial void LogClientDisconnected(ILogger logger, string method, string path);
}
