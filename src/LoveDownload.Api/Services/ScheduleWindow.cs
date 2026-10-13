namespace LoveDownload.Api.Services;

public static class ScheduleWindow
{
    public static bool IsOpen(int startMinutes, int endMinutes, TimeSpan localTimeOfDay)
    {
        if (startMinutes == endMinutes) return true; // whole day
        var now = (int)localTimeOfDay.TotalMinutes;
        if (now < 0) now = 0;
        if (now > 1439) now = 1439;
        return startMinutes < endMinutes
            ? now >= startMinutes && now < endMinutes
            : now >= startMinutes || now < endMinutes;
    }

    // Next local DateTimeOffset strictly AFTER `now` at which the window opens, or null if open now / all-day.
    public static DateTimeOffset? NextOpen(int startMinutes, int endMinutes, DateTimeOffset now)
    {
        if (startMinutes == endMinutes) return null;
        if (IsOpen(startMinutes, endMinutes, now.TimeOfDay)) return null;
        // walk forward minute by minute (max 1440) to find the boundary
        var probe = now;
        for (var i = 1; i <= 1441; i++)
        {
            probe = now.AddMinutes(i);
            if (IsOpen(startMinutes, endMinutes, probe.TimeOfDay)) return probe;
        }
        return null;
    }
}
