using LoveDownload.Engine;

namespace LoveDownload.Api.Services;

/// <summary>
/// Accumulates per-chunk samples reported from the engine's concurrent chunk
/// tasks. Report is called from multiple threads, so all access is locked.
/// Persistence is done by the worker after the download returns.
/// </summary>
internal sealed class SegmentStatsCollector : IProgress<SegmentSample>
{
    private readonly object _gate = new();
    private readonly List<SegmentSample> _samples = new();

    public void Report(SegmentSample value)
    {
        lock (_gate) { _samples.Add(value); }
    }

    public IReadOnlyList<SegmentSample> Snapshot()
    {
        lock (_gate) { return _samples.ToArray(); }
    }
}
