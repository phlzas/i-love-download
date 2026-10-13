namespace LoveDownload.Api.Models;

public class ScheduleSetting
{
    public int Id { get; set; } = 1;
    public bool Enabled { get; set; }
    public int StartMinutes { get; set; }  // 0..1439 (minutes from local midnight)
    public int EndMinutes { get; set; }    // 0..1439
    public DateTimeOffset UpdatedAt { get; set; }
}
