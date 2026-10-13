using LoveDownload.Api.Data;
using LoveDownload.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace LoveDownload.Api.Services;

public sealed class DownloadScheduler : BackgroundService
{
    private readonly ScheduleStore _store;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DownloadQueue _queue;
    private readonly JobControlRegistry _registry;
    private readonly ILogger<DownloadScheduler> _logger;

    public DownloadScheduler(
        ScheduleStore store,
        IServiceScopeFactory scopeFactory,
        DownloadQueue queue,
        JobControlRegistry registry,
        ILogger<DownloadScheduler> logger)
    {
        _store = store;
        _scopeFactory = scopeFactory;
        _queue = queue;
        _registry = registry;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken))
                {
                    break;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Download scheduler tick failed");
            }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var setting = await _store.GetAsync(ct);

        // Disabled schedule means no gating: the scheduler must not touch any job.
        if (!setting.Enabled)
        {
            return;
        }

        var nowUtc = DateTimeOffset.UtcNow;
        var open = ScheduleWindow.IsOpen(setting.StartMinutes, setting.EndMinutes, DateTimeOffset.Now.TimeOfDay);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        if (!open)
        {
            var running = await db.Jobs
                .Where(x => x.Status == DownloadStatus.Downloading)
                .ToListAsync(ct);
            if (running.Count == 0)
            {
                return;
            }

            foreach (var job in running)
            {
                job.Status = DownloadStatus.Scheduled;
                job.UpdatedAt = nowUtc;
            }

            await db.SaveChangesAsync(ct);

            foreach (var job in running)
            {
                _registry.TryCancel(job.Id);
            }
        }
        else
        {
            var scheduled = await db.Jobs
                .Where(x => x.Status == DownloadStatus.Scheduled)
                .ToListAsync(ct);
            if (scheduled.Count == 0)
            {
                return;
            }

            foreach (var job in scheduled)
            {
                job.Status = DownloadStatus.Queued;
                job.UpdatedAt = nowUtc;
            }

            await db.SaveChangesAsync(ct);

            foreach (var job in scheduled)
            {
                await _queue.EnqueueAsync(job.Id, ct);
            }
        }
    }
}
