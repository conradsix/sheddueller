namespace Sheddueller.Dashboard.Internal;

using System.Globalization;

/// <summary>
/// The metrics page's query string: the selected window, written as <c>5m</c> or <c>24h</c>.
/// </summary>
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
