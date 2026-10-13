using LoveDownload.Api.Data;
using LoveDownload.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace LoveDownload.Api.Services;

public sealed class ScheduleStore
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;

    public ScheduleStore(IServiceScopeFactory scopeFactory, IConfiguration configuration)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
    }

    public async Task<ScheduleSetting> GetAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var setting = await db.ScheduleSettings.FirstOrDefaultAsync(x => x.Id == 1, ct);
        if (setting is not null)
        {
            return setting;
        }

        setting = new ScheduleSetting
        {
            Id = 1,
            Enabled = _configuration.GetValue("Schedule:Enabled", false),
            StartMinutes = ReadMinutes("Schedule:Start", "Schedule:StartMinutes", 60),
            EndMinutes = ReadMinutes("Schedule:End", "Schedule:EndMinutes", 300),
            UpdatedAt = DateTimeOffset.UtcNow
        };

        db.ScheduleSettings.Add(setting);
        await db.SaveChangesAsync(ct);
        return setting;
    }

    public async Task<ScheduleSetting> SetAsync(bool enabled, int startMinutes, int endMinutes, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var setting = await db.ScheduleSettings.FirstOrDefaultAsync(x => x.Id == 1, ct);
        if (setting is null)
        {
            setting = new ScheduleSetting { Id = 1 };
            db.ScheduleSettings.Add(setting);
        }

        setting.Enabled = enabled;
        setting.StartMinutes = startMinutes;
        setting.EndMinutes = endMinutes;
        setting.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return setting;
    }

    public static bool TryParseHhMm(string? s, out int minutes)
    {
        minutes = 0;
        if (string.IsNullOrWhiteSpace(s))
        {
            return false;
        }

        var parts = s.Trim().Split(':');
        if (parts.Length != 2)
        {
            return false;
        }

        if (!int.TryParse(parts[0].Trim(), out var hour) || !int.TryParse(parts[1].Trim(), out var minute))
        {
            return false;
        }

        if (hour < 0 || hour > 23 || minute < 0 || minute > 59)
        {
            return false;
        }

        minutes = (hour * 60) + minute;
        return true;
    }

    private int ReadMinutes(string timeKey, string minutesKey, int fallback)
    {
        if (TryParseHhMm(_configuration[timeKey], out var fromTime))
        {
            return fromTime;
        }

        var raw = _configuration[minutesKey];
        if (int.TryParse(raw, out var fromMinutes) && fromMinutes >= 0 && fromMinutes <= 1439)
        {
            return fromMinutes;
        }

        return fallback;
    }
}
