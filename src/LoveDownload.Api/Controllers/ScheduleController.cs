using LoveDownload.Api.Models;
using LoveDownload.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace LoveDownload.Api.Controllers;

[ApiController]
[Route("schedule")]
public sealed class ScheduleController : ControllerBase
{
    private readonly ScheduleStore _store;

    public ScheduleController(ScheduleStore store)
    {
        _store = store;
    }

    public sealed record UpdateScheduleRequest(bool Enabled, string? Start, string? End);

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var setting = await _store.GetAsync(ct);
        return Ok(Build(setting));
    }

    [HttpPut]
    public async Task<IActionResult> Put([FromBody] UpdateScheduleRequest? body, CancellationToken ct)
    {
        if (body is null ||
            !ScheduleStore.TryParseHhMm(body.Start, out var start) ||
            !ScheduleStore.TryParseHhMm(body.End, out var end))
        {
            return BadRequest(new { error = "start and end must be HH:mm (00:00-23:59)" });
        }

        var setting = await _store.SetAsync(body.Enabled, start, end, ct);
        return Ok(Build(setting));
    }

    private static object Build(ScheduleSetting setting)
    {
        var isOpen = !setting.Enabled ||
            ScheduleWindow.IsOpen(setting.StartMinutes, setting.EndMinutes, DateTimeOffset.Now.TimeOfDay);
        var nextOpen = ScheduleWindow.NextOpen(setting.StartMinutes, setting.EndMinutes, DateTimeOffset.Now);

        return new
        {
            enabled = setting.Enabled,
            start = FormatHhMm(setting.StartMinutes),
            end = FormatHhMm(setting.EndMinutes),
            isOpen,
            nextOpenAt = nextOpen,
            now = DateTimeOffset.Now
        };
    }

    private static string FormatHhMm(int minutes)
    {
        var hour = minutes / 60;
        var minute = minutes % 60;
        return $"{hour:D2}:{minute:D2}";
    }
}
