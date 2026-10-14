using LoveDownload.Api.Data;
using LoveDownload.Api.Models;
using LoveDownload.Engine;
using Microsoft.EntityFrameworkCore;

namespace LoveDownload.Api.Services;

/// <summary>
/// SQLite-backed <see cref="IProbeCache"/>. Owns the TTL policy: entries older than
/// 24 hours are treated as absent.
/// </summary>
public sealed class SqliteProbeCache : IProbeCache
{
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(24);
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SqliteProbeCache> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SqliteProbeCache(IServiceScopeFactory scopeFactory, ILogger<SqliteProbeCache> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<ProbeResult?> GetAsync(string url, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.ProbeCacheEntries.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Url == url, ct)
                .ConfigureAwait(false);

            if (row is null)
            {
                return null;
            }

            if (DateTimeOffset.UtcNow - row.FetchedAt >= Ttl)
            {
                _logger.LogInformation("ProbeCache EXPIRED {Url}", url);
                return null;
            }

            _logger.LogInformation("ProbeCache HIT {Url}", url);
            return new ProbeResult(row.RangeSupported, row.Length, row.FileName);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetAsync(string url, ProbeResult result, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.ProbeCacheEntries.FirstOrDefaultAsync(x => x.Url == url, ct).ConfigureAwait(false);

            if (row is null)
            {
                db.ProbeCacheEntries.Add(new ProbeCacheEntry
                {
                    Url = url,
                    RangeSupported = result.RangeSupported,
                    Length = result.Length,
                    FileName = result.FileName,
                    FetchedAt = DateTimeOffset.UtcNow
                });
            }
            else
            {
                row.RangeSupported = result.RangeSupported;
                row.Length = result.Length;
                row.FileName = result.FileName;
                row.FetchedAt = DateTimeOffset.UtcNow;
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            _logger.LogInformation("ProbeCache STORE {Url}", url);
        }
        finally
        {
            _gate.Release();
        }
    }
}
