namespace Sheddueller.Dashboard.Tests;

using Sheddueller.Dashboard.Internal;
using Sheddueller.Inspection.Jobs;
using Sheddueller.Storage;

using Shouldly;

public sealed class DashboardJobTimingTests
{
    [Fact]
    public void QueuedJob_NeverStarted_IsStillWaitingAndHasNotRun()
    {
        var timing = DashboardJobTiming.From(Detail(TestData.Job(state: JobState.Queued)), []);

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
          Detail(TestData.Job(state: JobState.Queued)),
          [TestData.Event(30, JobEventKind.AttemptStarted, 1), TestData.Event(45, JobEventKind.AttemptFailed, 1)]);

        timing.IsWaiting.ShouldBeFalse();
        timing.Waited.ShouldBe(TimeSpan.FromSeconds(30));
        timing.RunTime.ShouldBe(TimeSpan.FromSeconds(15));
        timing.LastRunLabel.ShouldBe("Last attempt");
        timing.LastRunAtUtc.ShouldBe(TestData.EnqueuedAtUtc.AddSeconds(45));
    }

    [Fact]
    public void RunningJob_Claimed_HasNoRunTimeUntilItEnds()
    {
        var claimedAtUtc = TestData.EnqueuedAtUtc.AddSeconds(5);

        var timing = DashboardJobTiming.From(Detail(TestData.Job(state: JobState.Claimed), claimedAtUtc), []);

        timing.IsRunning.ShouldBeTrue();
        timing.Waited.ShouldBe(TimeSpan.FromSeconds(5));
        timing.RunTime.ShouldBeNull();
        timing.LastRunLabel.ShouldBe("Started");
        timing.LastRunAtUtc.ShouldBe(claimedAtUtc);
    }

    [Fact]
    public void CompletedJob_FirstAttempt_ReportsWaitRunTimeAndCompletion()
    {
        var job = TestData.Job(state: JobState.Completed) with { CompletedAtUtc = TestData.EnqueuedAtUtc.AddSeconds(72) };

        var timing = DashboardJobTiming.From(Detail(job, TestData.EnqueuedAtUtc.AddSeconds(12)), []);

        timing.Waited.ShouldBe(TimeSpan.FromSeconds(12));
        timing.RunTime.ShouldBe(TimeSpan.FromMinutes(1));
        timing.LastRunLabel.ShouldBe("Completed");
        timing.LastRunAtUtc.ShouldBe(TestData.EnqueuedAtUtc.AddSeconds(72));
    }

    [Fact]
    public void CompletedJob_AfterRetries_WaitsFromTheFirstAttemptAndRunsFromTheLast()
    {
        var job = TestData.Job(state: JobState.Completed) with
        {
            FailedAtUtc = TestData.EnqueuedAtUtc.AddSeconds(20),
            CompletedAtUtc = TestData.EnqueuedAtUtc.AddSeconds(130),
        };

        var timing = DashboardJobTiming.From(
          Detail(job, claimedAtUtc: TestData.EnqueuedAtUtc.AddSeconds(120)),
          [
              TestData.Event(10, JobEventKind.AttemptStarted, 1),
              TestData.Event(20, JobEventKind.AttemptFailed, 1),
              TestData.Event(120, JobEventKind.AttemptStarted, 2),
          ]);

        timing.Waited.ShouldBe(TimeSpan.FromSeconds(10));
        timing.RunTime.ShouldBe(TimeSpan.FromSeconds(10));
        timing.LastRunAtUtc.ShouldBe(TestData.EnqueuedAtUtc.AddSeconds(130));
    }

    [Fact]
    public void FinishedJob_ClaimNotCarried_FallsBackToAttemptEvents()
    {
        var job = TestData.Job(state: JobState.Failed) with { FailedAtUtc = TestData.EnqueuedAtUtc.AddSeconds(50) };

        var timing = DashboardJobTiming.From(
          Detail(job, claimedAtUtc: null),
          [
              TestData.Event(40, JobEventKind.AttemptStarted, 2),
              TestData.Event(8, JobEventKind.AttemptStarted, 1),
          ]);

        timing.Waited.ShouldBe(TimeSpan.FromSeconds(8));
        timing.RunTime.ShouldBe(TimeSpan.FromSeconds(10));
        timing.LastRunLabel.ShouldBe("Failed");
    }

    [Fact]
    public void CanceledJob_BeforeStarting_WaitedUntilCanceled()
    {
        var job = TestData.Job(state: JobState.Canceled) with { CanceledAtUtc = TestData.EnqueuedAtUtc.AddMinutes(3) };

        var timing = DashboardJobTiming.From(Detail(job), []);

        timing.Waited.ShouldBe(TimeSpan.FromMinutes(3));
        timing.RunTime.ShouldBeNull();
        timing.LastRunLabel.ShouldBe("Canceled");
        timing.LastRunAtUtc.ShouldBe(TestData.EnqueuedAtUtc.AddMinutes(3));
    }

    [Fact]
    public void Timestamps_OutOfOrder_GiveNoDuration()
    {
        var job = TestData.Job(state: JobState.Completed) with { CompletedAtUtc = TestData.EnqueuedAtUtc.AddSeconds(-5) };

        var timing = DashboardJobTiming.From(Detail(job, claimedAtUtc: TestData.EnqueuedAtUtc.AddSeconds(-10)), []);

        timing.Waited.ShouldBeNull();
        timing.RunTime.ShouldBe(TimeSpan.FromSeconds(5));
        DashboardJobTiming.From(Detail(job, claimedAtUtc: TestData.EnqueuedAtUtc), []).RunTime.ShouldBeNull();
    }

    private static JobInspectionDetail Detail(JobInspectionSummary job, DateTimeOffset? claimedAtUtc = null)
      => new(job, claimedAtUtc, ClaimedByNodeId: null, LeaseExpiresAtUtc: null, ScheduledFireAtUtc: null);
}
