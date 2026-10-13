using LoveDownload.Api.Data;
using LoveDownload.Api.Models;
using LoveDownload.Engine;
using Microsoft.EntityFrameworkCore;

namespace LoveDownload.Api.Services;

/// <summary>
/// SQLite-backed <see cref="IInterfaceSpeedProvider"/>. Keeps an EWMA of bytes/second
/// per interface with a 7-day half-life: each new sample is blended with the stored
/// estimate using a weight derived from the time elapsed since the last update.
/// </summary>
public sealed class SqliteInterfaceSpeedProvider : IInterfaceSpeedProvider
{
    private static readonly double HalfLifeSeconds = TimeSpan.FromDays(7).TotalSeconds;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SqliteInterfaceSpeedProvider(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public async Task RecordSegmentAsync(string interfaceKey, long bytes, long elapsedMs, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(interfaceKey) || bytes <= 0 || elapsedMs <= 0)
        {
            return;
        }

        var sample = (double)bytes / (elapsedMs / 1000.0);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.InterfaceSpeeds.FirstOrDefaultAsync(x => x.InterfaceKey == interfaceKey, ct).ConfigureAwait(false);

            var now = DateTimeOffset.UtcNow;
            if (row is null)
            {
                db.InterfaceSpeeds.Add(new InterfaceSpeed
                {
                    InterfaceKey = interfaceKey,
                    BytesPerSecond = sample,
                    UpdatedAt = now
                });
            }
            else
            {
                var elapsedSeconds = Math.Max(0.0, (now - row.UpdatedAt).TotalSeconds);
                var alpha = 1.0 - Math.Pow(0.5, elapsedSeconds / HalfLifeSeconds);
                row.BytesPerSecond = alpha * sample + (1.0 - alpha) * row.BytesPerSecond;
                row.UpdatedAt = now;
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<double> GetWeightAsync(string interfaceKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(interfaceKey))
        {
            return 0.0;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.InterfaceSpeeds.AsNoTracking()
                .FirstOrDefaultAsync(x => x.InterfaceKey == interfaceKey, ct)
                .ConfigureAwait(false);
            return row?.BytesPerSecond ?? 0.0;
        }
        finally
        {
            _gate.Release();
        }
    }
}
