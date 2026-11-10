using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using UserManagementAPI.Controllers;
using UserManagementAPI.Models;
using UserManagementAPI.Services;

namespace UserManagementAPI.Tests;

/// <summary>Controller logic in isolation (no HTTP pipeline).</summary>
public sealed class UsersControllerTests
{
    private static UsersController NewController(IUserStore store) =>
        new(store, NullLogger<UsersController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
            ProblemDetailsFactory = new TestProblemDetailsFactory()
        };

    [Fact]
    public void GetAll_returns_page_and_total_header()
    {
        var store = new InMemoryUserStore();
        store.Create(CreateRequest("a@techhive.example"));
        store.Create(CreateRequest("b@techhive.example"));
        var controller = NewController(store);

        var ok = Assert.IsType<OkObjectResult>(controller.GetAll(page: 1, pageSize: 1));

        Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<User>>(ok.Value));
        Assert.Equal("2", controller.Response.Headers[UsersController.TotalCountHeader]);
    }

    [Fact]
    public void Create_returns_created_at_user_endpoint()
    {
        var created = Assert.IsType<CreatedAtActionResult>(NewController(new InMemoryUserStore()).Create(CreateRequest()));
        Assert.Equal(nameof(UsersController.GetById), created.ActionName);
        Assert.IsType<User>(created.Value);
    }

    [Fact]
    public void Missing_user_returns_404_problem()
    {
        var controller = NewController(new InMemoryUserStore());

        AssertProblem(controller.GetById(99), StatusCodes.Status404NotFound);
        AssertProblem(controller.Update(99, UpdateRequest()), StatusCodes.Status404NotFound);
        AssertProblem(controller.Delete(99), StatusCodes.Status404NotFound);
    }

    [Fact]
    public void Duplicate_email_returns_409_problem()
    {
        var store = new InMemoryUserStore();
        store.Create(CreateRequest());

        AssertProblem(NewController(store).Create(CreateRequest()), StatusCodes.Status409Conflict);
    }

    [Fact]
    public void Unexpected_exceptions_bubble_up_to_the_error_middleware()
    {
        // The controller only handles expected errors; ErrorHandlingMiddleware turns this into a 500.
        Assert.Throws<InvalidOperationException>(() => NewController(new ThrowingUserStore()).GetById(1));
    }

    [Fact]
    public void Delete_returns_no_content_for_existing_user()
    {
        var store = new InMemoryUserStore();
        var user = store.Create(CreateRequest());
        Assert.IsType<NoContentResult>(NewController(store).Delete(user.Id));
    }

    private static ProblemDetails AssertProblem(IActionResult result, int status)
    {
        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(status, obj.StatusCode);
        return Assert.IsType<ProblemDetails>(obj.Value);
    }

    private static CreateUserRequest CreateRequest(string email = "avery@techhive.example") => new()
    {
        FirstName = "Avery", LastName = "Morgan", Email = email, Department = "Engineering"
    };

    private static UpdateUserRequest UpdateRequest() => new()
    {
        FirstName = "Avery", LastName = "Morgan", Email = "avery@techhive.example", Department = "Engineering"
    };

    private sealed class TestProblemDetailsFactory : Microsoft.AspNetCore.Mvc.Infrastructure.ProblemDetailsFactory
    {
        public override ProblemDetails CreateProblemDetails(HttpContext httpContext, int? statusCode = null, string? title = null,
            string? type = null, string? detail = null, string? instance = null) =>
            new() { Status = statusCode, Title = title, Type = type, Detail = detail, Instance = instance };

        public override ValidationProblemDetails CreateValidationProblemDetails(HttpContext httpContext,
            Microsoft.AspNetCore.Mvc.ModelBinding.ModelStateDictionary modelStateDictionary, int? statusCode = null,
            string? title = null, string? type = null, string? detail = null, string? instance = null) =>
            new(modelStateDictionary) { Status = statusCode, Title = title, Detail = detail };
    }
}

/// <summary>Simulates a failing data store (e.g. a database outage).</summary>
internal sealed class ThrowingUserStore : IUserStore
{
    private static Exception Boom() => new InvalidOperationException("Simulated database failure: connection refused.");
    public int Count => throw Boom();
    public (IReadOnlyList<User> Items, int Total) GetPage(int skip, int take) => throw Boom();
    public User? GetById(int id) => throw Boom();
    public User Create(UserRequest request) => throw Boom();
    public User? Update(int id, UserRequest request) => throw Boom();
    public bool Delete(int id) => throw Boom();
}
