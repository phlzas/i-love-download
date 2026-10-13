namespace LoveDownload.Engine;

public interface IDownloadEngine
{
    Task<DownloadOutcome> DownloadAsync(
        DownloadRequest request,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct);
}
