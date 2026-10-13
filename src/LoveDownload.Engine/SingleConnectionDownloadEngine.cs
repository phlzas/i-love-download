using System.Buffers;

namespace LoveDownload.Engine;

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
            var fileName = EngineFileName.Resolve(response, request);

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
            return new DownloadOutcome(true, fileName, filePath, written, totalBytes ?? written, null, 1, null);
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
}
