namespace LoveDownload.Api.Options;

/// <summary>
/// Strongly-typed JWT settings. Values are loaded from environment variables.
/// No secret ever lives in source control. Ported from BE-03.
/// </summary>
public sealed class JwtOptions
{
    /// <summary>Symmetric signing secret (HS256). Must be at least 32 chars.</summary>
    public string Secret { get; init; } = string.Empty;

    public string Issuer { get; init; } = "flyrank-auth";

    public string Audience { get; init; } = "flyrank-auth-clients";

    /// <summary>Lifetime of the short-lived access token, in minutes.</summary>
    public int AccessTokenMinutes { get; init; } = 15;

    /// <summary>Lifetime of the refresh token, in days.</summary>
    public int RefreshTokenDays { get; init; } = 7;

    /// <summary>
    /// Builds options from environment variables. Throws early (fail-fast) when
    /// <c>JWT_SECRET</c> is missing or too weak so the app never boots insecurely.
    /// Only ever called when <c>AUTH_ENABLED=true</c>, so the disabled app starts
    /// without any secret configured.
    /// </summary>
    public static JwtOptions FromEnvironment()
    {
        var secret = Environment.GetEnvironmentVariable("JWT_SECRET");
        if (string.IsNullOrWhiteSpace(secret) || secret.Length < 32)
        {
            throw new InvalidOperationException(
                "JWT_SECRET is missing or too short. Set a random value of at least 32 characters.");
        }

        return new JwtOptions
        {
            Secret = secret,
            Issuer = Environment.GetEnvironmentVariable("JWT_ISSUER") ?? "flyrank-auth",
            Audience = Environment.GetEnvironmentVariable("JWT_AUDIENCE") ?? "flyrank-auth-clients",
            AccessTokenMinutes = ReadInt("JWT_ACCESS_TOKEN_MINUTES", 15),
            RefreshTokenDays = ReadInt("JWT_REFRESH_TOKEN_DAYS", 7),
        };
    }

    private static int ReadInt(string key, int fallback)
        => int.TryParse(Environment.GetEnvironmentVariable(key), out var value) ? value : fallback;
}
