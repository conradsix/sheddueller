namespace Sheddueller.Dashboard.Tests;

using System.Globalization;

using Sheddueller.Dashboard.Internal;
using Sheddueller.Inspection.Jobs;
using Sheddueller.Storage;

using Shouldly;

public sealed class DashboardJobDetailFormatTests
{
    private static readonly Guid JobId = Guid.Parse("7c1e9a42-5b3d-4f1e-9a07-2d6c8e41b0f3");
    private static readonly DateTimeOffset EnqueuedAtUtc = DateTimeOffset.Parse("2026-04-20T12:00:00Z", CultureInfo.InvariantCulture);

    [Theory]
    [InlineData(null, "Logs")]
    [InlineData("", "Logs")]
    [InlineData("logs", "Logs")]
    [InlineData("Timeline", "Timeline")]
    [InlineData(" INVOCATION ", "Invocation")]
    [InlineData("related", "Related")]
    [InlineData("1", "Logs")]
    [InlineData("history", "Logs")]
    public void Tab_QueryValue_OpensNamedTabOrLogs(string? value, string expected)
        => DashboardJobDetailFormat.ParseTab(value).ToString().ShouldBe(expected);

    [Fact]
    public void TabLink_Logs_IsTheBareJobLink()
        => DashboardJobDetailFormat.TabHref(JobId, DashboardJobDetailTab.Logs).ShouldBe("jobs/7c1e9a42-5b3d-4f1e-9a07-2d6c8e41b0f3");

    [Fact]
    public void TabLink_OtherTab_CarriesTheTabName()
        => DashboardJobDetailFormat.TabHref(JobId, DashboardJobDetailTab.Related)
          .ShouldBe("jobs/7c1e9a42-5b3d-4f1e-9a07-2d6c8e41b0f3?tab=related");

    [Theory]
    [InlineData("jobs")]
    [InlineData("jobs?take=50")]
    [InlineData("jobs?view=shared%3Aops")]
    public void BackLink_UnfilteredList_HasNoContext(string href)
        => DashboardJobDetailFormat.JobsListContext(href).ShouldBeNull();

    [Fact]
    public void BackLink_FilteredList_DescribesTheFilters()
        => DashboardJobDetailFormat.JobsListContext("jobs?state=Failed&state=Claimed&handler=Report&tag=tenant%3Aacme&group=billing")
          .ShouldBe("Running, Failed · handler “Report” · tag “tenant:acme” · group “billing”");

    [Fact]
    public void Failure_SeveralFailedAttempts_IsTheLatest()
    {
        JobEvent[] events =
        [
            CreateEvent(1, JobEventKind.AttemptFailed, 1, "timeout"),
            CreateEvent(2, JobEventKind.AttemptStarted, 2, "Attempt started"),
            CreateEvent(3, JobEventKind.AttemptFailed, 2, "connection refused"),
            CreateEvent(4, JobEventKind.Lifecycle, 2, "Failed"),
        ];

        DashboardJobDetailFormat.LatestFailure(events)?.Message.ShouldBe("connection refused");
    }

    [Fact]
    public void Failure_NoFailedAttempt_IsMissing()
        => DashboardJobDetailFormat.LatestFailure([CreateEvent(1, JobEventKind.Lifecycle, 0, "Queued")]).ShouldBeNull();

    [Theory]
    [InlineData(JobEventKind.Lifecycle, "Queued", "Queued", "queued")]
    [InlineData(JobEventKind.Lifecycle, "Retry scheduled", "Retry scheduled", "waiting")]
    [InlineData(JobEventKind.Lifecycle, "Failed; superseded by queued idempotent job", "Failed; superseded by queued idempotent job", "failed")]
    [InlineData(JobEventKind.Lifecycle, "Released", "Released", "neutral")]
    [InlineData(JobEventKind.AttemptStarted, "Attempt started", "Attempt 2 started", "claimed")]
    [InlineData(JobEventKind.AttemptCompleted, "Attempt completed", "Attempt 2 completed", "completed")]
    [InlineData(JobEventKind.AttemptFailed, "timeout", "Attempt 2 failed", "failed")]
    [InlineData(JobEventKind.CancelRequested, "Cancellation requested", "Cancellation requested", "waiting")]
    [InlineData(JobEventKind.CancelObserved, "Cancellation observed", "Worker stopped for cancellation", "waiting")]
    public void Timeline_EventKind_HasTitleAndTone(JobEventKind kind, string message, string title, string tone)
    {
        var jobEvent = CreateEvent(1, kind, 2, message);

        DashboardJobDetailFormat.TimelineTitle(jobEvent).ShouldBe(title);
        DashboardJobDetailFormat.TimelineTone(jobEvent).ShouldBe(tone);
    }

    [Fact]
    public void Timeline_FailedAttempt_ShowsFailureMessage()
    {
        DashboardJobDetailFormat.TimelineDetail(CreateEvent(1, JobEventKind.AttemptFailed, 1, "timeout")).ShouldBe("timeout");
        DashboardJobDetailFormat.TimelineDetail(CreateEvent(2, JobEventKind.AttemptStarted, 1, "Attempt started")).ShouldBeNull();
    }

    [Fact]
    public void Attempts_NotStarted_DescribesTheLimit()
    {
        DashboardJobDetailFormat.Attempts(CreateJob(attemptCount: 0, maxAttempts: 3)).ShouldBe("Not started · up to 3 attempts");
        DashboardJobDetailFormat.Attempts(CreateJob(attemptCount: 0, maxAttempts: 1)).ShouldBe("Not started · up to 1 attempt");
        DashboardJobDetailFormat.Attempts(CreateJob(attemptCount: 2, maxAttempts: 3)).ShouldBe("Attempt 2 of 3");
    }

    [Theory]
    [InlineData(JobState.Completed, 1, 3, "job-detail-attempt")]
    [InlineData(JobState.Claimed, 2, 3, "job-detail-attempt job-detail-attempt--retried")]
    [InlineData(JobState.Completed, 3, 3, "job-detail-attempt job-detail-attempt--retried")]
    [InlineData(JobState.Failed, 3, 3, "job-detail-attempt job-detail-attempt--exhausted")]
    [InlineData(JobState.Failed, 1, 1, "job-detail-attempt job-detail-attempt--exhausted")]
    public void AttemptPill_RetriesAndExhaustion_AreHighlighted(JobState state, int attemptCount, int maxAttempts, string expected)
        => DashboardJobDetailFormat.AttemptClass(CreateJob(attemptCount, maxAttempts) with { State = state }).ShouldBe(expected);

    [Theory]
    [InlineData(JobQueuePositionKind.Claimable, 4L, "Waiting for a worker · #4 in the queue")]
    [InlineData(JobQueuePositionKind.Claimable, null, "Ready for a worker")]
    [InlineData(JobQueuePositionKind.Delayed, null, "Scheduled to run later")]
    [InlineData(JobQueuePositionKind.RetryWaiting, null, "Waiting to retry")]
    [InlineData(JobQueuePositionKind.WaitingForDependencies, null, "Waiting for prerequisites")]
    [InlineData(JobQueuePositionKind.BlockedByConcurrency, null, "Blocked by a concurrency group")]
    public void QueuedJob_QueuePosition_ExplainsWhyItIsNotRunning(JobQueuePositionKind kind, long? position, string expected)
    {
        var job = CreateJob(attemptCount: 0, maxAttempts: 3) with
        {
            QueuePosition = new JobQueuePosition(JobId, kind, position),
        };

        DashboardJobDetailFormat.QueueTitle(job).ShouldBe(expected);
    }

    private static JobEvent CreateEvent(
        long sequence,
        JobEventKind kind,
        int attempt,
        string message)
      => new(Guid.NewGuid(), JobId, sequence, kind, EnqueuedAtUtc.AddSeconds(sequence), attempt, Message: message);

    private static JobInspectionSummary CreateJob(
        int attemptCount,
        int maxAttempts)
      => new(
        JobId,
        JobState.Queued,
        "Sample.Jobs.ReportJob",
        "RunAsync",
        Priority: 0,
        EnqueueSequence: 1,
        EnqueuedAtUtc,
        NotBeforeUtc: null,
        attemptCount,
        maxAttempts,
        Tags: [],
        ConcurrencyGroupKeys: [],
        SourceScheduleKey: null,
        LatestProgress: null,
        QueuePosition: null,
        ClaimedAtUtc: null,
        CompletedAtUtc: null,
        FailedAtUtc: null,
        CanceledAtUtc: null);
}
