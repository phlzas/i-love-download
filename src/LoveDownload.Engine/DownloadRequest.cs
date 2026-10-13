namespace LoveDownload.Engine;

public sealed class DownloadRequest
{
    public required Guid JobId { get; init; }
    public required string Url { get; init; }
    public required string DestinationRoot { get; init; }
}
