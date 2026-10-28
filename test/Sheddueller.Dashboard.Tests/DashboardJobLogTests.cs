namespace Sheddueller.Dashboard.Tests;

using Sheddueller.Dashboard.Internal;
using Sheddueller.Storage;

using Shouldly;

public sealed class DashboardJobLogTests
{
    private static readonly Guid JobId = Guid.Parse("7c1e9a42-5b3d-4f1e-9a07-2d6c8e41b0f3");

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
        var line = TestData.Event(1, level: null, message: "no level");

        DashboardJobLog.Matches(line, new DashboardJobLogFilter()).ShouldBeTrue();
        DashboardJobLog.Matches(line, new DashboardJobLogFilter(DashboardLogLevelFilter.Warnings)).ShouldBeFalse();
        DashboardJobLog.LevelLabel(line).ShouldBe("INFO");
    }

    [Fact]
    public void ProgressUpdates_Default_AreHidden()
    {
        var progress = TestData.Event(1, JobEventKind.Progress, message: "chunk 4", progressPercent: 40);

        DashboardJobLog.Matches(progress, new DashboardJobLogFilter()).ShouldBeFalse();
        DashboardJobLog.Matches(progress, new DashboardJobLogFilter(IncludeProgress: true)).ShouldBeTrue();
    }

    [Fact]
    public void ProgressUpdates_LevelFilter_AreHiddenBecauseTheyHaveNoLevel()
        => DashboardJobLog.Matches(
            TestData.Event(1, JobEventKind.Progress, message: "chunk 4", progressPercent: 40),
            new DashboardJobLogFilter(DashboardLogLevelFilter.Warnings, IncludeProgress: true))
          .ShouldBeFalse();

    [Fact]
    public void LifecycleEvents_AnyFilter_AreNotLogLines()
    {
        var lifecycle = TestData.Event(1, JobEventKind.Lifecycle, message: "Queued");

        DashboardJobLog.IsLogLine(lifecycle).ShouldBeFalse();
        DashboardJobLog.Matches(lifecycle, new DashboardJobLogFilter(IncludeProgress: true)).ShouldBeFalse();
    }

    [Fact]
    public void AttemptFilter_SelectedAttempt_ShowsOnlyThatAttemptsLines()
    {
        JobEvent[] events =
        [
            TestData.Event(1, level: JobLogLevel.Information, message: "first", attempt: 1),
            TestData.Event(2, level: JobLogLevel.Information, message: "second", attempt: 2),
            TestData.Event(3, level: JobLogLevel.Error, message: "second failed", attempt: 2),
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
        var line = TestData.Event(1, level: JobLogLevel.Warning, message: "Request timeout after 30s") with
        {
            Fields = new Dictionary<string, string>(StringComparer.Ordinal) { ["TenantId"] = "acme" },
        };

        DashboardJobLog.Matches(line, new DashboardJobLogFilter(Search: search)).ShouldBeTrue();
    }

    [Fact]
    public void Search_NoMatch_HidesLine()
        => DashboardJobLog.Matches(
            TestData.Event(1, level: JobLogLevel.Warning, message: "Request timeout after 30s"),
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
            TestData.Event(1, level: JobLogLevel.Information, message: "third", attempt: 3),
            TestData.Event(2, level: JobLogLevel.Information, message: "first", attempt: 1),
            TestData.Event(3, JobEventKind.Progress, message: "progress", progressPercent: 10, attempt: 2),
            TestData.Event(4, level: JobLogLevel.Information, message: "first again", attempt: 1),
            TestData.Event(5, JobEventKind.AttemptStarted, attempt: 4, message: "lifecycle", level: JobLogLevel.Information),
        ];

        DashboardJobLog.Attempts(events).ShouldBe([1, 2, 3]);
    }

    [Fact]
    public void Fields_LoggerFields_FollowTheJobsOwnFields()
    {
        var line = TestData.Event(1, level: JobLogLevel.Information, message: "done") with
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
            TestData.Event(1, level: JobLogLevel.Information, message: "Starting", attempt: 1),
            TestData.Event(2, level: JobLogLevel.Error, message: "Failed to connect\nretrying later", attempt: 2) with
            {
                Fields = new Dictionary<string, string>(StringComparer.Ordinal) { ["Host"] = "db-1" },
            },
            TestData.Event(3, JobEventKind.Progress, message: "half way", progressPercent: 50, attempt: 2),
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
          TestData.Event(1, level: JobLogLevel.Trace, message: "trace"),
          TestData.Event(2, level: JobLogLevel.Debug, message: "debug"),
          TestData.Event(3, level: JobLogLevel.Information, message: "info"),
          TestData.Event(4, level: JobLogLevel.Warning, message: "warning"),
          TestData.Event(5, level: JobLogLevel.Error, message: "error"),
          TestData.Event(6, level: JobLogLevel.Critical, message: "critical"),
      ];

    [Fact]
    public void Message_ProgressUpdate_ReadsAsPercentAndMessage()
    {
        DashboardJobLog.Message(TestData.Event(1, JobEventKind.Progress, message: "chunk 7", progressPercent: 70)).ShouldBe("70% chunk 7");
        DashboardJobLog.Message(TestData.Event(2, JobEventKind.Progress, message: string.Empty, progressPercent: 140)).ShouldBe("100%");
        DashboardJobLog.Message(TestData.Event(3, JobEventKind.Progress, message: "half", progressPercent: null)).ShouldBe("half");
        DashboardJobLog.Message(TestData.Event(4, JobEventKind.Progress, message: " ", progressPercent: null)).ShouldBe("Progress reported");
        DashboardJobLog.Message(TestData.Event(5, level: JobLogLevel.Information, message: " ")).ShouldBe("Log");
    }
}
