namespace LoveDownload.Api;

/// <summary>
/// Shared helper so the JWT bearer events and any other middleware write the same
/// <c>{ "error": "..." }</c> body for 4xx responses. Ported from BE-03.
/// </summary>
internal static class HttpErrorExtensions
{
    public static Task WriteErrorAsync(
        this HttpContext context,
        int statusCode,
        string message,
        CancellationToken cancellationToken = default)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsJsonAsync(new { error = message }, cancellationToken);
    }
}
