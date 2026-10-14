namespace LoveDownload.Api.Models;

public class InterfaceSpeed
{
    public string InterfaceKey { get; set; } = string.Empty;
    public double BytesPerSecond { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
