using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using LoveDownload.Api.Dtos;
using LoveDownload.Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace LoveDownload.Api.Controllers;

/// <summary>
/// Protected endpoints. Every route here requires a valid Bearer access token;
/// missing/invalid/expired/revoked tokens yield 401 via the JWT bearer middleware.
/// Ported from BE-03 (concept 6).
/// </summary>
[ApiController]
[Route("protected")]
[Produces("application/json")]
[Authorize]
public class ProtectedController : ControllerBase
{
    private readonly UserManager<AppUser> _userManager;

    public ProtectedController(UserManager<AppUser> userManager)
    {
        _userManager = userManager;
    }

    /// <summary>Returns profile information for the authenticated user.</summary>
    /// <response code="200">Authenticated; returns the caller's profile.</response>
    /// <response code="401">Missing, invalid, expired or revoked token.</response>
    [HttpGet("profile")]
    [ProducesResponseType(typeof(ProfileResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<ProfileResponse>> Profile()
    {
        var userId = CurrentUserId();
        var user = await _userManager.FindByIdAsync(userId);

        return Ok(new ProfileResponse(
            Id: userId,
            Email: user?.Email ?? CurrentEmail(),
            FullName: user?.FullName,
            CreatedAt: user?.CreatedAt ?? DateTimeOffset.UtcNow,
            TokenId: User.FindFirst(JwtRegisteredClaimNames.Jti)?.Value ?? string.Empty,
            IssuedAt: ReadUnixTime("iat"),
            ExpiresAt: ReadUnixTime("exp")));
    }

    /// <summary>Example second protected route used to prove the middleware is reusable.</summary>
    /// <response code="200">Authenticated; returns a personalised dashboard message.</response>
    /// <response code="401">Missing, invalid, expired or revoked token.</response>
    [HttpGet("dashboard")]
    [ProducesResponseType(typeof(DashboardResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public ActionResult<DashboardResponse> Dashboard()
    {
        return Ok(new DashboardResponse(
            Message: "This is a protected dashboard. Only authenticated users can see it.",
            UserId: CurrentUserId(),
            ServerTime: DateTimeOffset.UtcNow));
    }

    /// <summary>Admin-only route: authenticated users without the admin role get 403.</summary>
    /// <response code="200">Caller is an admin.</response>
    /// <response code="401">Missing, invalid, expired or revoked token.</response>
    /// <response code="403">Authenticated but not an admin.</response>
    [HttpGet("admin")]
    [Authorize(Policy = "AdminOnly")]
    [ProducesResponseType(typeof(AdminResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public ActionResult<AdminResponse> Admin()
    {
        return Ok(new AdminResponse(
            Message: "Welcome, admin. This area is restricted.",
            UserId: CurrentUserId(),
            Role: "admin"));
    }

    private string CurrentUserId() =>
        User.FindFirst(ClaimTypes.NameIdentifier)?.Value
        ?? User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
        ?? string.Empty;

    private string CurrentEmail() =>
        User.FindFirst("email")?.Value
        ?? User.FindFirst(ClaimTypes.Email)?.Value
        ?? string.Empty;

    private DateTimeOffset ReadUnixTime(string claimType)
    {
        var raw = User.FindFirst(claimType)?.Value;
        return long.TryParse(raw, out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : DateTimeOffset.UtcNow;
    }
}
