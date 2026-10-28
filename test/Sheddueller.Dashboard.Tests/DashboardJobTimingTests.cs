namespace Sheddueller.Dashboard.Tests;

using System.Globalization;

using Sheddueller.Dashboard.Internal;
using Sheddueller.Inspection.Jobs;
using Sheddueller.Storage;

using Shouldly;

public sealed class DashboardJobTimingTests
{
    private static readonly Guid JobId = Guid.Parse("7c1e9a42-5b3d-4f1e-9a07-2d6c8e41b0f3");
    private static readonly DateTimeOffset EnqueuedAtUtc = DateTimeOffset.Parse("2026-04-20T12:00:00Z", CultureInfo.InvariantCulture);

    [Fact]
    public void QueuedJob_NeverStarted_IsStillWaitingAndHasNotRun()
    {
        var timing = DashboardJobTiming.From(Detail(Job(JobState.Queued)), []);

        timing.IsWaiting.ShouldBeTrue();
        timing.Waited.ShouldBeNull();
        timing.RunTime.ShouldBeNull();
        timing.LastRunLabel.ShouldBe("Last run");
        timing.LastRunAtUtc.ShouldBeNull();
    }

    [Fact]
    public void QueuedJob_AfterAFailedAttempt_ShowsThatAttempt()
    {
        var timing = DashboardJobTiming.From(
          Detail(Job(JobState.Queued)),
          [Event(JobEventKind.AttemptStarted, 1, seconds: 30), Event(JobEventKind.AttemptFailed, 1, seconds: 45)]);

        timing.IsWaiting.ShouldBeFalse();
        timing.Waited.ShouldBe(TimeSpan.FromSeconds(30));
        timing.RunTime.ShouldBe(TimeSpan.FromSeconds(15));
        timing.LastRunLabel.ShouldBe("Last attempt");
        timing.LastRunAtUtc.ShouldBe(EnqueuedAtUtc.AddSeconds(45));
    }

    [Fact]
    public void RunningJob_Claimed_HasNoRunTimeUntilItEnds()
    {
        var claimedAtUtc = EnqueuedAtUtc.AddSeconds(5);

        var timing = DashboardJobTiming.From(Detail(Job(JobState.Claimed), claimedAtUtc), []);

        timing.IsRunning.ShouldBeTrue();
        timing.Waited.ShouldBe(TimeSpan.FromSeconds(5));
        timing.RunTime.ShouldBeNull();
        timing.LastRunLabel.ShouldBe("Started");
        timing.LastRunAtUtc.ShouldBe(claimedAtUtc);
    }

    [Fact]
    public void CompletedJob_FirstAttempt_ReportsWaitRunTimeAndCompletion()
    {
        var job = Job(JobState.Completed) with { CompletedAtUtc = EnqueuedAtUtc.AddSeconds(72) };

        var timing = DashboardJobTiming.From(Detail(job, EnqueuedAtUtc.AddSeconds(12)), []);

        timing.Waited.ShouldBe(TimeSpan.FromSeconds(12));
        timing.RunTime.ShouldBe(TimeSpan.FromMinutes(1));
        timing.LastRunLabel.ShouldBe("Completed");
        timing.LastRunAtUtc.ShouldBe(EnqueuedAtUtc.AddSeconds(72));
    }

    [Fact]
    public void CompletedJob_AfterRetries_WaitsFromTheFirstAttemptAndRunsFromTheLast()
    {
        var job = Job(JobState.Completed) with
        {
            FailedAtUtc = EnqueuedAtUtc.AddSeconds(20),
            CompletedAtUtc = EnqueuedAtUtc.AddSeconds(130),
        };

        var timing = DashboardJobTiming.From(
          Detail(job, claimedAtUtc: EnqueuedAtUtc.AddSeconds(120)),
          [
              Event(JobEventKind.AttemptStarted, 1, seconds: 10),
              Event(JobEventKind.AttemptFailed, 1, seconds: 20),
              Event(JobEventKind.AttemptStarted, 2, seconds: 120),
          ]);

        timing.Waited.ShouldBe(TimeSpan.FromSeconds(10));
        timing.RunTime.ShouldBe(TimeSpan.FromSeconds(10));
        timing.LastRunAtUtc.ShouldBe(EnqueuedAtUtc.AddSeconds(130));
    }

    [Fact]
    public void FinishedJob_ClaimNotCarried_FallsBackToAttemptEvents()
    {
        var job = Job(JobState.Failed) with { FailedAtUtc = EnqueuedAtUtc.AddSeconds(50) };

        var timing = DashboardJobTiming.From(
          Detail(job, claimedAtUtc: null),
          [
              Event(JobEventKind.AttemptStarted, 2, seconds: 40),
              Event(JobEventKind.AttemptStarted, 1, seconds: 8),
          ]);

        timing.Waited.ShouldBe(TimeSpan.FromSeconds(8));
        timing.RunTime.ShouldBe(TimeSpan.FromSeconds(10));
        timing.LastRunLabel.ShouldBe("Failed");
    }

    [Fact]
    public void CanceledJob_BeforeStarting_WaitedUntilCanceled()
    {
        var job = Job(JobState.Canceled) with { CanceledAtUtc = EnqueuedAtUtc.AddMinutes(3) };

        var timing = DashboardJobTiming.From(Detail(job), []);

        timing.Waited.ShouldBe(TimeSpan.FromMinutes(3));
        timing.RunTime.ShouldBeNull();
        timing.LastRunLabel.ShouldBe("Canceled");
        timing.LastRunAtUtc.ShouldBe(EnqueuedAtUtc.AddMinutes(3));
    }

    [Fact]
    public void Timestamps_OutOfOrder_GiveNoDuration()
    {
        var job = Job(JobState.Completed) with { CompletedAtUtc = EnqueuedAtUtc.AddSeconds(-5) };

        var timing = DashboardJobTiming.From(Detail(job, claimedAtUtc: EnqueuedAtUtc.AddSeconds(-10)), []);

        timing.Waited.ShouldBeNull();
        timing.RunTime.ShouldBe(TimeSpan.FromSeconds(5));
        DashboardJobTiming.From(Detail(job, claimedAtUtc: EnqueuedAtUtc), []).RunTime.ShouldBeNull();
    }

    private static JobEvent Event(JobEventKind kind, int attempt, int seconds)
      => new(Guid.NewGuid(), JobId, seconds, kind, EnqueuedAtUtc.AddSeconds(seconds), attempt);

    private static JobInspectionDetail Detail(JobInspectionSummary job, DateTimeOffset? claimedAtUtc = null)
      => new(job, claimedAtUtc, ClaimedByNodeId: null, LeaseExpiresAtUtc: null, ScheduledFireAtUtc: null);

    private static JobInspectionSummary Job(JobState state)
      => new(
        JobId,
        state,
        "Sample.Jobs.ReportJob",
        "RunAsync",
        Priority: 0,
        EnqueueSequence: 1,
        EnqueuedAtUtc,
        NotBeforeUtc: null,
        AttemptCount: 1,
        MaxAttempts: 3,
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
