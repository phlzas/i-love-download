using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using LoveDownload.Api.Models;
using LoveDownload.Api.Options;
using Microsoft.IdentityModel.Tokens;

namespace LoveDownload.Api.Services;

/// <summary>
/// Signs JWTs with HS256 using the secret from configuration, and mints random
/// refresh tokens. Signing/verification is delegated to the IdentityModel library —
/// we never hand-roll cryptography. Ported from BE-03.
/// </summary>
public sealed class JwtTokenService : IJwtTokenService
{
    private readonly JwtOptions _options;

    public JwtTokenService(JwtOptions options) => _options = options;

    public string CreateAccessToken(AppUser user, out string jti, out DateTimeOffset expiresAt)
    {
        jti = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        expiresAt = now.AddMinutes(_options.AccessTokenMinutes);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id),
            new(JwtRegisteredClaimNames.Jti, jti),
            new(JwtRegisteredClaimNames.Email, user.Email ?? string.Empty),
            // Duplicate into the ASP.NET claim shapes so [Authorize] and User.Identity work
            // regardless of inbound claim-type mapping.
            new(ClaimTypes.NameIdentifier, user.Id),
            new(ClaimTypes.Name, user.Email ?? string.Empty),
            new("uid", user.Id),
            new("email", user.Email ?? string.Empty),
        };

        if (!string.IsNullOrWhiteSpace(user.FullName))
        {
            claims.Add(new Claim("full_name", user.FullName!));
        }

        // Authorization role: "admin" only for emails listed in ADMIN_EMAILS.
        // Everyone else is a plain "user". Drives the 403 demo route.
        claims.Add(new Claim("role", IsAdmin(user.Email) ? "admin" : "user"));

        var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.Secret));
        var credentials = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: expiresAt.UtcDateTime,
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public string CreateRefreshToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(48);
        return Base64UrlEncoder.Encode(bytes);
    }

    private static bool IsAdmin(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return false;
        }

        var allowlist = Environment.GetEnvironmentVariable("ADMIN_EMAILS") ?? string.Empty;
        return allowlist
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(a => string.Equals(a, email, StringComparison.OrdinalIgnoreCase));
    }
}
