using System.Buffers;
using System.Net.Http.Headers;
using System.Text;

namespace LoveDownload.Engine;

/// <summary>
/// Streams a single http/https resource over one connection. Chunking and
/// multi-interface transports can be layered on later behind IDownloadEngine.
/// </summary>
public sealed class SingleConnectionDownloadEngine : IDownloadEngine
{
    private const int BufferSize = 81920;

    private readonly HttpClient _http;

    public SingleConnectionDownloadEngine(HttpClient http) => _http = http;

    public async Task<DownloadOutcome> DownloadAsync(
        DownloadRequest request,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct)
    {
        try
        {
            using var response = await _http
                .GetAsync(request.Url, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength;
            var fileName = ResolveFileName(response, request);

            var jobDirectory = Path.Combine(request.DestinationRoot, request.JobId.ToString("D"));
            Directory.CreateDirectory(jobDirectory);
            var filePath = Path.Combine(jobDirectory, fileName);

            long written = 0;
            await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var destination = new FileStream(
                filePath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
            {
                var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
                try
                {
                    int read;
                    while ((read = await source.ReadAsync(buffer.AsMemory(0, BufferSize), ct).ConfigureAwait(false)) > 0)
                    {
                        await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                        written += read;
                        progress?.Report(new DownloadProgress(written, totalBytes));
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }

            progress?.Report(new DownloadProgress(written, totalBytes ?? written));
            return new DownloadOutcome(true, fileName, filePath, written, totalBytes ?? written, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new DownloadOutcome(false, null, null, 0, null, ex.Message);
        }
    }

    private static string ResolveFileName(HttpResponseMessage response, DownloadRequest request)
    {
        string? name = null;
        try
        {
            var disposition = response.Content.Headers.ContentDisposition;
            name = disposition?.FileNameStar ?? disposition?.FileName;
        }
        catch (FormatException)
        {
            // Malformed Content-Disposition: fall through to the URL path.
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
