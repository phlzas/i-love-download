namespace LoveDownload.Engine;

/// <summary>
/// The outcome of probing a URL for range support, total length, and file name.
/// Cached via <see cref="IProbeCache"/> so a repeat download can skip the network probe.
/// </summary>
public readonly record struct ProbeResult(bool RangeSupported, long? Length, string? FileName);
