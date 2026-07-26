namespace GlamourTracker.Services.FashionReport;

/// <summary>Fashion Report week timing (UTC), matching common Gold Saucer reset rules.</summary>
internal static class FashionReportWeek
{
    /// <summary>Next Tuesday 08:00 UTC (theme / weekly reset).</summary>
    public static DateTime NextWeeklyResetUtc() => NextDayOfWeekUtc(DayOfWeek.Tuesday, 8);

    /// <summary>Next Friday 08:00 UTC (judging window / allowance reset).</summary>
    public static DateTime NextJudgingResetUtc() => NextWeeklyResetUtc().AddDays(3);

    /// <summary>Judging is open from Friday until Tuesday reset.</summary>
    public static bool IsJudgingOpen(DateTime utcNow)
    {
        var nextWeekly = NextWeeklyResetUtc();
        return utcNow > nextWeekly.AddDays(-4) && utcNow < nextWeekly;
    }

    private static DateTime NextDayOfWeekUtc(DayOfWeek weekday, int hourUtc)
    {
        var today = DateTime.UtcNow;
        if (today.Hour < hourUtc && today.DayOfWeek == weekday)
            return today.Date.AddHours(hourUtc);

        var next = today.AddDays(1);
        while (next.DayOfWeek != weekday)
            next = next.AddDays(1);
        return next.Date.AddHours(hourUtc);
    }
}
