namespace Sheddueller.Scheduling;

using System.Numerics;

/// <summary>
/// A parsed five-field cron expression. Each field is a bitmask of the values it matches.
/// </summary>
internal sealed record CronFields(
    ulong Minutes,
    ulong Hours,
    ulong DaysOfMonth,
    ulong Months,
    ulong DaysOfWeek,
    bool DayOfMonthStarred,
    bool DayOfWeekStarred)
{
    // The Gregorian calendar, weekdays included, repeats every 400 years, so a search this long is exhaustive.
    private static readonly TimeSpan SearchWindow = TimeSpan.FromDays(146_097);

    public DateTime? FindNextAfter(DateTime afterUtc)
    {
        var candidate = new DateTime(afterUtc.Ticks - (afterUtc.Ticks % TimeSpan.TicksPerMinute), DateTimeKind.Utc).AddMinutes(1);
        var limit = candidate + SearchWindow;

        while (candidate < limit)
        {
            if (!Contains(this.Months, candidate.Month))
            {
                candidate = new DateTime(candidate.Year, candidate.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1);
                continue;
            }

            if (!this.MatchesDay(candidate))
            {
                candidate = candidate.Date.AddDays(1);
                continue;
            }

            var hour = NextSetBit(this.Hours, candidate.Hour);
            if (hour != candidate.Hour)
            {
                candidate = hour < 0 ? candidate.Date.AddDays(1) : candidate.Date.AddHours(hour);
                continue;
            }

            var minute = NextSetBit(this.Minutes, candidate.Minute);
            if (minute < 0)
            {
                candidate = candidate.Date.AddHours(hour + 1);
                continue;
            }

            return candidate.Date.AddHours(hour).AddMinutes(minute);
        }

        return null;
    }

    // Vixie cron: when either day field starts with '*' both must match, otherwise either may.
    private bool MatchesDay(DateTime date)
    {
        var dayOfMonthMatches = Contains(this.DaysOfMonth, date.Day);
        var dayOfWeekMatches = Contains(this.DaysOfWeek, (int)date.DayOfWeek);

        return this.DayOfMonthStarred || this.DayOfWeekStarred
          ? dayOfMonthMatches && dayOfWeekMatches
          : dayOfMonthMatches || dayOfWeekMatches;
    }

    private static bool Contains(ulong mask, int value)
      => (mask & (1UL << value)) != 0;

    private static int NextSetBit(ulong mask, int from)
    {
        var remaining = mask >> from;
        return remaining == 0 ? -1 : from + BitOperations.TrailingZeroCount(remaining);
    }
}
