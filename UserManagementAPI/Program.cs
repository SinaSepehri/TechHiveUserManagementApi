using Serilog;
using UserManagementAPI.Middleware;
using UserManagementAPI.Services;

var builder = WebApplication.CreateBuilder(args);

// ---- Logging: console + daily rolling log files ---------------------------------------------
// Files go to Logs/usermanagement-YYYYMMDD.log (path configurable via "FileLogging:Path"), and the
// last 30 days are kept. Levels: Information by default, Warning for ASP.NET Core internals; they can be
// changed in an optional "Serilog" section of appsettings.json.
builder.Services.AddSerilog((services, logger) => logger
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File(
        builder.Configuration["FileLogging:Path"] ?? Path.Combine(builder.Environment.ContentRootPath, "Logs", "usermanagement-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30,
        shared: true, // lets other processes (or tests) read the file while the API is running
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}"),
    // Each app instance owns its logger instead of replacing Serilog's global static one,
    // so two instances in one process (e.g. integration tests) never write to each other's files.
    preserveStaticLogger: true);

// ---- Services --------------------------------------------------------------------------------
builder.Services.AddControllers();
// Lower-case generated URLs, e.g. Location: /api/users/1 instead of /api/Users/1.
builder.Services.AddRouting(options => options.LowercaseUrls = true);
builder.Services.AddOpenApi();
builder.Services.AddSingleton<IUserStore, InMemoryUserStore>();
builder.Services.Configure<TokenAuthenticationOptions>(builder.Configuration.GetSection(TokenAuthenticationOptions.SectionName));

// Standard error format for EVERY error (400, 401, 404, 409, 500...): an RFC 7807 ProblemDetails
// body that always includes an "error" message and a "traceId" that matches the log entry.
builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = ctx =>
{
    var problem = ctx.ProblemDetails;
    problem.Extensions["error"] = problem.Status >= StatusCodes.Status500InternalServerError
        ? ErrorHandlingMiddleware.InternalServerError
        : problem.Detail ?? problem.Title;
    problem.Extensions["traceId"] = ctx.HttpContext.TraceIdentifier;
});

var app = builder.Build();

// ---- Middleware pipeline (order matters) -------------------------------------------------------
// 1. Error handling first: it wraps everything after it, so no exception can escape unhandled.
app.UseMiddleware<ErrorHandlingMiddleware>();
// Empty 404/405 responses (unknown routes, /api/users/abc) get the same JSON error body.
app.UseStatusCodePages();
// 2. Authentication next: rejects bad tokens early, before any logging or controller work is done.
app.UseMiddleware<TokenAuthenticationMiddleware>();
// 3. Logging last: records method, path, status and client for every authenticated request.
//    (Rejected requests are logged by the authentication middleware itself.)
app.UseMiddleware<RequestLoggingMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapControllers();
app.Run();

// Lets the integration tests start the app with WebApplicationFactory<Program>.
public partial class Program;
