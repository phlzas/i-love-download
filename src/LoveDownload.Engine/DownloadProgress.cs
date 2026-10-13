namespace LoveDownload.Engine;

public readonly record struct DownloadProgress(long BytesDownloaded, long? TotalBytes)
{
    public double? Fraction =>
        TotalBytes is > 0 ? (double)BytesDownloaded / TotalBytes.Value : null;
}
