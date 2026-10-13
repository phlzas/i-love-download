using LoveDownload.Api.Data;
using LoveDownload.Api.Models;
using LoveDownload.Engine;
using Microsoft.EntityFrameworkCore;

namespace LoveDownload.Api.Services;

public sealed class DownloadWorker : BackgroundService
{
    private readonly DownloadQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDownloadEngine _engine;
    private readonly JobControlRegistry _registry;
    private readonly ScheduleStore _scheduleStore;
    private readonly ILogger<DownloadWorker> _logger;

    public DownloadWorker(
        DownloadQueue queue,
        IServiceScopeFactory scopeFactory,
        IDownloadEngine engine,
        JobControlRegistry registry,
        ScheduleStore scheduleStore,
        ILogger<DownloadWorker> logger)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
        _engine = engine;
        _registry = registry;
        _scheduleStore = scheduleStore;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var jobId in _queue.ReadAllAsync(stoppingToken))
            {
                await ProcessAsync(jobId, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host is shutting down.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Download worker stopped unexpectedly");
        }
    }

    private async Task ProcessAsync(Guid jobId, CancellationToken ct)
    {
        // Register before the status check so a pause arriving after it still cancels
        // this token rather than racing past the check.
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _registry.Register(jobId, linkedCts);

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var job = await db.Jobs.FirstOrDefaultAsync(x => x.Id == jobId, ct);
            if (job is null || job.Status == DownloadStatus.Completed || job.Status == DownloadStatus.Paused)
            {
                return;
            }

            var setting = await _scheduleStore.GetAsync(ct);
            if (setting.Enabled &&
                !ScheduleWindow.IsOpen(setting.StartMinutes, setting.EndMinutes, DateTimeOffset.Now.TimeOfDay))
            {
                job.Status = DownloadStatus.Scheduled;
                job.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
                return;
            }

            job.Status = DownloadStatus.Downloading;
            job.Error = null;
            job.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            var request = new DownloadRequest
            {
                JobId = job.Id,
                Url = job.Url,
                DestinationRoot = RepoPaths.DownloadsDirectory
            };

            var outcome = await _engine.DownloadAsync(request, new ThrottledProgress(db, job), linkedCts.Token);

            if (outcome.Success)
            {
                job.Status = DownloadStatus.Completed;
                job.FileName = outcome.FileName;
                job.FilePath = outcome.FilePath;
                job.BytesDownloaded = outcome.BytesDownloaded;
                job.TotalBytes = outcome.TotalBytes ?? outcome.BytesDownloaded;
                job.ConnectionsUsed = outcome.ConnectionsUsed;
                job.InterfacesUsed = outcome.InterfacesUsed;
                job.Error = null;
            }
            else
            {
                job.Status = DownloadStatus.Failed;
                job.Error = outcome.Error ?? "Download failed";
            }

            job.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        catch (OperationCanceledException) when (linkedCts.IsCancellationRequested)
        {
            // Pause persists Paused and shutdown leaves Downloading; both are the
            // intended terminal state for this run, so writing Failed here would corrupt it.
            var status = await ReadStatusAsync(jobId);
            _logger.LogDebug(
                "Download job {JobId} cancelled with persisted status {Status}",
                jobId,
                status?.ToString() ?? "unknown");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Download job {JobId} failed", jobId);
            await TryMarkFailedAsync(jobId, ex.Message);
        }
        finally
        {
            _registry.Unregister(jobId);
        }
    }

    private async Task<DownloadStatus?> ReadStatusAsync(Guid jobId)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return await db.Jobs
                .AsNoTracking()
                .Where(x => x.Id == jobId)
                .Select(x => (DownloadStatus?)x.Status)
                .FirstOrDefaultAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read status for job {JobId}", jobId);
            return null;
        }
    }

    private async Task TryMarkFailedAsync(Guid jobId, string error)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var job = await db.Jobs.FirstOrDefaultAsync(x => x.Id == jobId);
            if (job is not null)
            {
                job.Status = DownloadStatus.Failed;
                job.Error = error;
                job.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not persist failure for job {JobId}", jobId);
        }
    }
}
