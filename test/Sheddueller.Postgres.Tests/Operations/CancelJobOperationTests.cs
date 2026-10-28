namespace Sheddueller.Postgres.Tests.Operations;

using System.Globalization;

using Sheddueller.Inspection.Jobs;
using Sheddueller.Storage;

using Shouldly;

public sealed class CancelJobOperationTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Cancel_QueuedJob_MarksCanceled()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        var jobId = Guid.NewGuid();
        await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(jobId));

        (await context.Store.CancelAsync(new CancelJobRequest(jobId, DateTimeOffset.UtcNow))).ShouldBe(JobCancellationResult.Canceled);

        var job = await context.ReadJobAsync(jobId);
        job.State.ShouldBe("Canceled");
        job.CanceledAtUtc.ShouldNotBeNull();
    }

    [Fact]
    public async Task Cancel_ClaimedJob_RequestsCooperativeCancellation()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        var jobId = Guid.NewGuid();
        await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(jobId));
        var claimed = await PostgresTestData.ClaimAsync(context.Store);

        (await context.Store.CancelAsync(new CancelJobRequest(jobId, DateTimeOffset.UtcNow))).ShouldBe(JobCancellationResult.CancellationRequested);

        var job = await context.ReadJobAsync(jobId);
        job.State.ShouldBe("Claimed");
        job.CancellationRequestedAtUtc.ShouldNotBeNull();
        (await context.Store.GetCancellationRequestedAtAsync(new JobCancellationStatusRequest(jobId, "node-1", claimed.LeaseToken, DateTimeOffset.UtcNow))).ShouldNotBeNull();

        var events = await ReadEventsAsync(context, jobId);
        events.Count(jobEvent => jobEvent.Kind == JobEventKind.CancelRequested).ShouldBe(1);

        (await context.Store.CancelAsync(new CancelJobRequest(jobId, DateTimeOffset.UtcNow))).ShouldBe(JobCancellationResult.CancellationRequested);
        events = await ReadEventsAsync(context, jobId);
        events.Count(jobEvent => jobEvent.Kind == JobEventKind.CancelRequested).ShouldBe(1);
    }

    [Fact]
    public async Task Cancel_TerminalOrMissingJob_ReturnsExpectedResult()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        var completed = Guid.NewGuid();
        var canceled = Guid.NewGuid();

        await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(completed));
        var claimed = await PostgresTestData.ClaimAsync(context.Store);
        (await context.Store.MarkCompletedAsync(new CompleteJobRequest(completed, "node-1", claimed.LeaseToken, DateTimeOffset.UtcNow))).ShouldBeTrue();

        await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(canceled));
        (await context.Store.CancelAsync(new CancelJobRequest(canceled, DateTimeOffset.UtcNow))).ShouldBe(JobCancellationResult.Canceled);

        (await context.Store.CancelAsync(new CancelJobRequest(completed, DateTimeOffset.UtcNow))).ShouldBe(JobCancellationResult.AlreadyFinished);
        (await context.Store.CancelAsync(new CancelJobRequest(canceled, DateTimeOffset.UtcNow))).ShouldBe(JobCancellationResult.AlreadyFinished);
        (await context.Store.CancelAsync(new CancelJobRequest(Guid.NewGuid(), DateTimeOffset.UtcNow))).ShouldBe(JobCancellationResult.NotFound);
    }

    [Fact]
    public async Task CancelQueuedJobs_QueuedState_MarksCanceledAndAppendsLifecycleEvents()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        var claimed = Guid.NewGuid();
        var retryWaiting = Guid.NewGuid();
        var completed = Guid.NewGuid();
        var failed = Guid.NewGuid();
        var alreadyCanceled = Guid.NewGuid();
        var claimable = Guid.NewGuid();
        var delayed = Guid.NewGuid();
        var canceledAtUtc = DateTimeOffset.Parse("2026-04-20T12:30:00Z", CultureInfo.InvariantCulture);

        await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(claimed));
        await PostgresTestData.ClaimAsync(context.Store);

        await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(
          retryWaiting,
          maxAttempts: 2,
          retryBackoffKind: RetryBackoffKind.Fixed,
          retryBaseDelay: TimeSpan.FromHours(1)));
        var retryClaim = await PostgresTestData.ClaimAsync(context.Store);
        (await context.Store.MarkFailedAsync(new FailJobRequest(retryWaiting, "node-1", retryClaim.LeaseToken, DateTimeOffset.UtcNow, PostgresTestData.CreateFailure()))).ShouldBeTrue();

        await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(completed));
        var completedClaim = await PostgresTestData.ClaimAsync(context.Store);
        (await context.Store.MarkCompletedAsync(new CompleteJobRequest(completed, "node-1", completedClaim.LeaseToken, DateTimeOffset.UtcNow))).ShouldBeTrue();

        await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(failed));
        var failedClaim = await PostgresTestData.ClaimAsync(context.Store);
        (await context.Store.MarkFailedAsync(new FailJobRequest(failed, "node-1", failedClaim.LeaseToken, DateTimeOffset.UtcNow, PostgresTestData.CreateFailure()))).ShouldBeTrue();

        await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(alreadyCanceled));
        (await context.Store.CancelAsync(new CancelJobRequest(alreadyCanceled, DateTimeOffset.UtcNow))).ShouldBe(JobCancellationResult.Canceled);

        await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(claimable));
        await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(delayed, notBeforeUtc: DateTimeOffset.UtcNow.AddHours(1)));

        (await context.Store.CancelQueuedJobsAsync(new CancelQueuedJobsRequest(canceledAtUtc))).ShouldBe(3);

        var claimableJob = await context.ReadJobAsync(claimable);
        claimableJob.State.ShouldBe("Canceled");
        claimableJob.CanceledAtUtc.ShouldBe(canceledAtUtc);
        var delayedJob = await context.ReadJobAsync(delayed);
        delayedJob.State.ShouldBe("Canceled");
        delayedJob.CanceledAtUtc.ShouldBe(canceledAtUtc);
        var retryJob = await context.ReadJobAsync(retryWaiting);
        retryJob.State.ShouldBe("Canceled");
        retryJob.CanceledAtUtc.ShouldBe(canceledAtUtc);
        (await context.ReadJobAsync(claimed)).State.ShouldBe("Claimed");
        (await context.ReadJobAsync(completed)).State.ShouldBe("Completed");
        (await context.ReadJobAsync(failed)).State.ShouldBe("Failed");
        (await context.ReadJobAsync(alreadyCanceled)).State.ShouldBe("Canceled");

        (await ReadEventsAsync(context, claimable)).Count(IsCanceledLifecycleEvent).ShouldBe(1);
        (await ReadEventsAsync(context, delayed)).Count(IsCanceledLifecycleEvent).ShouldBe(1);
        (await ReadEventsAsync(context, retryWaiting)).Count(IsCanceledLifecycleEvent).ShouldBe(1);
        (await ReadEventsAsync(context, alreadyCanceled)).Count(IsCanceledLifecycleEvent).ShouldBe(1);
    }

    private static async ValueTask<IReadOnlyList<JobEvent>> ReadEventsAsync(
        PostgresTestContext context,
        Guid jobId)
    {
        var events = new List<JobEvent>();
        await foreach (var jobEvent in context.InspectionReader.ReadEventsAsync(jobId))
        {
            events.Add(jobEvent);
        }

        return events;
    }

    private static bool IsCanceledLifecycleEvent(JobEvent jobEvent)
      => jobEvent.Kind == JobEventKind.Lifecycle
        && string.Equals(jobEvent.Message, "Canceled", StringComparison.Ordinal);
}
