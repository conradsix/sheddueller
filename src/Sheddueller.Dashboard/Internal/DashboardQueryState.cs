namespace Sheddueller.Dashboard.Internal;

using System.Globalization;

using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;

using Sheddueller.Inspection.Nodes;

/// <summary>
/// Shared helpers for reading and writing list-page state in the query string.
/// </summary>
internal static class DashboardQueryString
{
    public static Dictionary<string, StringValues> ParseUri(string uri)
      => QueryHelpers.ParseQuery(new Uri(uri, UriKind.Absolute).Query);

    public static string? LastNonEmpty(
        Dictionary<string, StringValues> query,
        string parameterName)
      => DashboardJobFilterQuery.TryGetLastNonEmptyValue(query, parameterName, out var value) ? value : null;

    public static bool IsTrue(
        Dictionary<string, StringValues> query,
        string parameterName)
      => LastNonEmpty(query, parameterName) is { } value
        && (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
          || string.Equals(value, "1", StringComparison.Ordinal));

    public static string Build(
        string path,
        IEnumerable<(string Name, string? Value)> parameters)
    {
        var queryParts = new List<string>();
        foreach (var (name, value) in parameters)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            queryParts.Add(string.Concat(
              Uri.EscapeDataString(name),
              "=",
              Uri.EscapeDataString(value.Trim())));
        }

        return queryParts.Count == 0
          ? path
          : string.Concat(path, "?", string.Join("&", queryParts));
    }

    public static bool IsPath(
        string relativeUri,
        string path)
    {
        var queryIndex = relativeUri.IndexOfAny(['?', '#']);
        var relativePath = queryIndex < 0 ? relativeUri : relativeUri[..queryIndex];
        return string.Equals(relativePath.TrimEnd('/'), path, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// The jobs list state carried by a URL: explicit filters (or <see langword="null"/> to use the view's filters),
/// the selected view key (or <see langword="null"/> for the user's preferred view), and the loaded depth.
/// </summary>
internal sealed record DashboardJobListLocation(
    DashboardJobFilters? Filters,
    string? ViewKey,
    int Take)
{
    public const int DefaultTake = 25;
    public const int MaximumTake = 1000;

    public static DashboardJobListLocation Parse(string uri)
    {
        var queryString = new Uri(uri, UriKind.Absolute).Query;
        var query = QueryHelpers.ParseQuery(queryString);
        var filters = DashboardJobFilterQuery.HasRecognizedQuery(queryString)
          ? DashboardJobFilterQuery.ParseQuery(queryString)
          : null;
        var viewKey = DashboardQueryString.LastNonEmpty(query, DashboardJobFilterQuery.ViewParameter);
        return new DashboardJobListLocation(filters, viewKey, ParseTake(DashboardQueryString.LastNonEmpty(query, DashboardJobFilterQuery.TakeParameter)));
    }

    public static int NormalizeTake(int take)
      => Math.Clamp(take, DefaultTake, MaximumTake);

    /// <summary>
    /// Builds the URL for the current list state. <paramref name="viewParameter"/> comes from
    /// <see cref="ViewParameterValue"/>; the depth is written only when more than one page is loaded.
    /// </summary>
    public static string ToHref(
        DashboardJobFilters filters,
        DashboardJobFilters viewFilters,
        string? viewParameter,
        int take)
      => DashboardJobFilterQuery.ToHref(
        filters,
        viewParameter,
        take > DefaultTake ? NormalizeTake(take) : null,
        forceExplicitFilters: !filters.Matches(viewFilters));

    /// <summary>
    /// The <c>view</c> query value for the selected view. A URL without one means "the preferred view", so the key
    /// is omitted only when the selected view is both the user's preferred view and the configured default; any
    /// other view is named so the URL restores it (for other users too, when it is a shared view).
    /// </summary>
    public static string? ViewParameterValue(
        string selectedViewKey,
        string preferredViewKey,
        string configuredDefaultViewKey)
      => string.Equals(selectedViewKey, preferredViewKey, StringComparison.Ordinal)
        && string.Equals(selectedViewKey, configuredDefaultViewKey, StringComparison.Ordinal)
          ? null
          : selectedViewKey;

    private static int ParseTake(string? value)
      => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var take)
        ? NormalizeTake(take)
        : DefaultTake;
}

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

internal static class DashboardConcurrencyGroupFilterQuery
{
    public const string Path = "concurrency-groups";
    public const string KeyParameter = "key";
    public const string SaturatedParameter = "saturated";
    public const string RateLimitedParameter = "rateLimited";
    public const string BlockedParameter = "blocked";

    public static DashboardConcurrencyGroupFilters ParseUri(string uri)
    {
        var query = DashboardQueryString.ParseUri(uri);
        return new DashboardConcurrencyGroupFilters
        {
            GroupKey = DashboardQueryString.LastNonEmpty(query, KeyParameter) ?? string.Empty,
            SaturatedOnly = DashboardQueryString.IsTrue(query, SaturatedParameter),
            RateLimitedOnly = DashboardQueryString.IsTrue(query, RateLimitedParameter),
            HasBlockedJobsOnly = DashboardQueryString.IsTrue(query, BlockedParameter),
        };
    }

    public static string ToHref(DashboardConcurrencyGroupFilters filters)
      => DashboardQueryString.Build(
        Path,
        [
            (SaturatedParameter, filters.SaturatedOnly ? "true" : null),
            (RateLimitedParameter, filters.RateLimitedOnly ? "true" : null),
            (BlockedParameter, filters.HasBlockedJobsOnly ? "true" : null),
            (KeyParameter, filters.GroupKey),
        ]);
}

internal static class DashboardNodeFilterQuery
{
    public const string Path = "nodes";
    public const string StateParameter = "state";
    public const string NodeParameter = "node";

    public static DashboardNodeFilters ParseUri(string uri)
    {
        var query = DashboardQueryString.ParseUri(uri);
        var state = DashboardQueryString.LastNonEmpty(query, StateParameter);
        return new DashboardNodeFilters
        {
            State = Enum.TryParse<NodeHealthState>(state, ignoreCase: true, out var parsedState) && Enum.IsDefined(parsedState)
              ? parsedState.ToString()
              : string.Empty,
            NodeId = DashboardQueryString.LastNonEmpty(query, NodeParameter) ?? string.Empty,
        };
    }

    public static string ToHref(DashboardNodeFilters filters)
      => DashboardQueryString.Build(
        Path,
        [
            (StateParameter, filters.State),
            (NodeParameter, filters.NodeId),
        ]);
}

internal static class DashboardMetricsWindowQuery
{
    public const string Path = "metrics";
    public const string WindowParameter = "window";

    public static TimeSpan ParseUri(
        string uri,
        IReadOnlyList<TimeSpan> windows,
        TimeSpan defaultWindow)
      => TryParseWindow(DashboardQueryString.LastNonEmpty(DashboardQueryString.ParseUri(uri), WindowParameter), out var window)
        && windows.Contains(window)
          ? window
          : defaultWindow;

    public static string ToHref(
        TimeSpan window,
        TimeSpan defaultWindow)
      => DashboardQueryString.Build(
        Path,
        [
            (WindowParameter, window == defaultWindow ? null : FormatWindow(window)),
        ]);

    public static string FormatWindow(TimeSpan window)
      => window.Ticks % TimeSpan.TicksPerHour == 0
        ? string.Create(CultureInfo.InvariantCulture, $"{(long)window.TotalHours}h")
        : string.Create(CultureInfo.InvariantCulture, $"{(long)window.TotalMinutes}m");

    public static bool TryParseWindow(
        string? value,
        out TimeSpan window)
    {
        window = default;
        if (string.IsNullOrWhiteSpace(value) || value.Length < 2)
        {
            return false;
        }

        var trimmed = value.Trim();
        if (!long.TryParse(trimmed.AsSpan(0, trimmed.Length - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var amount)
            || amount <= 0
            || amount > 24 * 60)
        {
            return false;
        }

        switch (char.ToLowerInvariant(trimmed[^1]))
        {
            case 'm':
                window = TimeSpan.FromMinutes(amount);
                return true;
            case 'h':
                window = TimeSpan.FromHours(amount);
                return true;
            default:
                return false;
        }
    }
}
