using System.IdentityModel.Tokens.Jwt;
using LoveDownload.Api.Data;
using LoveDownload.Api.Dtos;
using LoveDownload.Api.Models;
using LoveDownload.Api.Options;
using LoveDownload.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace LoveDownload.Api.Controllers;

/// <summary>
/// Authentication endpoints: sign-up, login, logout and token refresh.
/// All errors use the shape <c>{ "error": "..." }</c> with explicit status codes.
/// Ported from BE-03 (concept 6).
/// </summary>
[ApiController]
[Route("auth")]
[Produces("application/json")]
public class AuthController : ControllerBase
{
    private readonly UserManager<AppUser> _userManager;
    private readonly IJwtTokenService _tokens;
    private readonly AuthDbContext _db;
    private readonly JwtOptions _jwtOptions;

    public AuthController(
        UserManager<AppUser> userManager,
        IJwtTokenService tokens,
        AuthDbContext db,
        JwtOptions jwtOptions)
    {
        _userManager = userManager;
        _tokens = tokens;
        _db = db;
        _jwtOptions = jwtOptions;
    }

    /// <summary>Registers a new user with email + password.</summary>
    /// <response code="201">User created.</response>
    /// <response code="400">Missing/invalid input, or email already registered.</response>
    [HttpPost("signup")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(UserDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SignUp([FromBody] SignUpRequest request)
    {
        var user = new AppUser
        {
            UserName = request.Email,
            Email = request.Email,
            FullName = request.FullName,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        // Identity hashes the password with PBKDF2 and enforces the configured policy.
        var result = await _userManager.CreateAsync(user, request.Password);
        if (!result.Succeeded)
        {
            var message = string.Join("; ", result.Errors.Select(e => e.Description));
            return BadRequest(new { error = message });
        }

        return Created($"/auth/users/{user.Id}", new UserDto(user.Id, user.Email!, user.FullName));
    }

    /// <summary>Authenticates a user and returns an access token + refresh token.</summary>
    /// <response code="200">Authenticated.</response>
    /// <response code="400">Missing or invalid input.</response>
    /// <response code="401">Invalid email or password.</response>
    /// <response code="429">Too many login attempts.</response>
    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting("login")]
    [ProducesResponseType(typeof(AuthResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        var user = await _userManager.FindByEmailAsync(request.Email);

        // Same generic response whether the user is unknown or the password is wrong,
        // so we never leak which emails are registered.
        if (user is null || !await _userManager.CheckPasswordAsync(user, request.Password))
        {
            return Unauthorized(new { error = "Invalid login credentials" });
        }

        var accessToken = _tokens.CreateAccessToken(user, out _, out var expiresAt);
        var refreshToken = _tokens.CreateRefreshToken();

        _db.RefreshTokens.Add(new RefreshToken
        {
            Token = refreshToken,
            UserId = user.Id,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(_jwtOptions.RefreshTokenDays),
            IsRevoked = false,
        });
        await _db.SaveChangesAsync();

        return Ok(BuildAuthResponse(user, accessToken, refreshToken, expiresAt));
    }

    /// <summary>Logs out the caller by revoking the current access token and refresh token.</summary>
    /// <response code="204">Logged out.</response>
    /// <response code="401">Missing or invalid token.</response>
    [HttpPost("logout")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Logout([FromBody] RefreshRequest? request)
    {
        var jti = User.FindFirst(JwtRegisteredClaimNames.Jti)?.Value
                  ?? User.FindFirst("jti")?.Value;

        if (!string.IsNullOrEmpty(jti))
        {
            // Deny-list the access token until it would naturally expire.
            var alreadyRevoked = await _db.RevokedAccessTokens.AnyAsync(t => t.Jti == jti);
            if (!alreadyRevoked)
            {
                _db.RevokedAccessTokens.Add(new RevokedAccessToken
                {
                    Jti = jti,
                    RevokedAt = DateTimeOffset.UtcNow,
                    ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(_jwtOptions.AccessTokenMinutes),
                });
            }
        }

        // Revoke the presented refresh token (if any) so it can't mint new sessions.
        if (!string.IsNullOrWhiteSpace(request?.RefreshToken))
        {
            var stored = await _db.RefreshTokens
                .FirstOrDefaultAsync(t => t.Token == request.RefreshToken);
            if (stored is not null)
            {
                stored.IsRevoked = true;
            }
        }

        await _db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>Exchanges a valid refresh token for a new access token (rotation).</summary>
    /// <response code="200">Refreshed.</response>
    /// <response code="400">Missing input.</response>
    /// <response code="401">Refresh token invalid, expired or revoked.</response>
    [HttpPost("refresh")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(AuthResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Refresh([FromBody] RefreshRequest request)
    {
        var stored = await _db.RefreshTokens
            .Include(t => t.User)
            .FirstOrDefaultAsync(t => t.Token == request.RefreshToken);

        if (stored is null || stored.IsRevoked || stored.ExpiresAt <= DateTimeOffset.UtcNow || stored.User is null)
        {
            return Unauthorized(new { error = "Refresh token is invalid, expired or revoked" });
        }

        // Rotate: retire the old refresh token, issue a brand new pair.
        stored.IsRevoked = true;

        var user = stored.User;
        var accessToken = _tokens.CreateAccessToken(user, out _, out var expiresAt);
        var newRefresh = _tokens.CreateRefreshToken();

        _db.RefreshTokens.Add(new RefreshToken
        {
            Token = newRefresh,
            UserId = user.Id,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(_jwtOptions.RefreshTokenDays),
            IsRevoked = false,
        });
        await _db.SaveChangesAsync();

        return Ok(BuildAuthResponse(user, accessToken, newRefresh, expiresAt));
    }

    private static AuthResponse BuildAuthResponse(
        AppUser user,
        string accessToken,
        string refreshToken,
        DateTimeOffset expiresAt)
    {
        return new AuthResponse(
            accessToken,
            refreshToken,
            "Bearer",
            (int)Math.Max(0, (expiresAt - DateTimeOffset.UtcNow).TotalSeconds),
            new UserDto(user.Id, user.Email!, user.FullName));
    }
}
