namespace LoveDownload.Api.Models;

/// <summary>
/// Deny-list entry for a logged-out access token. JWTs are stateless, so "logout"
/// means recording the token's <c>jti</c> until it would have expired naturally.
/// The JWT bearer pipeline rejects any token whose <c>jti</c> is present here.
/// </summary>
public class RevokedAccessToken
{
    /// <summary>The unique token identifier (<c>jti</c> claim).</summary>
    public string Jti { get; set; } = string.Empty;

    public DateTimeOffset RevokedAt { get; set; }

    /// <summary>When the original token would have expired (used for housekeeping).</summary>
    public DateTimeOffset ExpiresAt { get; set; }
}
