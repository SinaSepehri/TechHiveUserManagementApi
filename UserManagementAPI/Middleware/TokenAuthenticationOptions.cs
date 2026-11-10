namespace UserManagementAPI.Middleware;

/// <summary>
/// Settings for <see cref="TokenAuthenticationMiddleware"/>, bound from the "Authentication" config section.
///
/// Don't put real tokens in appsettings.json. Supply them with environment variables, for example
/// <c>Authentication__Clients__0__Name=hr-portal</c> and <c>Authentication__Clients__0__Token=...</c>,
/// or with <c>dotnet user-secrets</c> during development.
/// </summary>
public sealed class TokenAuthenticationOptions
{
    public const string SectionName = "Authentication";

    /// <summary>API clients and their bearer tokens.</summary>
    public List<ApiClient> Clients { get; init; } = [];

    /// <summary>Path prefixes that don't need a token (e.g. the OpenAPI document in Development).</summary>
    public List<string> AnonymousPaths { get; init; } = [];

    /// <summary>Tokens shorter than this are ignored at startup, so weak tokens can't be configured by mistake.</summary>
    public int MinimumTokenLength { get; init; } = 16;
}

public sealed class ApiClient
{
    public string Name { get; init; } = string.Empty;
    public string Token { get; init; } = string.Empty;
}
