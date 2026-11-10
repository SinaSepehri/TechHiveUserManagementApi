using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using UserManagementAPI.Services;

namespace UserManagementAPI.Tests;

/// <summary>
/// Tests for the three middleware components and their order in the pipeline:
/// error handling -> authentication -> logging.
/// </summary>
public sealed class MiddlewareTests : IAsyncLifetime
{
    private readonly TestApiFactory factory = new();

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() => await factory.DisposeAsync();

    private static object ValidUser(string email = "avery@techhive.example") =>
        new { firstName = "Avery", lastName = "Morgan", email, department = "Engineering" };

    private static async Task<JsonElement> ReadError(HttpResponseMessage response, HttpStatusCode expected)
    {
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrWhiteSpace(json.GetProperty("error").GetString()), "Every error must have an 'error' message.");
        Assert.True(json.TryGetProperty("traceId", out _), "Every error must have a traceId.");
        return json;
    }

    private HttpClient ClientWithAuthorization(string? headerValue)
    {
        var client = factory.CreateClient();
        if (headerValue is not null) client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", headerValue);
        return client;
    }

    // ===== Authentication =========================================================================

    [Fact]
    public async Task Valid_token_is_allowed()
    {
        using var client = factory.CreateAuthenticatedClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/users")).StatusCode);
    }

    [Fact]
    public async Task Every_configured_client_token_works()
    {
        using var client = factory.CreateAuthenticatedClient(TestApiFactory.SecondToken);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/users")).StatusCode);
    }

    [Fact]
    public async Task Bearer_scheme_is_case_insensitive()
    {
        using var client = ClientWithAuthorization($"bearer {TestApiFactory.Token}");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/users")).StatusCode);
    }

    [Fact]
    public async Task Missing_token_returns_401_with_challenge()
    {
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/api/users");

        var error = await ReadError(response, HttpStatusCode.Unauthorized);
        Assert.Equal("Bearer", response.Headers.WwwAuthenticate.ToString());
        Assert.Contains("bearer token is required", error.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Bearer wrong-token-0123456789abcdef")]
    [InlineData("Bearer TEST-TOKEN-0123456789ABCDEF")]          // tokens are case-sensitive
    [InlineData("Bearer test-token-0123456789abcdef-extra")]
    public async Task Invalid_token_returns_401_invalid_token(string header)
    {
        using var client = ClientWithAuthorization(header);
        var response = await client.GetAsync("/api/users");

        await ReadError(response, HttpStatusCode.Unauthorized);
        Assert.Equal("Bearer error=\"invalid_token\"", response.Headers.WwwAuthenticate.ToString());
    }

    [Theory]
    [InlineData("Bearer")]
    [InlineData("Bearer    ")]
    [InlineData("Basic dXNlcjpwYXNz")]
    [InlineData("test-token-0123456789abcdef")]                 // token without the "Bearer" scheme
    public async Task Malformed_authorization_header_returns_401(string header)
    {
        using var client = ClientWithAuthorization(header);
        await ReadError(await client.GetAsync("/api/users"), HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Every_endpoint_is_protected_and_nothing_runs_without_a_token()
    {
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/users/1")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/users", ValidUser())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PutAsJsonAsync("/api/users/1", ValidUser())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.DeleteAsync("/api/users/1")).StatusCode);

        Assert.Equal(0, factory.Services.GetRequiredService<IUserStore>().Count); // the POST never reached the controller
    }

    [Fact]
    public async Task Unknown_routes_also_require_a_token()
    {
        // Returning 401 (not 404) means anonymous callers can't probe which routes exist.
        using var client = factory.CreateClient();
        await ReadError(await client.GetAsync("/api/does-not-exist"), HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Anonymous_paths_do_not_need_a_token()
    {
        // appsettings.Development.json allows /openapi without a token.
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/openapi/v1.json")).StatusCode);
    }

    [Fact]
    public async Task With_no_tokens_configured_every_request_is_rejected()
    {
        await using var locked = new TestApiFactory(configureClients: false, environment: "Production");
        using var client = locked.CreateAuthenticatedClient("anything-at-all-0123456789");

        await ReadError(await client.GetAsync("/api/users"), HttpStatusCode.Unauthorized);
        Assert.Contains("No valid API tokens are configured", await locked.ReadLogAsync("No valid API tokens"));
    }

    [Fact]
    public async Task Tokens_shorter_than_the_minimum_are_ignored()
    {
        await using var weak = new TestApiFactory(configureClients: false, environment: "Production");
        await using var configured = weak.WithWebHostBuilder(b =>
        {
            b.UseSetting("Authentication:Clients:0:Name", "weak");
            b.UseSetting("Authentication:Clients:0:Token", "short");
        });
        using var client = configured.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "short");

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/users")).StatusCode);
    }

    // ===== Error handling =========================================================================

    [Fact]
    public async Task Unhandled_exception_returns_standard_json_500()
    {
        await using var failing = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.AddSingleton<IUserStore, ThrowingUserStore>()));
        using var client = failing.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestApiFactory.Token);

        var response = await client.GetAsync("/api/users/1");
        var body = await response.Content.ReadAsStringAsync();
        var error = await ReadError(response, HttpStatusCode.InternalServerError);

        Assert.Equal("Internal server error.", error.GetProperty("error").GetString());
        Assert.DoesNotContain("Simulated database failure", body); // internals are logged, not returned
        Assert.DoesNotContain("   at ", body);                     // no stack trace
    }

    [Fact]
    public async Task Authentication_runs_before_the_endpoint_so_bad_tokens_never_reach_failing_code()
    {
        await using var failing = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.AddSingleton<IUserStore, ThrowingUserStore>()));
        using var client = failing.CreateClient();

        // The endpoint would throw, but without a token the request is stopped first: 401, not 500.
        await ReadError(await client.GetAsync("/api/users/1"), HttpStatusCode.Unauthorized);
    }

    public static TheoryData<string, HttpStatusCode> ErrorCases => new()
    {
        { "invalid-input", HttpStatusCode.BadRequest },
        { "no-token", HttpStatusCode.Unauthorized },
        { "missing-user", HttpStatusCode.NotFound },
        { "bad-route", HttpStatusCode.NotFound },
        { "wrong-method", HttpStatusCode.MethodNotAllowed },
        { "duplicate", HttpStatusCode.Conflict },
    };

    [Theory]
    [MemberData(nameof(ErrorCases))]
    public async Task All_errors_use_the_same_json_format(string scenario, HttpStatusCode expected)
    {
        using var client = factory.CreateAuthenticatedClient();
        await client.PostAsJsonAsync("/api/users", ValidUser());

        var response = scenario switch
        {
            "invalid-input" => await client.PostAsJsonAsync("/api/users", new { firstName = "" }),
            "no-token" => await factory.CreateClient().GetAsync("/api/users"),
            "missing-user" => await client.GetAsync("/api/users/999"),
            "bad-route" => await client.GetAsync("/api/users/abc"),
            "wrong-method" => await client.PatchAsync("/api/users/1", JsonContent.Create(ValidUser())),
            "duplicate" => await client.PostAsJsonAsync("/api/users", ValidUser()),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };

        var error = await ReadError(response, expected);
        Assert.Equal((int)expected, error.GetProperty("status").GetInt32());
    }

    // ===== Logging ================================================================================

    [Fact]
    public async Task Requests_are_logged_to_file_with_method_path_status_and_client()
    {
        using var client = factory.CreateAuthenticatedClient();
        await client.PostAsJsonAsync("/api/users", ValidUser());
        await client.GetAsync("/api/users?page=1&pageSize=10");
        await client.GetAsync("/api/users/999");
        await client.DeleteAsync("/api/users/1");

        var log = await factory.ReadLogAsync("POST /api/users responded 201", "GET /api/users responded 200",
            "GET /api/users/999 responded 404", "DELETE /api/users/1 responded 204");

        Assert.Contains("[INF] UserManagementAPI.Middleware.RequestLoggingMiddleware: HTTP POST /api/users responded 201", log);
        Assert.Contains("HTTP GET /api/users responded 200", log);          // query string is not logged
        Assert.Contains("[WRN] UserManagementAPI.Middleware.RequestLoggingMiddleware: HTTP GET /api/users/999 responded 404", log);
        Assert.Contains("HTTP DELETE /api/users/1 responded 204", log);
        Assert.Contains($"client: {TestApiFactory.ClientName}", log);
        Assert.DoesNotContain("pageSize", log);
    }

    [Fact]
    public async Task Log_trace_id_matches_the_error_response()
    {
        using var client = factory.CreateAuthenticatedClient();
        var error = await ReadError(await client.GetAsync("/api/users/12345"), HttpStatusCode.NotFound);
        var traceId = error.GetProperty("traceId").GetString()!;

        var log = await factory.ReadLogAsync($"traceId: {traceId}");
        Assert.Contains($"GET /api/users/12345 responded 404", log);
        Assert.Contains($"traceId: {traceId}", log);
    }

    [Fact]
    public async Task Rejected_requests_are_logged_without_the_token()
    {
        using var client = ClientWithAuthorization("Bearer leaked-secret-0123456789abcdef");
        await client.GetAsync("/api/users");
        using var noToken = factory.CreateClient();
        await noToken.DeleteAsync("/api/users/7");

        var log = await factory.ReadLogAsync("Rejected GET /api/users", "Rejected DELETE /api/users/7");

        Assert.Contains("Rejected GET /api/users: invalid token (traceId: ", log);
        Assert.Contains("Rejected DELETE /api/users/7: missing or malformed Authorization header (traceId: ", log);
        Assert.DoesNotContain("leaked-secret", log);
        Assert.DoesNotContain(TestApiFactory.Token, log);
    }

    [Fact]
    public async Task Exceptions_are_logged_with_details_and_status_500()
    {
        await using var failing = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.AddSingleton<IUserStore, ThrowingUserStore>()));
        using var client = failing.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestApiFactory.Token);

        await client.PostAsJsonAsync("/api/users", ValidUser());
        var log = await factory.ReadLogAsync("POST /api/users responded 500", "Unhandled exception for POST /api/users");

        Assert.Contains("[ERR] UserManagementAPI.Middleware.ErrorHandlingMiddleware: Unhandled exception for POST /api/users; returning 500.", log);
        Assert.Contains("Simulated database failure", log);   // the details are in the log for investigation
        Assert.Contains("[ERR] UserManagementAPI.Middleware.RequestLoggingMiddleware: HTTP POST /api/users responded 500", log);
    }

    [Fact]
    public async Task Api_keeps_working_after_an_error()
    {
        await using var failing = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.AddSingleton<IUserStore, ThrowingUserStore>()));
        using var failingClient = failing.CreateClient();
        failingClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestApiFactory.Token);
        Assert.Equal(HttpStatusCode.InternalServerError, (await failingClient.GetAsync("/api/users")).StatusCode);

        using var client = factory.CreateAuthenticatedClient();
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/users", ValidUser())).StatusCode);
    }
}
