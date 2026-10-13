using System.Text;

namespace LoveDownload.Engine;

internal static class EngineFileName
{
    public static string Resolve(HttpResponseMessage response, DownloadRequest request)
    {
        string? name = null;
        try
        {
            var disposition = response.Content.Headers.ContentDisposition;
            name = disposition?.FileNameStar ?? disposition?.FileName;
        }
        catch (FormatException)
        {
            name = null;
        }

        name = Sanitize(name);

        if (name is null && Uri.TryCreate(request.Url, UriKind.Absolute, out var uri))
        {
            var segment = uri.Segments.Length > 0 ? uri.Segments[^1] : null;
            name = Sanitize(segment is null ? null : Uri.UnescapeDataString(segment.Trim('/')));
        }

        return name ?? request.JobId.ToString("D");
    }

    private static string? Sanitize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var trimmed = raw.Trim().Trim('"');
        if (trimmed.Length == 0)
        {
            return null;
        }

        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(trimmed.Length);
        foreach (var c in trimmed)
        {
            builder.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        }

        var result = builder.ToString().Trim();
        return result.Length == 0 ? null : result;
    }
}
