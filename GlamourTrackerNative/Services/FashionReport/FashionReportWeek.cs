namespace GlamourTracker.Services.FashionReport;

/// <summary>
/// Fashion Report week timing in UTC: themes reset Tuesday 08:00, judging runs from Friday 08:00
/// until that reset. All helpers take the current time so callers can't disagree about "now".
/// </summary>
internal static class FashionReportWeek
{
    private const int ResetHourUtc = 8;
    private const int JudgingDaysBeforeReset = 4;

    /// <summary>Next Tuesday 08:00 UTC — the theme change that ends the current judging window.</summary>
    public static DateTime NextWeeklyResetUtc(DateTime utcNow) =>
        NextDayOfWeekUtc(utcNow, DayOfWeek.Tuesday, ResetHourUtc);

    /// <summary>Most recent Tuesday 08:00 UTC. Data fetched before this belongs to last week.</summary>
    public static DateTime LastWeeklyResetUtc(DateTime utcNow) =>
        NextWeeklyResetUtc(utcNow).AddDays(-7);

    /// <summary>
    /// When a score recorded now stops being shown: the Friday that opens the next judging window.
    /// Deliberately not "the next Friday" — a score has to survive the closed Tuesday–Friday gap.
    /// </summary>
    public static DateTime ScoreExpiryUtc(DateTime utcNow) =>
        NextWeeklyResetUtc(utcNow).AddDays(3);

    /// <summary>Judging is open from Friday 08:00 UTC until the Tuesday reset.</summary>
    public static bool IsJudgingOpen(DateTime utcNow)
    {
        var reset = NextWeeklyResetUtc(utcNow);
        return utcNow >= reset.AddDays(-JudgingDaysBeforeReset) && utcNow < reset;
    }

    private static DateTime NextDayOfWeekUtc(DateTime utcNow, DayOfWeek weekday, int hourUtc)
    {
        if (utcNow.DayOfWeek == weekday && utcNow.Hour < hourUtc)
            return utcNow.Date.AddHours(hourUtc);

        var next = utcNow.AddDays(1);
        while (next.DayOfWeek != weekday)
            next = next.AddDays(1);

        return next.Date.AddHours(hourUtc);
    }
}
