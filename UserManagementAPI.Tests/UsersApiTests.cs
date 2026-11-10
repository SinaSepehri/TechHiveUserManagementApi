using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using UserManagementAPI.Models;
using UserManagementAPI.Services;

namespace UserManagementAPI.Tests;

/// <summary>
/// End-to-end tests: real HTTP requests through the full pipeline (routing, model validation,
/// exception handling). Each test gets a fresh app with an empty user store.
/// </summary>
public sealed class UsersApiTests : IDisposable
{
    private readonly TestApiFactory factory;
    private readonly HttpClient client;

    public UsersApiTests()
    {
        factory = new TestApiFactory();
        client = factory.CreateAuthenticatedClient();
    }

    private static HttpClient Authenticated(WebApplicationFactory<Program> app)
    {
        var c = app.CreateClient();
        c.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", TestApiFactory.Token);
        return c;
    }

    public void Dispose()
    {
        client.Dispose();
        factory.Dispose();
    }

    private static object ValidUser(string email = "avery@techhive.example") => new
    {
        firstName = "Avery", lastName = "Morgan", email, department = "Engineering"
    };

    private async Task<User> CreateUser(string email = "avery@techhive.example")
    {
        var response = await client.PostAsJsonAsync("/api/users", ValidUser(email));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<User>())!;
    }

    private static async Task<JsonElement> ReadProblem(HttpResponseMessage response, HttpStatusCode expected)
    {
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal((int)expected, json.GetProperty("status").GetInt32());
        return json;
    }

    // ---- Happy path -----------------------------------------------------------------------

    [Fact]
    public async Task Full_crud_flow_works()
    {
        var created = await client.PostAsJsonAsync("/api/users", ValidUser());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("/api/users/1", created.Headers.Location?.AbsolutePath);

        var user = await client.GetFromJsonAsync<User>("/api/users/1");
        Assert.Equal("Avery", user!.FirstName);

        var updated = await client.PutAsJsonAsync("/api/users/1",
            new { firstName = "Avery", lastName = "Morgan", email = "avery@techhive.example", department = "Platform" });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal("Platform", (await updated.Content.ReadFromJsonAsync<User>())!.Department);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/users/1")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/users/1")).StatusCode);
    }

    [Fact]
    public async Task Input_is_trimmed_and_email_lowercased()
    {
        var response = await client.PostAsJsonAsync("/api/users",
            new { firstName = "  Avery ", lastName = "Morgan ", email = " Avery@TechHive.EXAMPLE ", department = " Engineering" });

        var user = (await response.Content.ReadFromJsonAsync<User>())!;
        Assert.Equal(new User(1, "Avery", "Morgan", "avery@techhive.example", "Engineering"), user);
    }

    [Theory]
    [InlineData("José", "García-López")]
    [InlineData("Mary Jane", "O'Neil")]
    [InlineData("J.", "Smith")]
    public async Task Real_world_names_are_accepted(string first, string last)
    {
        var response = await client.PostAsJsonAsync("/api/users",
            new { firstName = first, lastName = last, email = "x@techhive.example", department = "Sales" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    // ---- Invalid input → 400 -------------------------------------------------------------

    [Theory]
    [InlineData("", "Morgan", "a@techhive.example", "Engineering", "FirstName")]
    [InlineData("   ", "Morgan", "a@techhive.example", "Engineering", "FirstName")]
    [InlineData("Avery", "", "a@techhive.example", "Engineering", "LastName")]
    [InlineData("Avery1", "Morgan", "a@techhive.example", "Engineering", "FirstName")]
    [InlineData("<script>", "Morgan", "a@techhive.example", "Engineering", "FirstName")]
    [InlineData("Avery", "Morgan", "", "Engineering", "Email")]
    [InlineData("Avery", "Morgan", "not-an-email", "Engineering", "Email")]
    [InlineData("Avery", "Morgan", "a@b", "Engineering", "Email")]
    [InlineData("Avery", "Morgan", "a b@techhive.example", "Engineering", "Email")]
    [InlineData("Avery", "Morgan", "a@@techhive.example", "Engineering", "Email")]
    [InlineData("Avery", "Morgan", "a@techhive.example", "", "Department")]
    [InlineData("Avery", "Morgan", "a@techhive.example", "X", "Department")]
    public async Task Invalid_fields_are_rejected(string first, string last, string email, string dept, string field)
    {
        var response = await client.PostAsJsonAsync("/api/users",
            new { firstName = first, lastName = last, email, department = dept });

        var problem = await ReadProblem(response, HttpStatusCode.BadRequest);
        Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _), $"Expected an error for {field}");
        Assert.Equal(0, factory.Services.GetRequiredService<IUserStore>().Count);
    }

    [Fact]
    public async Task Empty_name_gets_one_clear_message()
    {
        var response = await client.PostAsJsonAsync("/api/users",
            new { firstName = "", lastName = "Morgan", email = "a@techhive.example", department = "Engineering" });

        var messages = (await ReadProblem(response, HttpStatusCode.BadRequest)).GetProperty("errors").GetProperty("FirstName");
        Assert.Equal("First name is required.", Assert.Single(messages.EnumerateArray()).GetString());
    }

    [Fact]
    public async Task Empty_email_gets_one_clear_message()
    {
        var response = await client.PostAsJsonAsync("/api/users",
            new { firstName = "Avery", lastName = "Morgan", email = "", department = "Engineering" });

        var messages = (await ReadProblem(response, HttpStatusCode.BadRequest)).GetProperty("errors").GetProperty("Email");
        Assert.Equal("Email is required.", Assert.Single(messages.EnumerateArray()).GetString());
    }

    [Fact]
    public async Task Too_long_values_are_rejected()
    {
        var response = await client.PostAsJsonAsync("/api/users",
            new { firstName = new string('a', 51), lastName = "Morgan", email = "a@techhive.example", department = "Engineering" });
        await ReadProblem(response, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Missing_fields_are_all_reported()
    {
        var response = await client.PostAsJsonAsync("/api/users", new { });
        var errors = (await ReadProblem(response, HttpStatusCode.BadRequest)).GetProperty("errors");

        foreach (var field in new[] { "FirstName", "LastName", "Email", "Department" })
        {
            Assert.True(errors.TryGetProperty(field, out _), $"Expected an error for {field}");
        }
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("""{ "firstName": 123, "lastName": "Morgan", "email": "a@techhive.example", "department": "Eng" }""")]
    [InlineData("null")]
    [InlineData("")]
    public async Task Malformed_bodies_return_400_not_500(string body)
    {
        var response = await client.PostAsync("/api/users", new StringContent(body, Encoding.UTF8, "application/json"));
        await ReadProblem(response, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Invalid_update_is_rejected_and_user_unchanged()
    {
        await CreateUser();
        var response = await client.PutAsJsonAsync("/api/users/1", new { firstName = "", lastName = "Morgan", email = "bad", department = "Eng" });

        await ReadProblem(response, HttpStatusCode.BadRequest);
        Assert.Equal("Avery", (await client.GetFromJsonAsync<User>("/api/users/1"))!.FirstName);
    }

    // ---- Duplicates → 409 ----------------------------------------------------------------

    [Theory]
    [InlineData("avery@techhive.example")]
    [InlineData("AVERY@TechHive.example")]
    [InlineData("  avery@techhive.example ")]
    public async Task Duplicate_email_returns_409(string email)
    {
        await CreateUser();
        var response = await client.PostAsJsonAsync("/api/users", ValidUser(email));

        var problem = await ReadProblem(response, HttpStatusCode.Conflict);
        Assert.Contains("already exists", problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Updating_to_another_users_email_returns_409()
    {
        await CreateUser("first@techhive.example");
        await CreateUser("second@techhive.example");

        var response = await client.PutAsJsonAsync("/api/users/2", ValidUser("first@techhive.example"));
        await ReadProblem(response, HttpStatusCode.Conflict);
    }

    // ---- Non-existent users and invalid routes → 404 ---------------------------------------

    [Theory]
    [InlineData("/api/users/999")]
    [InlineData("/api/users/0")]
    [InlineData("/api/users/-1")]
    public async Task Get_unknown_user_returns_404_problem(string url)
    {
        var problem = await ReadProblem(await client.GetAsync(url), HttpStatusCode.NotFound);
        Assert.Equal("User not found", problem.GetProperty("title").GetString());
    }

    [Theory]
    [InlineData("/api/users/abc")]
    [InlineData("/api/users/99999999999")]   // larger than int
    [InlineData("/api/users/1.5")]
    [InlineData("/api/nothing-here")]
    public async Task Invalid_routes_return_404_with_problem_body(string url)
    {
        await ReadProblem(await client.GetAsync(url), HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Update_and_delete_unknown_user_return_404()
    {
        await ReadProblem(await client.PutAsJsonAsync("/api/users/42", ValidUser()), HttpStatusCode.NotFound);
        await ReadProblem(await client.DeleteAsync("/api/users/42"), HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Deleting_twice_returns_404_the_second_time()
    {
        await CreateUser();
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/users/1")).StatusCode);
        await ReadProblem(await client.DeleteAsync("/api/users/1"), HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Unsupported_method_returns_405_problem()
    {
        await ReadProblem(await client.PatchAsync("/api/users/1", JsonContent.Create(ValidUser())), HttpStatusCode.MethodNotAllowed);
    }

    // ---- GET /api/users paging -----------------------------------------------------------

    [Fact]
    public async Task List_is_paged_with_total_count_header()
    {
        for (var i = 1; i <= 7; i++) await CreateUser($"user{i}@techhive.example");

        var response = await client.GetAsync("/api/users?page=2&pageSize=3");
        var users = (await response.Content.ReadFromJsonAsync<List<User>>())!;

        Assert.Equal([4, 5, 6], users.Select(u => u.Id));
        Assert.Equal("7", response.Headers.GetValues("X-Total-Count").Single());
    }

    [Fact]
    public async Task Empty_list_returns_200_with_empty_array()
    {
        var response = await client.GetAsync("/api/users");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty((await response.Content.ReadFromJsonAsync<List<User>>())!);
        Assert.Equal("0", response.Headers.GetValues("X-Total-Count").Single());
    }

    [Theory]
    [InlineData("page=0")]
    [InlineData("page=-1")]
    [InlineData("pageSize=0")]
    [InlineData("pageSize=101")]
    [InlineData("page=abc")]
    public async Task Invalid_paging_returns_400(string query)
    {
        await ReadProblem(await client.GetAsync($"/api/users?{query}"), HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Huge_page_number_returns_empty_page_instead_of_overflowing()
    {
        await CreateUser();
        var response = await client.GetAsync($"/api/users?page={int.MaxValue}&pageSize=100");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty((await response.Content.ReadFromJsonAsync<List<User>>())!);
    }

    // ---- Unhandled exceptions never crash the API ------------------------------------------

    [Fact]
    public async Task Store_failure_returns_500_problem_without_internal_details()
    {
        using var failing = factory.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
            services.AddSingleton<IUserStore, ThrowingUserStore>()));
        using var failingClient = Authenticated(failing);

        foreach (var request in new Func<Task<HttpResponseMessage>>[]
                 {
                     () => failingClient.GetAsync("/api/users"),
                     () => failingClient.GetAsync("/api/users/1"),
                     () => failingClient.PostAsJsonAsync("/api/users", ValidUser()),
                     () => failingClient.DeleteAsync("/api/users/1")
                 })
        {
            var response = await request();
            var body = await response.Content.ReadAsStringAsync();
            await ReadProblem(response, HttpStatusCode.InternalServerError);
            Assert.DoesNotContain("Simulated database failure", body);   // no leaked internals
            Assert.DoesNotContain("   at ", body);                        // no stack trace
        }
    }

    [Fact]
    public async Task Exception_outside_controllers_is_handled_by_global_handler()
    {
        // A middleware that throws, to prove the global IExceptionHandler catches what controllers can't.
        using var failing = factory.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
            services.AddSingleton<IStartupFilter>(new ThrowingMiddlewareStartupFilter())));
        using var failingClient = Authenticated(failing);

        var response = await failingClient.GetAsync("/boom");
        var problem = await ReadProblem(response, HttpStatusCode.InternalServerError);

        Assert.Equal("Internal server error.", problem.GetProperty("error").GetString());
        Assert.True(problem.TryGetProperty("traceId", out _));

        // The app keeps working after the failure.
        Assert.Equal(HttpStatusCode.OK, (await failingClient.GetAsync("/api/users")).StatusCode);
    }

    private sealed class ThrowingMiddlewareStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            next(app);
            app.Use((HttpContext ctx, RequestDelegate nextDelegate) =>
                ctx.Request.Path == "/boom" ? throw new InvalidOperationException("Middleware failure") : nextDelegate(ctx));
        };
    }
}
