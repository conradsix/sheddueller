namespace Sheddueller.Scheduling;

internal static class CronSchedule
{
    // Any instant works: an expression that matches nothing in one 400-year cycle never matches.
    private static readonly DateTime ReachabilityProbe = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static void Validate(string cronExpression)
    {
        ArgumentException.ThrowIfNullOrEmpty(cronExpression);

        if (CronParser.Parse(cronExpression).FindNextAfter(ReachabilityProbe) is null)
        {
            throw new FormatException($"Cron expression '{cronExpression}' never matches a date.");
        }
    }

    public static DateTimeOffset GetNextOccurrenceAfter(string cronExpression, DateTimeOffset afterUtc)
    {
        var next = CronParser.Parse(cronExpression).FindNextAfter(afterUtc.UtcDateTime)
          ?? throw new InvalidOperationException($"Cron expression '{cronExpression}' does not have a next occurrence.");

        return new DateTimeOffset(next);
    }
}
