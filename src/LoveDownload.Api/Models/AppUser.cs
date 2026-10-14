using Microsoft.AspNetCore.Identity;

namespace LoveDownload.Api.Models;

/// <summary>
/// Application user for the optional auth layer (concept 6, ported from BE-03).
/// Extends the framework's <see cref="IdentityUser"/>, which provides password
/// hashing (PBKDF2), normalization and security-stamp handling.
/// </summary>
public class AppUser : IdentityUser
{
    /// <summary>Optional display name supplied at sign-up.</summary>
    public string? FullName { get; set; }

    /// <summary>When the account was created (returned by the protected profile endpoint).</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
