using LoveDownload.Api.Models;

namespace LoveDownload.Api.Services;

/// <summary>Creates access tokens (signed JWTs) and opaque refresh tokens.</summary>
public interface IJwtTokenService
{
    /// <summary>
    /// Creates a signed JWT for the given user.
    /// </summary>
    /// <param name="user">The authenticated user.</param>
    /// <param name="jti">The unique token id embedded as the <c>jti</c> claim (used for revocation).</param>
    /// <param name="expiresAt">The absolute expiry of the token.</param>
    /// <returns>The compact serialized JWT.</returns>
    string CreateAccessToken(AppUser user, out string jti, out DateTimeOffset expiresAt);

    /// <summary>Creates a cryptographically-random refresh token value.</summary>
    string CreateRefreshToken();
}
