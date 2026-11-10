using Microsoft.AspNetCore.Mvc;
using UserManagementAPI.Models;
using UserManagementAPI.Services;

namespace UserManagementAPI.Controllers;

/// <summary>
/// CRUD endpoints for users. Requires a bearer token (see TokenAuthenticationMiddleware).
/// Every error response uses the standard JSON error format (ProblemDetails with an "error" field):
/// 400 invalid input, 401 bad token, 404 unknown user, 409 duplicate email, 500 unexpected error.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public sealed class UsersController(IUserStore userStore, ILogger<UsersController> logger) : ControllerBase
{
    public const int MaxPageSize = 100;
    public const string TotalCountHeader = "X-Total-Count";

    /// <summary>Returns users ordered by ID, one page at a time. The total is in the X-Total-Count header.</summary>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<User>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public IActionResult GetAll([FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        if (page < 1)
        {
            ModelState.AddModelError(nameof(page), "page must be 1 or greater.");
        }
        if (pageSize is < 1 or > MaxPageSize)
        {
            ModelState.AddModelError(nameof(pageSize), $"pageSize must be between 1 and {MaxPageSize}.");
        }
        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        return Execute(() =>
        {
            // long math so a huge page number can't overflow int and turn negative.
            var skip = (long)(page - 1) * pageSize;
            var (items, total) = skip >= int.MaxValue
                ? ([], userStore.Count)
                : userStore.GetPage((int)skip, pageSize);

            Response.Headers[TotalCountHeader] = total.ToString();
            return Ok(items);
        });
    }

    [HttpGet("{id:int}")]
    [ProducesResponseType<User>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public IActionResult GetById(int id) => Execute(() =>
    {
        var user = userStore.GetById(id);
        return user is null ? UserNotFound(id) : Ok(user);
    });

    [HttpPost]
    [ProducesResponseType<User>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public IActionResult Create(CreateUserRequest request) => Execute(() =>
    {
        var user = userStore.Create(request);
        return CreatedAtAction(nameof(GetById), new { id = user.Id }, user);
    });

    [HttpPut("{id:int}")]
    [ProducesResponseType<User>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public IActionResult Update(int id, UpdateUserRequest request) => Execute(() =>
    {
        var user = userStore.Update(id, request);
        return user is null ? UserNotFound(id) : Ok(user);
    });

    [HttpDelete("{id:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public IActionResult Delete(int id) => Execute(() => userStore.Delete(id) ? NoContent() : UserNotFound(id));

    /// <summary>
    /// Maps expected business errors to HTTP responses (duplicate email -> 409).
    /// Unexpected exceptions are deliberately NOT caught here: they bubble up to
    /// <see cref="Middleware.ErrorHandlingMiddleware"/>, which logs them and returns the standard 500 error,
    /// so every endpoint handles failures the same way.
    /// </summary>
    private IActionResult Execute(Func<IActionResult> action)
    {
        try
        {
            return action();
        }
        catch (DuplicateEmailException ex)
        {
            logger.LogInformation("Rejected duplicate email for {Action}.", ControllerContext.ActionDescriptor?.ActionName);
            return Problem(ex.Message, statusCode: StatusCodes.Status409Conflict, title: "Email already in use");
        }
    }

    private ObjectResult UserNotFound(int id) =>
        Problem($"No user with ID {id} exists.", statusCode: StatusCodes.Status404NotFound, title: "User not found");
}
