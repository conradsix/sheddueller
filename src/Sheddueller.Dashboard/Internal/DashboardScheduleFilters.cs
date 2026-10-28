namespace Sheddueller.Dashboard.Internal;

using Sheddueller.Inspection.Schedules;

/// <summary>
/// The schedules list filters: schedule key text and active or paused.
/// </summary>
internal sealed class DashboardScheduleFilters
{
    public const string ActiveStateFilter = "active";
    public const string PausedStateFilter = "paused";

    public string ScheduleKey { get; set; } = string.Empty;

    public string PauseState { get; set; } = string.Empty;

    public bool HasAppliedFilters
      => !string.IsNullOrWhiteSpace(this.ScheduleKey)
        || !string.IsNullOrWhiteSpace(this.PauseState);

    public bool CopyFrom(DashboardScheduleFilters other)
    {
        if (string.Equals(this.ScheduleKey, other.ScheduleKey, StringComparison.Ordinal)
            && string.Equals(this.PauseState, other.PauseState, StringComparison.Ordinal))
        {
            return false;
        }

        this.ScheduleKey = other.ScheduleKey;
        this.PauseState = other.PauseState;
        return true;
    }

    public ScheduleInspectionQuery ToQuery(
        int pageSize,
        string? continuationToken)
      => new(
        Normalize(this.ScheduleKey),
        ParsePauseState(this.PauseState),
        ServiceType: null,
        MethodName: null,
        Tag: null,
        pageSize,
        continuationToken);

    private static bool? ParsePauseState(string value)
      => value switch
      {
          PausedStateFilter => true,
          ActiveStateFilter => false,
          _ => null,
      };

    private static string? Normalize(string value)
      => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// The schedules list's query string, and links to it.
/// </summary>
internal static class DashboardScheduleFilterQuery
{
    public const string Path = "schedules";
    public const string KeyParameter = "key";
    public const string StateParameter = "state";

    public static DashboardScheduleFilters ParseUri(string uri)
    {
        var query = DashboardQueryString.ParseUri(uri);
        var state = DashboardQueryString.LastNonEmpty(query, StateParameter);
        return new DashboardScheduleFilters
        {
            ScheduleKey = DashboardQueryString.LastNonEmpty(query, KeyParameter) ?? string.Empty,
            PauseState = NormalizePauseState(state),
        };
    }

    public static string ToHref(DashboardScheduleFilters filters)
      => DashboardQueryString.Build(
        Path,
        [
            (StateParameter, filters.PauseState),
            (KeyParameter, filters.ScheduleKey),
        ]);

    /// <summary>
    /// The schedules list filtered to one schedule key.
    /// </summary>
    public static string ScheduleHref(string scheduleKey)
      => ToHref(new DashboardScheduleFilters { ScheduleKey = scheduleKey });

    private static string NormalizePauseState(string? value)
    {
        if (string.Equals(value, DashboardScheduleFilters.ActiveStateFilter, StringComparison.OrdinalIgnoreCase))
        {
            return DashboardScheduleFilters.ActiveStateFilter;
        }

        return string.Equals(value, DashboardScheduleFilters.PausedStateFilter, StringComparison.OrdinalIgnoreCase)
          ? DashboardScheduleFilters.PausedStateFilter
          : string.Empty;
    }
}
