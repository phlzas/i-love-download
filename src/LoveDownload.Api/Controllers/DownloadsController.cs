using LoveDownload.Api.Data;
using LoveDownload.Api.Models;
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

    public DownloadsController(AppDbContext db, DownloadQueue queue)
    {
        _db = db;
        _queue = queue;
    }

    public sealed record CreateDownloadRequest(string? Url);

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateDownloadRequest? body, CancellationToken ct)
    {
        if (!TryNormalizeUrl(body?.Url, out var normalizedUrl))
        {
            return BadRequest(new { error = "url must be a non-empty absolute http or https URL" });
        }

        var now = DateTimeOffset.UtcNow;
        var job = new DownloadJob
        {
            Id = Guid.NewGuid(),
            Url = normalizedUrl!,
            Status = DownloadStatus.Queued,
            CreatedAt = now,
            UpdatedAt = now
        };

        _db.Jobs.Add(job);
        await _db.SaveChangesAsync(ct);
        await _queue.EnqueueAsync(job.Id, ct);

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
