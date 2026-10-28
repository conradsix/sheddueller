namespace Sheddueller.Dashboard.Tests;

using System.Globalization;
using System.Text.Json;

using Sheddueller.Dashboard.Internal;
using Sheddueller.Inspection.Jobs;
using Sheddueller.Inspection.Metrics;
using Sheddueller.Storage;

using Shouldly;

public sealed class DashboardFormatTests
{
    [Fact]
    public void Handler_DisplayFormats_UsesFullAndShortNames()
    {
        var job = CreateJob(serviceType: "Sheddueller.Dashboard.Tests.Workers.RollupWorker", methodName: "RunAsync");

        DashboardFormat.FullHandler(job).ShouldBe("Sheddueller.Dashboard.Tests.Workers.RollupWorker.RunAsync");
        DashboardFormat.ShortHandler(job).ShouldBe("RollupWorker.RunAsync");
    }

    [Fact]
    public void ShortTypeName_AssemblyQualifiedNames_DropsNamespacesAndAssemblies()
    {
        DashboardFormat.ShortTypeName(typeof(CancellationToken).AssemblyQualifiedName!).ShouldBe("CancellationToken");
        DashboardFormat.ShortTypeName(typeof(IProgress<decimal>).AssemblyQualifiedName!).ShouldBe("IProgress<Decimal>");
        DashboardFormat.ShortTypeName(typeof(Dictionary<string, List<int>>).AssemblyQualifiedName!).ShouldBe("Dictionary<String, List<Int32>>");
        DashboardFormat.ShortTypeName(typeof(int?[]).AssemblyQualifiedName!).ShouldBe("Nullable<Int32>[]");
        DashboardFormat.ShortTypeName(typeof(string[,]).AssemblyQualifiedName!).ShouldBe("String[,]");
        DashboardFormat.ShortTypeName("System.Collections.Generic.List`1[System.Int32]").ShouldBe("List<Int32>");
        DashboardFormat.ShortTypeName("Acme.Jobs.RateLoader, Acme.Jobs").ShouldBe("RateLoader");
    }

    [Fact]
    public void Progress_PercentOutsideRange_ClampsDisplayValues()
    {
        var progress = new JobProgressSnapshot(125, "indexed records", DateTimeOffset.Parse("2026-04-20T12:08:00Z", CultureInfo.InvariantCulture));

        DashboardFormat.ProgressPercent(progress).ShouldBe("100%");
        DashboardFormat.ProgressText(progress).ShouldBe("100% indexed records");
        DashboardFormat.ProgressWidthStyle(progress).ShouldBe("width: 100%");
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
    public void RunTime_TerminalJob_FormatsClaimToTerminalDuration()
    {
        var claimedAtUtc = DateTimeOffset.Parse("2026-04-20T12:05:00Z", CultureInfo.InvariantCulture);
        var job = CreateJob() with
        {
            State = JobState.Completed,
            CompletedAtUtc = claimedAtUtc.AddSeconds(72),
        };

        DashboardFormat.RunTime(CreateDetail(job, claimedAtUtc)).ShouldBe("1.2 m");
    }

    [Fact]
    public void RunTime_MissingOrInvalidTimestamps_ReturnsEmptyValue()
    {
        var claimedAtUtc = DateTimeOffset.Parse("2026-04-20T12:05:00Z", CultureInfo.InvariantCulture);
        var terminalJob = CreateJob() with
        {
            State = JobState.Completed,
            CompletedAtUtc = claimedAtUtc.AddSeconds(72),
        };
        var activeJob = CreateJob();
        var invalidJob = terminalJob with
        {
            CompletedAtUtc = claimedAtUtc.AddSeconds(-1),
        };

        DashboardFormat.RunTime(CreateDetail(terminalJob, claimedAtUtc: null)).ShouldBe("-");
        DashboardFormat.RunTime(CreateDetail(activeJob, claimedAtUtc)).ShouldBe("-");
        DashboardFormat.RunTime(CreateDetail(invalidJob, claimedAtUtc)).ShouldBe("-");
    }

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
    public void JobEvent_LogAndTimelineEvents_FormatsOperationalText()
    {
        var failed = new JobEvent(
          Guid.NewGuid(),
          Guid.NewGuid(),
          EventSequence: 4,
          JobEventKind.AttemptFailed,
          DateTimeOffset.Parse("2026-04-20T12:04:00Z", CultureInfo.InvariantCulture),
          AttemptNumber: 2,
          Message: "Timeout");
        var progress = failed with
        {
            Kind = JobEventKind.Progress,
            LogLevel = null,
            Message = "chunk 7",
            ProgressPercent = 70,
        };
        var cancelRequested = failed with
        {
            Kind = JobEventKind.CancelRequested,
            Message = "Cancellation requested",
        };

        DashboardFormat.TimelineTitle(failed).ShouldBe("Attempt 2 Failed");
        DashboardFormat.ShouldRenderTimelineMessage(failed).ShouldBeTrue();
        DashboardFormat.IsLifecycleEvent(cancelRequested).ShouldBeTrue();
        DashboardFormat.TimelineTitle(cancelRequested).ShouldBe("Cancellation Requested");
        DashboardFormat.LogLevel(progress).ShouldBe("PROGRESS");
        DashboardFormat.LogMessage(progress).ShouldBe("70% chunk 7");
    }

    [Fact]
    public void MetricsFormat_RatesDurationsAndActivity_FormatsOperationalValues()
    {
        var inactiveWindow = CreateMetricsWindow();
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

    private static JobInspectionSummary CreateJob(
        string serviceType = "Sheddueller.Dashboard.Tests.Worker",
        string methodName = "Run")
      => new(
        Guid.Parse("8c32d457-9e7a-42bb-8947-0c8fa54743be"),
        JobState.Claimed,
        serviceType,
        methodName,
        Priority: 10,
        EnqueueSequence: 1,
        DateTimeOffset.Parse("2026-04-20T12:00:00Z", CultureInfo.InvariantCulture),
        NotBeforeUtc: null,
        AttemptCount: 2,
        MaxAttempts: 5,
        Tags: [],
        ConcurrencyGroupKeys: [],
        SourceScheduleKey: null,
        LatestProgress: null,
        QueuePosition: null,
        ClaimedAtUtc: null,
        CompletedAtUtc: null,
        FailedAtUtc: null,
        CanceledAtUtc: null);

    private static JobInspectionDetail CreateDetail(
        JobInspectionSummary job,
        DateTimeOffset? claimedAtUtc)
      => new(
        job,
        claimedAtUtc,
        ClaimedByNodeId: null,
        LeaseExpiresAtUtc: null,
        ScheduledFireAtUtc: null);

    private static MetricsInspectionWindow CreateMetricsWindow()
      => new(
        TimeSpan.FromMinutes(5),
        QueuedCount: 0,
        ClaimedCount: 0,
        FailedCount: 0,
        CanceledCount: 0,
        OldestQueuedAge: null,
        EnqueueRatePerMinute: 0,
        ClaimRatePerMinute: 0,
        SuccessRatePerMinute: 0,
        FailureRatePerMinute: 0,
        CancellationRatePerMinute: 0,
        RetryRatePerMinute: 0,
        P50QueueLatency: null,
        P95QueueLatency: null,
        P50ExecutionDuration: null,
        P95ExecutionDuration: null,
        P95ScheduleFireLag: null,
        SaturatedConcurrencyGroupCount: 0,
        ActiveNodeCount: 0,
        StaleNodeCount: 0,
        DeadNodeCount: 0);
}
