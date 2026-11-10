using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace UserManagementAPI.Middleware;

/// <summary>
/// Requires a valid bearer token on every request (except configured anonymous paths):
/// <c>Authorization: Bearer &lt;token&gt;</c>. Missing or invalid tokens get 401 Unauthorized
/// with a JSON error body and a WWW-Authenticate header, and the request never reaches the API.
///
/// Security notes:
/// - Tokens are hashed (SHA-256) once at startup; only hashes are kept in memory.
/// - The incoming token is compared with <see cref="CryptographicOperations.FixedTimeEquals"/>
///   to avoid timing attacks.
/// - Token values are never logged.
/// - If no valid tokens are configured, every request is rejected (fail closed).
/// </summary>
public sealed partial class TokenAuthenticationMiddleware
{
    public const string Scheme = "Bearer";

    private readonly RequestDelegate next;
    private readonly IProblemDetailsService problemDetails;
    private readonly ILogger<TokenAuthenticationMiddleware> logger;
    private readonly (byte[] Hash, string Name)[] clients;
    private readonly string[] anonymousPaths;

    public TokenAuthenticationMiddleware(
        RequestDelegate next,
        IOptions<TokenAuthenticationOptions> options,
        IProblemDetailsService problemDetails,
        ILogger<TokenAuthenticationMiddleware> logger)
    {
        this.next = next;
        this.problemDetails = problemDetails;
        this.logger = logger;

        var settings = options.Value;
        clients = settings.Clients
            .Where(c => !string.IsNullOrWhiteSpace(c.Name) && c.Token.Length >= settings.MinimumTokenLength)
            .Select(c => (Hash(c.Token), c.Name))
            .ToArray();
        anonymousPaths = settings.AnonymousPaths.ToArray();

        if (clients.Length < settings.Clients.Count)
        {
            LogIgnoredClients(logger, settings.Clients.Count - clients.Length, settings.MinimumTokenLength);
        }
        if (clients.Length == 0)
        {
            LogNoClients(logger);
        }
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (IsAnonymous(context.Request.Path))
        {
            await next(context);
            return;
        }

        var token = ReadBearerToken(context.Request);
        if (token is null)
        {
            LogRejected(logger, context.Request.Method, context.Request.Path, "missing or malformed Authorization header", context.TraceIdentifier);
            await ChallengeAsync(context, "A bearer token is required. Send it as 'Authorization: Bearer <token>'.", error: null);
            return;
        }

        var client = FindClient(token);
        if (client is null)
        {
            LogRejected(logger, context.Request.Method, context.Request.Path, "invalid token", context.TraceIdentifier);
            await ChallengeAsync(context, "The bearer token is invalid.", error: "invalid_token");
            return;
        }

        // Make the caller's identity available to the rest of the pipeline (logging, controllers).
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, client)], authenticationType: Scheme));
        await next(context);
    }

    private bool IsAnonymous(PathString path)
    {
        foreach (var prefix in anonymousPaths)
        {
            if (path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>Returns the token from "Authorization: Bearer xyz", or null if the header is missing or malformed.</summary>
    private static string? ReadBearerToken(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        if (!header.StartsWith(Scheme + " ", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var token = header[(Scheme.Length + 1)..].Trim();
        return token.Length == 0 ? null : token;
    }

    private string? FindClient(string token)
    {
        var hash = Hash(token);
        string? match = null;
        // Check every client (no early exit) so the time taken doesn't reveal which one matched.
        foreach (var (clientHash, name) in clients)
        {
            if (CryptographicOperations.FixedTimeEquals(hash, clientHash)) match = name;
        }
        return match;
    }

    private static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    private async Task ChallengeAsync(HttpContext context, string detail, string? error)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = error is null ? Scheme : $"{Scheme} error=\"{error}\"";

        await problemDetails.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status401Unauthorized,
                Title = "Unauthorized",
                Detail = detail
            }
        });
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rejected {Method} {Path}: {Reason} (traceId: {TraceId}).")]
    private static partial void LogRejected(ILogger logger, string method, string path, string reason, string traceId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Count} API client(s) ignored: missing name or token shorter than {MinLength} characters.")]
    private static partial void LogIgnoredClients(ILogger logger, int count, int minLength);

    [LoggerMessage(Level = LogLevel.Critical, Message = "No valid API tokens are configured. Every request will be rejected with 401.")]
    private static partial void LogNoClients(ILogger logger);
}
