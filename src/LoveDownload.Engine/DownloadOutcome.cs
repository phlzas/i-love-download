namespace LoveDownload.Engine;

public sealed record DownloadOutcome(
    bool Success,
    string? FileName,
    string? FilePath,
    long BytesDownloaded,
    long? TotalBytes,
    string? Error);
