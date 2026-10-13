namespace LoveDownload.Api.Models;

public class ProbeCacheEntry
{
    public string Url { get; set; } = string.Empty;
    public bool RangeSupported { get; set; }
    public long? Length { get; set; }
    public string? FileName { get; set; }
    public DateTimeOffset FetchedAt { get; set; }
}
