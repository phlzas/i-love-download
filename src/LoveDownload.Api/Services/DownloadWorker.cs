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
    private readonly ILogger<DownloadWorker> _logger;

    public DownloadWorker(
        DownloadQueue queue,
        IServiceScopeFactory scopeFactory,
        IDownloadEngine engine,
        ILogger<DownloadWorker> logger)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
        _engine = engine;
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
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var job = await db.Jobs.FirstOrDefaultAsync(x => x.Id == jobId, ct);
            if (job is null || job.Status == DownloadStatus.Completed)
            {
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

            var outcome = await _engine.DownloadAsync(request, new ThrottledProgress(db, job), ct);

            if (outcome.Success)
            {
                job.Status = DownloadStatus.Completed;
                job.FileName = outcome.FileName;
                job.FilePath = outcome.FilePath;
                job.BytesDownloaded = outcome.BytesDownloaded;
                job.TotalBytes = outcome.TotalBytes ?? outcome.BytesDownloaded;
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
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cancelled during shutdown; job stays Downloading for restart.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Download job {JobId} failed", jobId);
            await TryMarkFailedAsync(jobId, ex.Message);
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
