namespace LoveDownload.Api.Models;

public class DownloadSegmentStat
{
    public int Id { get; set; }
    public Guid JobId { get; set; }
    public int ChunkIndex { get; set; }
    public string? InterfaceName { get; set; }
    public string? LocalAddress { get; set; }
    public long Start { get; set; }
    public long End { get; set; }
    public long BytesWritten { get; set; }
    public long ElapsedMs { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
