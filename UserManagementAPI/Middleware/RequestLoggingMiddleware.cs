using System.Diagnostics;
using System.Security.Claims;

namespace UserManagementAPI.Middleware;

/// <summary>
/// Audit log of every request that reaches the API: HTTP method, path, response status code,
/// duration, the authenticated client, and the trace ID (which also appears in error responses).
///
/// Uses a source-generated LoggerMessage (no string formatting or boxing when the level is disabled)
/// and Stopwatch.GetTimestamp (no Stopwatch allocation), so the cost per request is tiny.
/// Bodies, query strings and headers are not logged, so no personal data or tokens end up in the logs.
/// </summary>
public sealed partial class RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var start = Stopwatch.GetTimestamp();
        var failed = false;
        try
        {
            await next(context);
        }
        catch
        {
            // The error middleware (outside this one) will turn this into a 500; record that status here.
            failed = true;
            throw;
        }
        finally
        {
            var status = failed ? StatusCodes.Status500InternalServerError : context.Response.StatusCode;
            var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            var client = context.User.FindFirstValue(ClaimTypes.Name) ?? "anonymous";

            LogRequest(logger, Level(status), context.Request.Method, context.Request.Path, status, elapsed, client, context.TraceIdentifier);
        }
    }

    // 5xx = error, 4xx = warning, everything else = information.
    private static LogLevel Level(int status) => status switch
    {
        >= 500 => LogLevel.Error,
        >= 400 => LogLevel.Warning,
        _ => LogLevel.Information
    };

    [LoggerMessage(Message = "HTTP {Method} {Path} responded {StatusCode} in {ElapsedMs:0.0} ms (client: {Client}, traceId: {TraceId})")]
    private static partial void LogRequest(ILogger logger, LogLevel level, string method, string path, int statusCode,
        double elapsedMs, string client, string traceId);
}
