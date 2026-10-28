namespace Sheddueller.Dashboard.Tests;

using System.Globalization;
using System.Text.Json;

using Sheddueller.Dashboard.Internal;
using Sheddueller.Inspection.Jobs;
using Sheddueller.Inspection.Schedules;
using Sheddueller.Storage;

using Shouldly;

public sealed class DashboardFormatTests
{
    [Fact]
    public void Handler_DisplayFormats_UsesFullAndShortNames()
    {
        var job = TestData.Job(serviceType: "Sheddueller.Dashboard.Tests.Workers.RollupWorker", methodName: "RunAsync");

        DashboardFormat.FullHandler(job).ShouldBe("Sheddueller.Dashboard.Tests.Workers.RollupWorker.RunAsync");
        DashboardFormat.ShortHandler(job).ShouldBe("RollupWorker.RunAsync");
    }

    [Fact]
    public void Handler_Schedule_UsesFullAndShortNames()
    {
        var schedule = new ScheduleInspectionSummary(
          "nightly",
          "Acme.Jobs.RateLoader, Acme.Jobs",
          "LoadRates",
          "0 2 * * *",
          IsPaused: false,
          RecurringOverlapMode.Skip,
          NextFireAtUtc: null,
          Priority: 0,
          ConcurrencyGroupKeys: [],
          Tags: [],
          DateTimeOffset.Parse("2026-04-20T12:00:00Z", CultureInfo.InvariantCulture));

        DashboardFormat.FullHandler(schedule).ShouldBe("Acme.Jobs.RateLoader, Acme.Jobs.LoadRates");
        DashboardFormat.ShortHandler(schedule).ShouldBe("RateLoader.LoadRates");
    }

    [Theory]
    [InlineData("8c32d457-9e7a-42bb-8947-0c8fa54743be", "8c32d457")]
    [InlineData("8c32d457", "8c32d457")]
    [InlineData("abc", "abc")]
    public void ShortJobId_TextFromAUrl_KeepsTheFirstBlock(string id, string expected)
      => DashboardFormat.ShortJobId(id).ShouldBe(expected);

    [Fact]
    public void ShortJobId_Guid_IsTheFirstBlock()
      => DashboardFormat.ShortJobId(Guid.Parse("8c32d457-9e7a-42bb-8947-0c8fa54743be")).ShouldBe("8c32d457");

    [Fact]
    public void FailureReason_ProgressMessageOrQueueReason_PrefersTheProgressMessage()
    {
        var job = TestData.Job() with
        {
            State = JobState.Failed,
            QueuePosition = new JobQueuePosition(Guid.Empty, JobQueuePositionKind.Terminal, Position: null, Reason: "Job is terminal"),
        };

        DashboardFormat.FailureReason(job).ShouldBe("Job is terminal");
        DashboardFormat.FailureReason(job with { LatestProgress = Progress("Timeout talking to the API") }).ShouldBe("Timeout talking to the API");
        DashboardFormat.FailureReason(job with { LatestProgress = Progress(" "), QueuePosition = null }).ShouldBeNull();
    }

    [Fact]
    public void Disposition_FailedJob_ShowsTheFailureReasonOrFailed()
    {
        var failed = TestData.Job() with { State = JobState.Failed };

        DashboardFormat.Disposition(failed with { LatestProgress = Progress("Timeout") }).ShouldBe("Timeout");
        DashboardFormat.Disposition(failed).ShouldBe("failed");
        DashboardFormat.DispositionClass(failed).ShouldBe("jobs-disposition--error");
        DashboardFormat.DispositionClass(failed with { State = JobState.Completed }).ShouldBe("jobs-disposition--muted");
    }

    [Theory]
    [InlineData(JobQueuePositionKind.Claimable, 3L, "#3", "jobs-queue jobs-queue--claimable")]
    [InlineData(JobQueuePositionKind.Claimable, null, "Ready", "jobs-queue jobs-queue--claimable")]
    [InlineData(JobQueuePositionKind.RetryWaiting, null, "Retry", "jobs-queue jobs-queue--waiting")]
    [InlineData(JobQueuePositionKind.BlockedByConcurrency, null, "Blocked", "jobs-queue jobs-queue--blocked")]
    [InlineData(JobQueuePositionKind.Terminal, null, "-", "jobs-queue jobs-queue--empty")]
    public void QueuePosition_Kind_HasTextAndClass(JobQueuePositionKind kind, long? position, string text, string cssClass)
    {
        var job = TestData.Job() with { QueuePosition = new JobQueuePosition(Guid.Empty, kind, position) };

        DashboardFormat.QueuePositionText(job).ShouldBe(text);
        DashboardFormat.QueuePositionClass(job).ShouldBe(cssClass);
        DashboardFormat.QueuePositionTitle(job).ShouldBe(text);
        DashboardFormat.QueuePositionTitle(job with { QueuePosition = new JobQueuePosition(Guid.Empty, kind, position, "Waiting on group billing") })
          .ShouldBe("Waiting on group billing");
    }

    [Fact]
    public void Progress_PercentOutsideRange_ClampsDisplayValues()
    {
        var progress = new JobProgressSnapshot(125, "indexed records", DateTimeOffset.Parse("2026-04-20T12:08:00Z", CultureInfo.InvariantCulture));

        DashboardFormat.ProgressPercent(progress).ShouldBe("100%");
        DashboardFormat.ProgressText(progress).ShouldBe("100% indexed records");
    }

    [Fact]
    public void Relative_PastAndFutureValues_FormatsCompactUtcDurations()
    {
        var now = DateTimeOffset.Parse("2026-04-20T12:00:00Z", CultureInfo.InvariantCulture);

        DashboardFormat.Relative(now.AddMinutes(-42), now).ShouldBe("42m ago");
        DashboardFormat.Relative(now.AddHours(3), now).ShouldBe("in 3h");
        DashboardFormat.Utc(now).ShouldBe("2026-04-20 12:00:00 UTC");
        DashboardFormat.UtcMinute(now).ShouldBe("12:00 UTC");
        DashboardFormat.UtcMinute(null).ShouldBeEmpty();
    }

    [Fact]
    public void Relative_FutureValueWhenFutureDisabled_RendersJustNow()
    {
        var now = DateTimeOffset.Parse("2026-04-20T12:00:00Z", CultureInfo.InvariantCulture);

        DashboardFormat.Relative(now.AddSeconds(30), now, allowFuture: false).ShouldBe("just now");
    }

    [Fact]
    public void TerminalTimestamp_CompletedAfterAFailedAttempt_UsesTheCompletionTime()
    {
        var failedAtUtc = DateTimeOffset.Parse("2026-04-20T12:01:00Z", CultureInfo.InvariantCulture);
        var job = TestData.Job() with { FailedAtUtc = failedAtUtc };

        DashboardFormat.TerminalTimestamp(job with { State = JobState.Completed, CompletedAtUtc = failedAtUtc.AddMinutes(1) })
          .ShouldBe(failedAtUtc.AddMinutes(1));
        DashboardFormat.TerminalTimestamp(job with { State = JobState.Failed }).ShouldBe(failedAtUtc);
        DashboardFormat.TerminalTimestamp(job with { State = JobState.Queued }).ShouldBeNull();
    }

    [Theory]
    [InlineData(45, "<1m")]
    [InlineData(2_520, "42m")]
    [InlineData(14_340, "3h")]
    [InlineData(176_400, "2d")]
    public void Elapsed_RunningDurations_RoundDownToTheLargestUnit(int seconds, string expected)
        => DashboardFormat.Elapsed(TimeSpan.FromSeconds(seconds)).ShouldBe(expected);

    [Fact]
    public void TagsAndGroups_EmptyAndPopulatedValues_FormatsTitlesForChips()
    {
        var tags = new[] { new JobTag("tenant", "acme"), new JobTag("schedule", "daily_rollup") };

        DashboardFormat.Tags(tags).ShouldBe(["tenant:acme", "schedule:daily_rollup"]);
        DashboardFormat.TagsTitle(tags).ShouldBe("tenant:acme, schedule:daily_rollup");
        DashboardFormat.TagsTitle([]).ShouldBe("No tags");
        DashboardFormat.GroupKeysTitle(["tenant-acme", "daily-rollup"]).ShouldBe("tenant-acme, daily-rollup");
    }

    [Fact]
    public void TagOrder_ConfiguredNames_PrioritizesConfiguredNamesAndPreservesOrdinalFallback()
    {
        var tags = new[]
        {
            new JobTag("source", "api"),
            new JobTag("tenant", "acme"),
            new JobTag("domain", "billing"),
            new JobTag("job", "sync"),
        };

        DashboardTagOrder.Apply(tags, ["domain", "tenant"])
          .ShouldBe(
          [
              new JobTag("domain", "billing"),
              new JobTag("tenant", "acme"),
              new JobTag("source", "api"),
              new JobTag("job", "sync"),
          ]);
    }

    [Fact]
    public void TagOrder_OptionsValidation_RejectsEmptyAndDuplicateNames()
    {
        DashboardTagOrder.IsValid(new ShedduellerDashboardOptions { TagDisplayOrder = [" tenant ", "domain"] }).ShouldBeTrue();
        DashboardTagOrder.IsValid(new ShedduellerDashboardOptions { TagDisplayOrder = ["tenant", " tenant "] }).ShouldBeFalse();
        DashboardTagOrder.IsValid(new ShedduellerDashboardOptions { TagDisplayOrder = ["tenant", " "] }).ShouldBeFalse();
    }

    [Fact]
    public void JobViews_OptionsValidation_RequiresUniqueNamesValidColumnsAndExistingDefault()
    {
        var validView = new ShedduellerDashboardJobView("Failures")
        {
            States = [JobState.Failed],
            Sort = JobInspectionSort.NewestFirst,
            Columns =
            [
                new(ShedduellerDashboardJobColumnKind.JobId),
                new(ShedduellerDashboardJobColumnKind.Handler),
                new(ShedduellerDashboardJobColumnKind.Tag, "provider", "Provider"),
            ],
        };

        DashboardJobViews.IsValid(new ShedduellerDashboardOptions
        {
            JobViews = [validView],
            DefaultJobViewName = " failures ",
        }).ShouldBeTrue();
        DashboardJobViews.IsValid(new ShedduellerDashboardOptions
        {
            JobViews = [validView, validView with { Name = " failures " }],
        }).ShouldBeFalse();
        DashboardJobViews.IsValid(new ShedduellerDashboardOptions
        {
            JobViews = [validView with { Columns = [new(ShedduellerDashboardJobColumnKind.Handler)] }],
        }).ShouldBeFalse();
        DashboardJobViews.IsValid(new ShedduellerDashboardOptions
        {
            JobViews = [validView],
            DefaultJobViewName = "Missing",
        }).ShouldBeFalse();
        DashboardJobViews.IsValid(new ShedduellerDashboardOptions
        {
            JobViews =
            [
                validView with
                {
                    Columns =
                    [
                        new(ShedduellerDashboardJobColumnKind.JobId),
                        new ShedduellerDashboardJobColumn(ShedduellerDashboardJobColumnKind.Handler) { Width = 47 },
                    ],
                },
            ],
        }).ShouldBeFalse();
    }

    [Fact]
    public void JobViews_PromotedTags_ExtractsDistinctValuesAndRemovesResidualTags()
    {
        var tags = new[]
        {
            new JobTag("source", "channel"),
            new JobTag("Provider", "streamline"),
            new JobTag("provider", "streamline"),
            new JobTag("provider", "rentals-united"),
            new JobTag("listing-id", "42"),
        };
        var columns = new[]
        {
            new ShedduellerDashboardJobColumn(ShedduellerDashboardJobColumnKind.JobId),
            new ShedduellerDashboardJobColumn(ShedduellerDashboardJobColumnKind.Tag, "provider"),
        };

        DashboardJobViews.GetTagValues(tags, "provider")
          .ShouldBe([new JobTag("Provider", "streamline"), new JobTag("provider", "rentals-united")]);
        DashboardJobViews.GetResidualTags(tags, columns)
          .ShouldBe([new JobTag("source", "channel"), new JobTag("listing-id", "42")]);
        DashboardJobViews.BuiltInColumns.ShouldNotContain(
          column => column.Kind == ShedduellerDashboardJobColumnKind.Progress);
    }

    [Fact]
    public void JobViews_StoragePayload_RoundTripsVersionPreferenceAndDefinition()
    {
        var id = Guid.Parse("ab40ef11-b70d-44c9-8a91-3c356f45da73").ToString("D");
        var payload = new DashboardJobViewStoragePayload
        {
            PreferredViewKey = DashboardJobViews.PersonalKey(id),
            Views =
            [
                new DashboardStoredJobView
                {
                    Id = id,
                    View = new ShedduellerDashboardJobView("Listings")
                    {
                        TagContains = "domain:listing",
                        Columns =
                        [
                            new(ShedduellerDashboardJobColumnKind.JobId),
                            new ShedduellerDashboardJobColumn(ShedduellerDashboardJobColumnKind.Tag, "provider", "Provider")
                            {
                                Width = 124,
                            },
                        ],
                    },
                },
            ],
        };

        var json = JsonSerializer.Serialize(payload);
        var restored = JsonSerializer.Deserialize<DashboardJobViewStoragePayload>(json);

        restored.ShouldNotBeNull();
        restored.Version.ShouldBe(DashboardJobViewStoragePayload.CurrentVersion);
        restored.PreferredViewKey.ShouldBe(payload.PreferredViewKey);
        restored.Views.Single().View.TagContains.ShouldBe("domain:listing");
        restored.Views.Single().View.Columns![1].TagName.ShouldBe("provider");
        restored.Views.Single().View.Columns![1].Width.ShouldBe(124);
    }

    [Fact]
    public void MetricsFormat_RatesDurationsAndActivity_FormatsOperationalValues()
    {
        var inactiveWindow = TestData.MetricsWindow();
        var activeWindow = inactiveWindow with
        {
            SuccessRatePerMinute = 842.25,
            P95ExecutionDuration = TimeSpan.FromMilliseconds(1200),
            SaturatedConcurrencyGroupCount = 2,
        };

        DashboardMetricsFormat.HasNoActivity(inactiveWindow).ShouldBeTrue();
        DashboardMetricsFormat.HasNoActivity(activeWindow).ShouldBeFalse();
        DashboardMetricsFormat.WindowLabel(TimeSpan.FromMinutes(5)).ShouldBe("5m");
        DashboardMetricsFormat.Rate(0.04).ShouldBe("<0.1/min");
        DashboardMetricsFormat.Rate(activeWindow.SuccessRatePerMinute).ShouldBe("842.3/min");
        DashboardMetricsFormat.Duration(activeWindow.P95ExecutionDuration).ShouldBe("1.2 s");
        DashboardMetricsFormat.AttentionValueClass(activeWindow.SaturatedConcurrencyGroupCount).ShouldBe("metrics-value--attention");
        DashboardMetricsFormat.AttentionValueClass(0).ShouldBeEmpty();
        DashboardMetricsFormat.WarningValueClass(0.5).ShouldBe("metrics-value--warning");
    }

    [Fact]
    public void MetricsFormat_Period_ReadsAsRunningText()
    {
        DashboardMetricsFormat.Period(TimeSpan.FromHours(1)).ShouldBe("hour");
        DashboardMetricsFormat.Period(TimeSpan.FromHours(24)).ShouldBe("24 hours");
        DashboardMetricsFormat.Period(TimeSpan.FromMinutes(1)).ShouldBe("minute");
        DashboardMetricsFormat.Period(TimeSpan.FromMinutes(5)).ShouldBe("5 minutes");
        DashboardMetricsFormat.Period(TimeSpan.FromMinutes(90)).ShouldBe("90 minutes");
        DashboardMetricsFormat.Period(TimeSpan.FromSeconds(30)).ShouldBe("30 seconds");
    }

    private static JobProgressSnapshot Progress(string message)
      => new(Percent: null, message, DateTimeOffset.Parse("2026-04-20T12:08:00Z", CultureInfo.InvariantCulture));
}
