using LoveDownload.Api.Data;
using LoveDownload.Api.Models;
using LoveDownload.Engine;

namespace LoveDownload.Api.Services;

/// <summary>
/// Stores byte progress on the tracked job and persists at most every ~500ms
/// or ~1MB. Report runs synchronously on the download flow, so the DbContext
/// is never touched concurrently.
/// </summary>
internal sealed class ThrottledProgress : IProgress<DownloadProgress>
{
    private const long ByteThreshold = 1024 * 1024;
    private static readonly TimeSpan TimeThreshold = TimeSpan.FromMilliseconds(500);

    private readonly AppDbContext _db;
    private readonly DownloadJob _job;

    private DateTimeOffset _lastPersistedAt = DateTimeOffset.MinValue;
    private long _lastPersistedBytes;

    public ThrottledProgress(AppDbContext db, DownloadJob job)
    {
        _db = db;
        _job = job;
    }

    public void Report(DownloadProgress value)
    {
        _job.BytesDownloaded = value.BytesDownloaded;
        if (value.TotalBytes.HasValue)
        {
            _job.TotalBytes = value.TotalBytes;
        }

        var now = DateTimeOffset.UtcNow;
        if (value.BytesDownloaded - _lastPersistedBytes >= ByteThreshold ||
            now - _lastPersistedAt >= TimeThreshold)
        {
            _job.UpdatedAt = now;
            _db.SaveChanges();
            _lastPersistedAt = now;
            _lastPersistedBytes = value.BytesDownloaded;
        }
    }
}
