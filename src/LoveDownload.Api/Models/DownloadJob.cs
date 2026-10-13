namespace LoveDownload.Api.Models;

public class DownloadJob
{
    public Guid Id { get; set; }
    public string Url { get; set; } = string.Empty;
    public string? FileName { get; set; }
    public string? FilePath { get; set; }
    public DownloadStatus Status { get; set; } = DownloadStatus.Queued;
    public long BytesDownloaded { get; set; }
    public long? TotalBytes { get; set; }
    public int ConnectionsUsed { get; set; }
    public string? InterfacesUsed { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
