namespace UserManagementAPI.Models;

public sealed record User(int Id, string FirstName, string LastName, string Email, string Department);
