using LoveDownload.Api.Llm;
using LoveDownload.Api.Models;
using LoveDownload.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace LoveDownload.Api.Controllers;

/// <summary>
/// Natural-language scheduling (concept 5). Add-only: <c>POST /schedule/parse</c> returns a
/// validated proposal WITHOUT touching the stored schedule; <c>POST /schedule/apply-nl</c>
/// validates then applies it through <see cref="ScheduleStore"/>. The existing PUT/GET
/// <c>/schedule</c> endpoints are untouched.
/// </summary>
[ApiController]
[Route("schedule")]
[Produces("application/json")]
public sealed class ScheduleNlController : ControllerBase
{
    private readonly ScheduleNlParser _parser;
    private readonly ScheduleStore _store;

    public ScheduleNlController(ScheduleNlParser parser, ScheduleStore store)
    {
        _parser = parser;
        _store = store;
    }

    public sealed record ParseRequest(string? Prompt);

    /// <summary>Parse a natural-language scheduling request into a proposal (read-only).</summary>
    [HttpPost("parse")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Parse([FromBody] ParseRequest? body, CancellationToken ct)
    {
        var outcome = await _parser.ParseAsync(body?.Prompt, ct);
        Response.Headers["X-Schedule-Parse-Source"] = outcome.Source;

        if (!outcome.Ok)
        {
            return StatusCode(outcome.StatusCode, new { error = outcome.Error });
        }

        var proposal = outcome.Proposal!;
        return Ok(new
        {
            enabled = proposal.Enabled,
            start = proposal.Start,
            end = proposal.End,
            explanation = proposal.Explanation
        });
    }

    /// <summary>Parse and then apply the proposal via ScheduleStore, returning the updated window.</summary>
    [HttpPost("apply-nl")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> ApplyNl([FromBody] ParseRequest? body, CancellationToken ct)
    {
        var outcome = await _parser.ParseAsync(body?.Prompt, ct);
        Response.Headers["X-Schedule-Parse-Source"] = outcome.Source;

        if (!outcome.Ok)
        {
            return StatusCode(outcome.StatusCode, new { error = outcome.Error });
        }

        var proposal = outcome.Proposal!;
        ScheduleStore.TryParseHhMm(proposal.Start, out var startMinutes);
        ScheduleStore.TryParseHhMm(proposal.End, out var endMinutes);

        var setting = await _store.SetAsync(proposal.Enabled, startMinutes, endMinutes, ct);
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

    private static string FormatHhMm(int minutes) => $"{minutes / 60:D2}:{minutes % 60:D2}";
}
