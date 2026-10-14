namespace LoveDownload.Engine;

/// <summary>
/// Caches URL probe results keyed by URL. The implementation owns the TTL policy
/// (it must treat entries older than its own threshold as absent). Callers treat
/// caching as best-effort: a cache failure must never fail a download.
/// </summary>
public interface IProbeCache
{
    Task<ProbeResult?> GetAsync(string url, CancellationToken ct = default);

    Task SetAsync(string url, ProbeResult result, CancellationToken ct = default);
}
