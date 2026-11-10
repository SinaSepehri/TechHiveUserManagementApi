using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace UserManagementAPI.Tests;

/// <summary>
/// Starts the real API in memory with known test tokens and a private log folder,
/// so tests can call it over HTTP and read the log file it writes.
/// </summary>
public sealed class TestApiFactory : WebApplicationFactory<Program>
{
    public const string Token = "test-token-0123456789abcdef";
    public const string ClientName = "test-client";
    public const string SecondToken = "second-token-fedcba9876543210";
    public const string SecondClientName = "reporting-service";

    private readonly bool configureClients;

    public TestApiFactory(bool configureClients = true, string environment = "Development")
    {
        this.configureClients = configureClients;
        Environment = environment;
        LogDirectory = Path.Combine(Path.GetTempPath(), "usermgmt-tests", Guid.NewGuid().ToString("N"));
    }

    public string Environment { get; }
    public string LogDirectory { get; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environment);
        builder.UseSetting("FileLogging:Path", Path.Combine(LogDirectory, "usermanagement-.log"));
        // Set here rather than relying on appsettings.Development.json, which is git-ignored.
        builder.UseSetting("Authentication:AnonymousPaths:0", "/openapi");

        if (configureClients)
        {
            // Overrides the local-dev client from appsettings.Development.json.
            builder.UseSetting("Authentication:Clients:0:Name", ClientName);
            builder.UseSetting("Authentication:Clients:0:Token", Token);
            builder.UseSetting("Authentication:Clients:1:Name", SecondClientName);
            builder.UseSetting("Authentication:Clients:1:Token", SecondToken);
        }
    }

    public HttpClient CreateAuthenticatedClient(string token = Token)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>
    /// Reads the log file, waiting until ALL <paramref name="expected"/> strings have been written
    /// (log lines from different middleware are written at slightly different times), or until a timeout.
    /// </summary>
    public async Task<string> ReadLogAsync(params string[] expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            var text = string.Concat(Directory.Exists(LogDirectory)
                ? Directory.GetFiles(LogDirectory, "usermanagement-*.log").Select(ReadShared)
                : []);

            if (expected.All(text.Contains) || DateTime.UtcNow > deadline) return text;
            await Task.Delay(50);
        }
    }

    // The API keeps the file open, so open it with FileShare.ReadWrite.
    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        TryDeleteLogs();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) TryDeleteLogs();
    }

    private void TryDeleteLogs()
    {
        try { if (Directory.Exists(LogDirectory)) Directory.Delete(LogDirectory, recursive: true); }
        catch (IOException) { /* best effort */ }
    }
}
