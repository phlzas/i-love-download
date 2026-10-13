namespace LoveDownload.Api.Models;

public enum DownloadStatus
{
    Queued,
    Scheduled,
    Downloading,
    Paused,
    Completed,
    Failed
}
