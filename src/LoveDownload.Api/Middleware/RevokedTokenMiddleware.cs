using System.IdentityModel.Tokens.Jwt;
using LoveDownload.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace LoveDownload.Api.Middleware;

/// <summary>
/// Reusable middleware that rejects access tokens whose <c>jti</c> has been revoked
/// (i.e. the user logged out). Runs after authentication and before authorization /
/// route handlers, so every protected route benefits without extra code. Ported from BE-03.
/// </summary>
public sealed class RevokedTokenMiddleware
{
    private readonly RequestDelegate _next;

    public RevokedTokenMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, AuthDbContext db)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            var jti = context.User.FindFirst(JwtRegisteredClaimNames.Jti)?.Value
                      ?? context.User.FindFirst("jti")?.Value;

            if (!string.IsNullOrEmpty(jti) &&
                await db.RevokedAccessTokens.AnyAsync(t => t.Jti == jti))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsJsonAsync(
                    new { error = "This token has been revoked. Please log in again." });
                return;
            }
        }

        await _next(context);
    }
}
