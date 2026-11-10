using UserManagementAPI.Models;

namespace UserManagementAPI.Services;

public interface IUserStore
{
    int Count { get; }

    /// <summary>Returns one page of users ordered by ID, plus the total number of users.</summary>
    (IReadOnlyList<User> Items, int Total) GetPage(int skip, int take);

    User? GetById(int id);

    /// <exception cref="DuplicateEmailException">Another user already has this email.</exception>
    User Create(UserRequest request);

    /// <returns>The updated user, or null if no user has this ID.</returns>
    /// <exception cref="DuplicateEmailException">Another user already has this email.</exception>
    User? Update(int id, UserRequest request);

    bool Delete(int id);
}

/// <summary>Thrown when an email address is already used by another user.</summary>
public sealed class DuplicateEmailException(string email)
    : Exception($"A user with the email '{email}' already exists.")
{
    public string Email { get; } = email;
}
