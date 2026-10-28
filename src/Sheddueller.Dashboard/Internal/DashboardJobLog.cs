namespace Sheddueller.Dashboard.Internal;

using System.Globalization;
using System.Text;

using Sheddueller.Storage;

/// <summary>
/// Which log levels the job detail log shows.
/// </summary>
internal enum DashboardLogLevelFilter
{
    /// <summary>
    /// Every level.
    /// </summary>
    All,

    /// <summary>
    /// Warnings and anything more severe.
    /// </summary>
    Warnings,

    /// <summary>
    /// Errors and critical lines.
    /// </summary>
    Errors,
}

/// <summary>
/// The filters applied to the job detail log.
/// </summary>
/// <param name="Level">Minimum level shown. Progress updates only show at <see cref="DashboardLogLevelFilter.All" />.</param>
/// <param name="Attempt">Only lines written by this attempt, or every attempt when <see langword="null" />.</param>
/// <param name="Search">Case-insensitive text matched against the message and the structured fields.</param>
/// <param name="IncludeProgress">Shows progress updates alongside log lines.</param>
internal sealed record DashboardJobLogFilter(
    DashboardLogLevelFilter Level = DashboardLogLevelFilter.All,
    int? Attempt = null,
    string? Search = null,
    bool IncludeProgress = false)
{
    /// <summary>
    /// The level, attempt, or search hides some lines. Showing progress updates only ever adds lines.
    /// </summary>
    public bool IsNarrowed
      => this.Level != DashboardLogLevelFilter.All
        || this.Attempt is not null
        || !string.IsNullOrWhiteSpace(this.Search);
}

/// <summary>
/// Filtering, labelling, and text export for the log lines on the job detail page.
/// </summary>
internal static class DashboardJobLog
{
    // Fields the job logger adds to every line; shown after the fields the job wrote itself.
    private static readonly string[] TrailingFieldKeys = ["EventId", "EventName", "LoggerCategory"];

    public static bool IsLogLine(JobEvent jobEvent)
      => jobEvent.Kind is JobEventKind.Log or JobEventKind.Progress;

    public static IReadOnlyList<JobEvent> Filter(
        IEnumerable<JobEvent> events,
        DashboardJobLogFilter filter)
      => [.. events.Where(jobEvent => Matches(jobEvent, filter))];

    public static int Count(
        IEnumerable<JobEvent> events,
        DashboardJobLogFilter filter)
      => events.Count(jobEvent => Matches(jobEvent, filter));

    public static bool Matches(
        JobEvent jobEvent,
        DashboardJobLogFilter filter)
    {
        if (!IsLogLine(jobEvent))
        {
            return false;
        }

        if (jobEvent.Kind == JobEventKind.Progress)
        {
            if (!filter.IncludeProgress || filter.Level != DashboardLogLevelFilter.All)
            {
                return false;
            }
        }
        else if (!MatchesLevel(jobEvent.LogLevel ?? JobLogLevel.Information, filter.Level))
        {
            return false;
        }

        if (filter.Attempt is { } attempt && jobEvent.AttemptNumber != attempt)
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(filter.Search) || MatchesSearch(jobEvent, filter.Search.Trim());
    }

    /// <summary>
    /// The attempts that wrote at least one log line or progress update, in order.
    /// </summary>
    public static IReadOnlyList<int> Attempts(IEnumerable<JobEvent> events)
      => [.. events.Where(IsLogLine).Select(jobEvent => jobEvent.AttemptNumber).Distinct().Order()];

    /// <summary>
    /// Short upper-case level label, padded by the caller where alignment matters.
    /// </summary>
    public static string LevelLabel(JobEvent jobEvent)
      => jobEvent.Kind == JobEventKind.Progress
        ? "PROG"
        : (jobEvent.LogLevel ?? JobLogLevel.Information) switch
        {
            JobLogLevel.Trace => "TRACE",
            JobLogLevel.Debug => "DEBUG",
            JobLogLevel.Information => "INFO",
            JobLogLevel.Warning => "WARN",
            JobLogLevel.Error => "ERROR",
            JobLogLevel.Critical => "CRIT",
            _ => "INFO",
        };

    /// <summary>
    /// CSS modifier for the line's level: <c>trace</c>, <c>debug</c>, <c>info</c>, <c>warning</c>, <c>error</c>,
    /// <c>critical</c>, or <c>progress</c>.
    /// </summary>
    public static string LevelModifier(JobEvent jobEvent)
      => jobEvent.Kind == JobEventKind.Progress
        ? "progress"
        : (jobEvent.LogLevel ?? JobLogLevel.Information) switch
        {
            JobLogLevel.Trace => "trace",
            JobLogLevel.Debug => "debug",
            JobLogLevel.Warning => "warning",
            JobLogLevel.Error => "error",
            JobLogLevel.Critical => "critical",
            _ => "info",
        };

    /// <summary>
    /// The line's text; a progress update reads as its percentage and message.
    /// </summary>
    public static string Message(JobEvent jobEvent)
    {
        if (jobEvent.Kind == JobEventKind.Progress)
        {
            return jobEvent.ProgressPercent is { } percent
              ? string.Create(CultureInfo.InvariantCulture, $"{Math.Clamp(percent, 0, 100):0.#}% {jobEvent.Message}").Trim()
              : DashboardFormat.FirstNonEmpty(jobEvent.Message, "Progress reported");
        }

        return DashboardFormat.FirstNonEmpty(jobEvent.Message, jobEvent.Kind.ToString());
    }

    /// <summary>
    /// The line's structured fields: the job's own fields by name, then the fields the job logger adds to every line.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> Fields(JobEvent jobEvent)
      => jobEvent.Fields is not { Count: > 0 } fields
        ? []
        : [
            .. fields
              .OrderBy(field => Array.IndexOf(TrailingFieldKeys, field.Key))
              .ThenBy(field => field.Key, StringComparer.OrdinalIgnoreCase),
        ];

    public static string Time(JobEvent jobEvent)
      => jobEvent.OccurredAtUtc.ToUniversalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);

    public static string FileName(Guid jobId)
      => string.Create(CultureInfo.InvariantCulture, $"job-{jobId:D}-log.txt");

    /// <summary>
    /// Plain-text log: one line per event (UTC time, level, attempt, message), continuation lines and fields indented.
    /// </summary>
    public static string ToText(IEnumerable<JobEvent> events)
    {
        var builder = new StringBuilder();
        foreach (var jobEvent in events)
        {
            builder.Append(jobEvent.OccurredAtUtc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture))
              .Append(' ')
              .Append(LevelLabel(jobEvent).PadRight(5))
              .Append(' ')
              .Append(string.Create(CultureInfo.InvariantCulture, $"attempt {jobEvent.AttemptNumber}"))
              .Append("  ");

            var lines = Message(jobEvent).ReplaceLineEndings("\n").Split('\n');
            builder.Append(lines[0]).Append('\n');
            foreach (var line in lines.Skip(1))
            {
                builder.Append("    ").Append(line).Append('\n');
            }

            foreach (var field in Fields(jobEvent))
            {
                builder.Append("    ").Append(field.Key).Append('=').Append(field.Value.ReplaceLineEndings(" ")).Append('\n');
            }
        }

        return builder.ToString();
    }

    private static bool MatchesLevel(
        JobLogLevel level,
        DashboardLogLevelFilter filter)
      => filter switch
      {
          DashboardLogLevelFilter.Warnings => level >= JobLogLevel.Warning,
          DashboardLogLevelFilter.Errors => level >= JobLogLevel.Error,
          _ => true,
      };

    private static bool MatchesSearch(
        JobEvent jobEvent,
        string search)
      => Message(jobEvent).Contains(search, StringComparison.OrdinalIgnoreCase)
        || (jobEvent.Fields?.Any(field =>
          field.Key.Contains(search, StringComparison.OrdinalIgnoreCase)
          || field.Value.Contains(search, StringComparison.OrdinalIgnoreCase)) ?? false);
}
