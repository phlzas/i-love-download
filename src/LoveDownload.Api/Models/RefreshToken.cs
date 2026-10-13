namespace LoveDownload.Api.Models;

/// <summary>
/// Opaque refresh token issued alongside the access token. Persisted in the separate
/// auth database so it can be rotated or revoked on logout.
/// </summary>
public class RefreshToken
{
    public int Id { get; set; }

    /// <summary>Cryptographically-random, URL-safe token value.</summary>
    public string Token { get; set; } = string.Empty;

    public string UserId { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public bool IsRevoked { get; set; }

    public AppUser? User { get; set; }
}
