namespace Sheddueller.Dashboard.Tests;

using System.Globalization;

using Sheddueller.Dashboard.Internal;
using Sheddueller.Storage;

using Shouldly;

public sealed class DashboardJobLogTests
{
    private static readonly Guid JobId = Guid.Parse("7c1e9a42-5b3d-4f1e-9a07-2d6c8e41b0f3");
    private static readonly DateTimeOffset StartedAtUtc = DateTimeOffset.Parse("2026-04-20T12:00:00Z", CultureInfo.InvariantCulture);

    [Fact]
    public void LevelFilter_Warnings_ShowsWarningsAndMoreSevereLines()
    {
        var events = CreateLevels();

        var lines = DashboardJobLog.Filter(events, new DashboardJobLogFilter(DashboardLogLevelFilter.Warnings));

        lines.Select(line => line.LogLevel).ShouldBe([JobLogLevel.Warning, JobLogLevel.Error, JobLogLevel.Critical]);
    }

    [Fact]
    public void LevelFilter_Errors_ShowsErrorAndCriticalLines()
    {
        var events = CreateLevels();

        var lines = DashboardJobLog.Filter(events, new DashboardJobLogFilter(DashboardLogLevelFilter.Errors));

        lines.Select(line => line.LogLevel).ShouldBe([JobLogLevel.Error, JobLogLevel.Critical]);
    }

    [Fact]
    public void LevelFilter_LineWithoutLevel_CountsAsInformation()
    {
        var line = CreateLog(1, JobLogLevel.Information, "no level") with { LogLevel = null };

        DashboardJobLog.Matches(line, new DashboardJobLogFilter()).ShouldBeTrue();
        DashboardJobLog.Matches(line, new DashboardJobLogFilter(DashboardLogLevelFilter.Warnings)).ShouldBeFalse();
        DashboardJobLog.LevelLabel(line).ShouldBe("INFO");
    }

    [Fact]
    public void ProgressUpdates_Default_AreHidden()
    {
        var progress = CreateProgress(1, 40, "chunk 4");

        DashboardJobLog.Matches(progress, new DashboardJobLogFilter()).ShouldBeFalse();
        DashboardJobLog.Matches(progress, new DashboardJobLogFilter(IncludeProgress: true)).ShouldBeTrue();
    }

    [Fact]
    public void ProgressUpdates_LevelFilter_AreHiddenBecauseTheyHaveNoLevel()
        => DashboardJobLog.Matches(
            CreateProgress(1, 40, "chunk 4"),
            new DashboardJobLogFilter(DashboardLogLevelFilter.Warnings, IncludeProgress: true))
          .ShouldBeFalse();

    [Fact]
    public void LifecycleEvents_AnyFilter_AreNotLogLines()
    {
        var lifecycle = CreateLog(1, JobLogLevel.Information, "Queued") with { Kind = JobEventKind.Lifecycle, LogLevel = null };

        DashboardJobLog.IsLogLine(lifecycle).ShouldBeFalse();
        DashboardJobLog.Matches(lifecycle, new DashboardJobLogFilter(IncludeProgress: true)).ShouldBeFalse();
    }

    [Fact]
    public void AttemptFilter_SelectedAttempt_ShowsOnlyThatAttemptsLines()
    {
        JobEvent[] events =
        [
            CreateLog(1, JobLogLevel.Information, "first", attempt: 1),
            CreateLog(2, JobLogLevel.Information, "second", attempt: 2),
            CreateLog(3, JobLogLevel.Error, "second failed", attempt: 2),
        ];

        var lines = DashboardJobLog.Filter(events, new DashboardJobLogFilter(Attempt: 2));

        lines.Select(line => line.Message).ShouldBe(["second", "second failed"]);
    }

    [Theory]
    [InlineData("TIMEOUT")]
    [InlineData("tenant")]
    [InlineData("ACME")]
    public void Search_MessageOrField_MatchesCaseInsensitively(string search)
    {
        var line = CreateLog(1, JobLogLevel.Warning, "Request timeout after 30s") with
        {
            Fields = new Dictionary<string, string>(StringComparer.Ordinal) { ["TenantId"] = "acme" },
        };

        DashboardJobLog.Matches(line, new DashboardJobLogFilter(Search: search)).ShouldBeTrue();
    }

    [Fact]
    public void Search_NoMatch_HidesLine()
        => DashboardJobLog.Matches(
            CreateLog(1, JobLogLevel.Warning, "Request timeout after 30s"),
            new DashboardJobLogFilter(Search: "billing"))
          .ShouldBeFalse();

    [Theory]
    [InlineData(nameof(DashboardLogLevelFilter.All), null, null, false)]
    [InlineData(nameof(DashboardLogLevelFilter.All), null, "  ", false)]
    [InlineData(nameof(DashboardLogLevelFilter.Errors), null, null, true)]
    [InlineData(nameof(DashboardLogLevelFilter.All), 2, null, true)]
    [InlineData(nameof(DashboardLogLevelFilter.All), null, "timeout", true)]
    public void Narrowing_LevelAttemptOrSearch_HidesLines(string level, int? attempt, string? search, bool expected)
        => new DashboardJobLogFilter(Enum.Parse<DashboardLogLevelFilter>(level), attempt, search, IncludeProgress: true).IsNarrowed.ShouldBe(expected);

    [Fact]
    public void Attempts_LogLines_AreDistinctAndOrdered()
    {
        JobEvent[] events =
        [
            CreateLog(1, JobLogLevel.Information, "third", attempt: 3),
            CreateLog(2, JobLogLevel.Information, "first", attempt: 1),
            CreateProgress(3, 10, "progress", attempt: 2),
            CreateLog(4, JobLogLevel.Information, "first again", attempt: 1),
            CreateLog(5, JobLogLevel.Information, "lifecycle", attempt: 4) with { Kind = JobEventKind.AttemptStarted },
        ];

        DashboardJobLog.Attempts(events).ShouldBe([1, 2, 3]);
    }

    [Fact]
    public void Fields_LoggerFields_FollowTheJobsOwnFields()
    {
        var line = CreateLog(1, JobLogLevel.Information, "done") with
        {
            Fields = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["LoggerCategory"] = "Sample.ReportJob",
                ["rows"] = "120",
                ["EventId"] = "7",
                ["Batch"] = "4",
            },
        };

        DashboardJobLog.Fields(line).Select(field => field.Key).ShouldBe(["Batch", "rows", "EventId", "LoggerCategory"]);
    }

    [Fact]
    public void TextExport_Lines_FormatsTimeLevelAttemptMessageAndFields()
    {
        JobEvent[] events =
        [
            CreateLog(1, JobLogLevel.Information, "Starting", attempt: 1),
            CreateLog(2, JobLogLevel.Error, "Failed to connect\nretrying later", attempt: 2) with
            {
                Fields = new Dictionary<string, string>(StringComparer.Ordinal) { ["Host"] = "db-1" },
            },
            CreateProgress(3, 50, "half way", attempt: 2),
        ];

        var text = DashboardJobLog.ToText(events);

        text.ShouldBe(
          "2026-04-20T12:00:01.000Z INFO  attempt 1  Starting\n"
          + "2026-04-20T12:00:02.000Z ERROR attempt 2  Failed to connect\n"
          + "    retrying later\n"
          + "    Host=db-1\n"
          + "2026-04-20T12:00:03.000Z PROG  attempt 2  50% half way\n");
    }

    [Fact]
    public void TextExport_FileName_NamesTheJob()
        => DashboardJobLog.FileName(JobId).ShouldBe("job-7c1e9a42-5b3d-4f1e-9a07-2d6c8e41b0f3-log.txt");

    private static JobEvent[] CreateLevels()
      =>
      [
          CreateLog(1, JobLogLevel.Trace, "trace"),
          CreateLog(2, JobLogLevel.Debug, "debug"),
          CreateLog(3, JobLogLevel.Information, "info"),
          CreateLog(4, JobLogLevel.Warning, "warning"),
          CreateLog(5, JobLogLevel.Error, "error"),
          CreateLog(6, JobLogLevel.Critical, "critical"),
      ];

    [Fact]
    public void Message_ProgressUpdate_ReadsAsPercentAndMessage()
    {
        DashboardJobLog.Message(CreateProgress(1, 70, "chunk 7")).ShouldBe("70% chunk 7");
        DashboardJobLog.Message(CreateProgress(2, 140, string.Empty)).ShouldBe("100%");
        DashboardJobLog.Message(CreateProgress(3, 50, "half") with { ProgressPercent = null }).ShouldBe("half");
        DashboardJobLog.Message(CreateProgress(4, 50, " ") with { ProgressPercent = null }).ShouldBe("Progress reported");
        DashboardJobLog.Message(CreateLog(5, JobLogLevel.Information, " ")).ShouldBe("Log");
    }

    private static JobEvent CreateLog(
        long sequence,
        JobLogLevel level,
        string message,
        int attempt = 1)
      => new(
        Guid.NewGuid(),
        JobId,
        sequence,
        JobEventKind.Log,
        StartedAtUtc.AddSeconds(sequence),
        attempt,
        level,
        message);

    private static JobEvent CreateProgress(
        long sequence,
        double percent,
        string message,
        int attempt = 1)
      => new(
        Guid.NewGuid(),
        JobId,
        sequence,
        JobEventKind.Progress,
        StartedAtUtc.AddSeconds(sequence),
        attempt,
        Message: message,
        ProgressPercent: percent);
}
