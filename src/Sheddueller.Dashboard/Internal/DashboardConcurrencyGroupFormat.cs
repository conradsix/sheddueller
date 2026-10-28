namespace Sheddueller.Dashboard.Internal;

using System.Globalization;

using Sheddueller.Inspection.ConcurrencyGroups;

/// <summary>
/// How busy a concurrency group is, in order of severity.
/// </summary>
internal enum DashboardConcurrencyGroupState
{
    /// <summary>
    /// Less than 80% of the slots are in use and nothing is waiting.
    /// </summary>
    Nominal,

    /// <summary>
    /// At least 80% of the slots are in use.
    /// </summary>
    High,

    /// <summary>
    /// Every slot is in use; the next job waits for one to free up.
    /// </summary>
    Saturated,

    /// <summary>
    /// Due jobs are waiting for a slot or a rate permit.
    /// </summary>
    Blocked,
}

/// <summary>
/// Display text for concurrency-group limits, rates, and state.
/// </summary>
internal static class DashboardConcurrencyGroupFormat
{
    /// <summary>
    /// The limit that applies when a group has neither an override nor a code default.
    /// </summary>
    public const int BuiltInDefaultLimit = 1;

    private const double HighOccupancyRatio = 0.8;

    /// <summary>
    /// A start rate such as <c>10 per second</c> or <c>100 per 15 min</c>; <c>Unlimited</c> when there is none.
    /// </summary>
    public static string Rate(ConcurrencyGroupRateLimit? rateLimit)
      => rateLimit is null
        ? "Unlimited"
        : string.Concat(DashboardFormat.Count(rateLimit.PermitCount), " per ", Period(rateLimit.Period));

    /// <summary>
    /// A rate period in the largest whole unit: <c>second</c>, <c>30 s</c>, <c>15 min</c>, <c>2 h</c>, <c>500 ms</c>.
    /// </summary>
    public static string Period(TimeSpan period)
    {
        if (period.Ticks % TimeSpan.TicksPerHour == 0)
        {
            return Unit(period.Ticks / TimeSpan.TicksPerHour, "hour", "h");
        }

        if (period.Ticks % TimeSpan.TicksPerMinute == 0)
        {
            return Unit(period.Ticks / TimeSpan.TicksPerMinute, "minute", "min");
        }

        if (period.Ticks % TimeSpan.TicksPerSecond == 0)
        {
            return Unit(period.Ticks / TimeSpan.TicksPerSecond, "second", "s");
        }

        return period >= TimeSpan.FromSeconds(1)
          ? string.Create(CultureInfo.InvariantCulture, $"{period.TotalSeconds:#,0.#######} s")
          : string.Create(CultureInfo.InvariantCulture, $"{period.TotalMilliseconds:#,0.####} ms");
    }

    public static string LimitSource(ConcurrencyGroupInspectionSummary group)
      => group.OverrideLimit is not null
        ? "Override"
        : group.DefaultLimit is not null ? "Code default" : "Built-in default";

    public static string LimitSourceTitle(ConcurrencyGroupInspectionSummary group)
      => group.OverrideLimit is not null
        ? string.Concat("Set from the dashboard. Without it the limit is ", DefaultLimitText(group), ".")
        : group.DefaultLimit is not null
          ? "Set in code."
          : string.Create(CultureInfo.InvariantCulture, $"Nothing is set in code, so the built-in default of {BuiltInDefaultLimit} applies.");

    public static string RateSource(ConcurrencyGroupInspectionSummary group)
      => group.HasRateLimitOverride
        ? "Override"
        : group.DefaultRateLimit is not null ? "Code default" : "Built-in default";

    public static string RateSourceTitle(ConcurrencyGroupInspectionSummary group)
      => group.HasRateLimitOverride
        ? string.Concat("Set from the dashboard. Without it the rate is ", DefaultRateText(group), ".")
        : group.DefaultRateLimit is not null
          ? "Set in code."
          : "No rate limit is set in code, so starts are unlimited.";

    /// <summary>
    /// The limit the group falls back to when its override is removed, e.g. <c>the code default of 3</c>.
    /// </summary>
    public static string DefaultLimitText(ConcurrencyGroupInspectionSummary group)
      => group.DefaultLimit is { } defaultLimit
        ? string.Concat("the code default of ", DashboardFormat.Count(defaultLimit))
        : string.Create(CultureInfo.InvariantCulture, $"the built-in default of {BuiltInDefaultLimit}");

    /// <summary>
    /// The rate the group falls back to when its override is removed, e.g. <c>the code default of 10 per second</c>.
    /// </summary>
    public static string DefaultRateText(ConcurrencyGroupInspectionSummary group)
      => group.DefaultRateLimit is { } defaultRate
        ? string.Concat("the code default of ", Rate(defaultRate))
        : "unlimited, because no rate limit is set in code";

    public static DashboardConcurrencyGroupState State(ConcurrencyGroupInspectionSummary group)
    {
        if (group.BlockedJobCount > 0)
        {
            return DashboardConcurrencyGroupState.Blocked;
        }

        if (group.IsSaturated)
        {
            return DashboardConcurrencyGroupState.Saturated;
        }

        return OccupancyRatio(group) >= HighOccupancyRatio
          ? DashboardConcurrencyGroupState.High
          : DashboardConcurrencyGroupState.Nominal;
    }

    public static string StateTitle(ConcurrencyGroupInspectionSummary group)
      => State(group) switch
      {
          DashboardConcurrencyGroupState.Blocked => BlockedTitle(group),
          DashboardConcurrencyGroupState.Saturated => "Every slot is in use. The next job waits for one to free up.",
          _ => OccupancyTitle(group),
      };

    /// <summary>
    /// For example <c>3 of 5 slots in use</c>.
    /// </summary>
    public static string OccupancyTitle(ConcurrencyGroupInspectionSummary group)
      => string.Concat(
        DashboardFormat.Count(group.CurrentOccupancy),
        " of ",
        DashboardFormat.Count(group.EffectiveLimit),
        group.EffectiveLimit == 1 ? " slot in use" : " slots in use");

    /// <summary>
    /// Why due jobs are waiting, split by what they wait for when the store reports it.
    /// </summary>
    public static string BlockedTitle(ConcurrencyGroupInspectionSummary group)
    {
        if (group.BlockedJobCount == 0)
        {
            return "No due jobs are waiting.";
        }

        var title = string.Concat(Jobs(group.BlockedJobCount), group.BlockedJobCount == 1 ? " is waiting" : " are waiting");
        var reasons = new List<string>(2);
        if (group.ConcurrencyBlockedJobCount > 0)
        {
            reasons.Add(string.Concat(DashboardFormat.Count(group.ConcurrencyBlockedJobCount), " for a free slot"));
        }

        if (group.RateBlockedJobCount > 0)
        {
            reasons.Add(string.Concat(DashboardFormat.Count(group.RateBlockedJobCount), " for a rate permit"));
        }

        return reasons.Count == 0
          ? string.Concat(title, ".")
          : string.Concat(title, ": ", string.Join(", ", reasons), ".");
    }

    private static double OccupancyRatio(ConcurrencyGroupInspectionSummary group)
      => group.EffectiveLimit <= 0 ? 0 : (double)group.CurrentOccupancy / group.EffectiveLimit;

    private static string Jobs(int count)
      => string.Concat(DashboardFormat.Count(count), count == 1 ? " due job" : " due jobs");

    private static string Unit(long count, string singular, string abbreviation)
      => count == 1 ? singular : string.Concat(DashboardFormat.Count(count), " ", abbreviation);
}
