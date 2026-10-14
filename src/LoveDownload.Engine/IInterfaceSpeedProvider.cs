namespace LoveDownload.Engine;

/// <summary>
/// Remembers how fast each network interface has been moving data and exposes a
/// smoothed weight per interface (EWMA with a 7-day half-life) so the selector can
/// prefer faster links. Persistence is the implementation's concern; the engine
/// only sees this abstraction.
/// </summary>
public interface IInterfaceSpeedProvider
{
    /// <summary>Records a completed segment: bytes transferred over a link in elapsed milliseconds.</summary>
    Task RecordSegmentAsync(string interfaceKey, long bytes, long elapsedMs, CancellationToken ct = default);

    /// <summary>Current weight (bytes/second estimate) for an interface; 0 when nothing is known.</summary>
    Task<double> GetWeightAsync(string interfaceKey, CancellationToken ct = default);
}
