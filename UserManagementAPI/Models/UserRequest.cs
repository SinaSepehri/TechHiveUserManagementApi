using System.ComponentModel.DataAnnotations;

namespace UserManagementAPI.Models;

/// <summary>
/// Fields shared by create and update requests. [ApiController] validates these attributes
/// automatically and returns 400 with a ValidationProblemDetails body when they fail.
/// </summary>
public abstract class UserRequest
{
    // Letters (any language), spaces, apostrophes, hyphens and dots, e.g. "Mary-Jane", "O'Neil", "José".
    public const string NamePattern = @"^\s*\p{L}[\p{L}\p{M} '\.\-]*\s*$";

    // Stricter than [EmailAddress] (which accepts "a@b"): requires a domain with a dot and no spaces.
    public const string EmailPattern = @"^\s*[^@\s]+@[^@\s]+\.[^@\s]{2,}\s*$";

    [Required(ErrorMessage = "First name is required.")]
    [StringLength(50, ErrorMessage = "First name must be at most 50 characters.")]
    [RegularExpression(NamePattern, ErrorMessage = "First name may only contain letters, spaces, apostrophes, hyphens and dots.")]
    public string FirstName { get; init; } = string.Empty;

    [Required(ErrorMessage = "Last name is required.")]
    [StringLength(50, ErrorMessage = "Last name must be at most 50 characters.")]
    [RegularExpression(NamePattern, ErrorMessage = "Last name may only contain letters, spaces, apostrophes, hyphens and dots.")]
    public string LastName { get; init; } = string.Empty;

    [Required(ErrorMessage = "Email is required.")]
    [StringLength(254, ErrorMessage = "Email must be at most 254 characters.")]
    [RegularExpression(EmailPattern, ErrorMessage = "Email must be a valid email address.")]
    public string Email { get; init; } = string.Empty;

    [Required(ErrorMessage = "Department is required.")]
    [StringLength(100, MinimumLength = 2, ErrorMessage = "Department must be between 2 and 100 characters.")]
    public string Department { get; init; } = string.Empty;
}
