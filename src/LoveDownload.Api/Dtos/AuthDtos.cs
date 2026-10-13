using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace LoveDownload.Api.Dtos;

/// <summary>Payload for <c>POST /auth/signup</c>.</summary>
public sealed record SignUpRequest
{
    [Required(ErrorMessage = "email is required")]
    [EmailAddress(ErrorMessage = "email must be a valid email address")]
    public string Email { get; init; } = string.Empty;

    [Required(ErrorMessage = "password is required")]
    [MinLength(8, ErrorMessage = "password must be at least 8 characters")]
    public string Password { get; init; } = string.Empty;

    public string? FullName { get; init; }
}

/// <summary>Payload for <c>POST /auth/login</c>.</summary>
public sealed record LoginRequest
{
    [Required(ErrorMessage = "email is required")]
    [EmailAddress(ErrorMessage = "email must be a valid email address")]
    public string Email { get; init; } = string.Empty;

    [Required(ErrorMessage = "password is required")]
    public string Password { get; init; } = string.Empty;
}

/// <summary>Payload for <c>POST /auth/refresh</c>.</summary>
public sealed record RefreshRequest
{
    [Required(ErrorMessage = "refreshToken is required")]
    public string RefreshToken { get; init; } = string.Empty;
}

/// <summary>Public projection of a user.</summary>
public sealed record UserDto(string Id, string Email, string? FullName);

/// <summary>
/// Returned by sign-up, login and refresh. Token fields use snake_case
/// (<c>access_token</c>, <c>refresh_token</c>) to match the specification.
/// </summary>
public sealed record AuthResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("refresh_token")] string RefreshToken,
    [property: JsonPropertyName("token_type")] string TokenType,
    [property: JsonPropertyName("expires_in")] int ExpiresIn,
    [property: JsonPropertyName("user")] UserDto User);

/// <summary>Returned by <c>GET /protected/profile</c> (id, email and account-created date).</summary>
public sealed record ProfileResponse(
    string Id,
    string Email,
    string? FullName,
    DateTimeOffset CreatedAt,
    string TokenId,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt);

/// <summary>Returned by <c>GET /protected/dashboard</c>.</summary>
public sealed record DashboardResponse(
    string Message,
    string UserId,
    DateTimeOffset ServerTime);

/// <summary>Returned by <c>GET /protected/admin</c>.</summary>
public sealed record AdminResponse(
    string Message,
    string UserId,
    string Role);

/// <summary>Returned by <c>GET /public/info</c>.</summary>
public sealed record PublicInfoResponse(string Message);

/// <summary>Uniform error body: <c>{ "error": "..." }</c> for every 4xx response.</summary>
public sealed record ErrorResponse(string Error);
