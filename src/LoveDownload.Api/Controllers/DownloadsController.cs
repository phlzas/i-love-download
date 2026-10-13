using LoveDownload.Api.Data;
using LoveDownload.Api.Models;
using LoveDownload.Api.Reporting;
using LoveDownload.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LoveDownload.Api.Controllers;

[ApiController]
[Route("downloads")]
public sealed class DownloadsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly DownloadQueue _queue;
    private readonly JobControlRegistry _registry;
    private readonly ScheduleStore _scheduleStore;
    private readonly PdfReportService _reportService;

    public DownloadsController(AppDbContext db, DownloadQueue queue, JobControlRegistry registry, ScheduleStore scheduleStore, PdfReportService reportService)
    {
        _db = db;
        _queue = queue;
        _registry = registry;
        _scheduleStore = scheduleStore;
        _reportService = reportService;
    }

    public sealed record CreateDownloadRequest(string? Url);

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateDownloadRequest? body, CancellationToken ct)
    {
        if (!TryNormalizeUrl(body?.Url, out var normalizedUrl))
        {
            return BadRequest(new { error = "url must be a non-empty absolute http or https URL" });
        }

        var setting = await _scheduleStore.GetAsync(ct);
        var windowClosed = setting.Enabled &&
            !ScheduleWindow.IsOpen(setting.StartMinutes, setting.EndMinutes, DateTimeOffset.Now.TimeOfDay);

        var now = DateTimeOffset.UtcNow;
        var job = new DownloadJob
        {
            Id = Guid.NewGuid(),
            Url = normalizedUrl!,
            Status = windowClosed ? DownloadStatus.Scheduled : DownloadStatus.Queued,
            CreatedAt = now,
            UpdatedAt = now
        };

        _db.Jobs.Add(job);
        await _db.SaveChangesAsync(ct);

        if (job.Status == DownloadStatus.Queued)
        {
            await _queue.EnqueueAsync(job.Id, ct);
        }

        return CreatedAtAction(nameof(GetById), new { id = job.Id }, job);
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<DownloadJob>>> List(CancellationToken ct)
    {
        var jobs = await _db.Jobs
            .AsNoTracking()
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync(ct);
        return Ok(jobs);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
    {
        var job = await _db.Jobs.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (job is null)
        {
            return NotFound(new { error = $"download '{id}' was not found" });
        }

        return Ok(job);
    }

    [HttpGet("{id:guid}/stats")]
    public async Task<IActionResult> Stats(Guid id, CancellationToken ct)
    {
        var job = await _db.Jobs.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (job is null)
        {
            return NotFound(new { error = $"download '{id}' was not found" });
        }

        var segments = await _db.SegmentStats
            .AsNoTracking()
            .Where(x => x.JobId == id)
            .OrderBy(x => x.ChunkIndex)
            .ToListAsync(ct);

        var segmentViews = segments.Select(s => new
        {
            chunkIndex = s.ChunkIndex,
            start = s.Start,
            end = s.End,
            bytesWritten = s.BytesWritten,
            elapsedMs = s.ElapsedMs,
            interfaceName = s.InterfaceName,
            localAddress = s.LocalAddress,
            bytesPerSecond = s.ElapsedMs > 0 ? (double)s.BytesWritten / (s.ElapsedMs / 1000.0) : (double?)null
        }).ToList();

        var interfaces = segments
            .GroupBy(s => new { s.InterfaceName, s.LocalAddress })
            .Select(grp =>
            {
                var bytes = grp.Sum(x => x.BytesWritten);
                var elapsed = grp.Sum(x => x.ElapsedMs);
                return new
                {
                    interfaceName = grp.Key.InterfaceName,
                    localAddress = grp.Key.LocalAddress,
                    chunkCount = grp.Count(),
                    bytes,
                    elapsedMs = elapsed,
                    bytesPerSecond = elapsed > 0 ? (double)bytes / (elapsed / 1000.0) : (double?)null
                };
            })
            .OrderByDescending(x => x.bytes)
            .ToList();

        var totalBytes = segments.Sum(s => s.BytesWritten);
        var totalElapsed = segments.Sum(s => s.ElapsedMs);

        return Ok(new
        {
            jobId = id,
            totalBytes,
            completedBytes = job.BytesDownloaded,
            elapsedMs = totalElapsed,
            bytesPerSecond = totalElapsed > 0 ? (double)totalBytes / (totalElapsed / 1000.0) : (double?)null,
            startedAt = job.StartedAt,
            completedAt = job.CompletedAt,
            interfaces,
            segments = segmentViews
        });
    }

    [HttpGet("{id:guid}/report")]
    public async Task<IActionResult> Report(Guid id, CancellationToken ct)
    {
        var job = await _db.Jobs.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (job is null)
        {
            return NotFound(new { error = $"download '{id}' was not found" });
        }

        if (job.Status != DownloadStatus.Completed)
        {
            return Conflict(new { error = $"download '{id}' is not completed" });
        }

        var segments = await _db.SegmentStats
            .AsNoTracking()
            .Where(x => x.JobId == id)
            .OrderBy(x => x.ChunkIndex)
            .ToListAsync(ct);

        var pdf = _reportService.Build(job, segments);
        return File(pdf, "application/pdf", $"report-{id}.pdf");
    }

    [HttpPost("{id:guid}/pause")]
    public async Task<IActionResult> Pause(Guid id, CancellationToken ct)
    {
        var job = await _db.Jobs.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (job is null)
        {
            return NotFound(new { error = $"download '{id}' was not found" });
        }

        if (job.Status == DownloadStatus.Completed || job.Status == DownloadStatus.Failed)
        {
            return Conflict(new { error = "download cannot be paused in its current state" });
        }

        job.Status = DownloadStatus.Paused;
        job.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        _registry.TryCancel(id);

        return Ok(job);
    }

    [HttpPost("{id:guid}/resume")]
    public async Task<IActionResult> Resume(Guid id, CancellationToken ct)
    {
        var job = await _db.Jobs.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (job is null)
        {
            return NotFound(new { error = $"download '{id}' was not found" });
        }

        if (job.Status != DownloadStatus.Paused)
        {
            return Conflict(new { error = "download is not paused" });
        }

        job.Status = DownloadStatus.Queued;
        job.Error = null;
        job.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        await _queue.EnqueueAsync(id, ct);

        return Ok(job);
    }

    private static bool TryNormalizeUrl(string? raw, out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        normalized = uri.ToString();
        return true;
    }
}
