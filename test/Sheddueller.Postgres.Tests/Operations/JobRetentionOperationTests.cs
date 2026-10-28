namespace Sheddueller.Postgres.Tests.Operations;

using Sheddueller.Inspection.Jobs;
using Sheddueller.Postgres.Internal;
using Sheddueller.Postgres.Internal.Operations;
using Sheddueller.Storage;

using Shouldly;

public sealed class JobRetentionOperationTests(PostgresFixture fixture)
{
    [Fact]
    public async Task CleanupTerminalJobs_DeletedJob_RemovesCascadingRows()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        var jobId = Guid.NewGuid();
        await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(
          jobId,
          groupKeys: ["group-a"],
          tags: [new JobTag("tenant", "acme")]));
        var claimed = await PostgresTestData.ClaimAsync(context.Store);
        await context.Store.MarkCompletedAsync(new CompleteJobRequest(
          jobId,
          "node-1",
          claimed.LeaseToken,
          DateTimeOffset.UtcNow.AddDays(-2)));

        (await context.ReadJobTagsAsync(jobId)).ShouldNotBeEmpty();
        (await context.ReadJobGroupKeysAsync(jobId)).ShouldNotBeEmpty();
        (await context.CountJobEventsAsync(jobId)).ShouldBeGreaterThan(0);

        var result = await ((IJobRetentionStore)context.Store).CleanupTerminalJobsAsync(
          new JobRetentionCleanupRequest(
            DateTimeOffset.UtcNow.AddDays(-1),
            failedBeforeUtc: null,
            canceledBeforeUtc: null,
            batchSize: 10));

        result.DeletedCount.ShouldBe(1);
        (await ((IJobInspectionReader)context.Store).GetJobAsync(jobId)).ShouldBeNull();
        (await context.ReadJobTagsAsync(jobId)).ShouldBeEmpty();
        (await context.ReadJobGroupKeysAsync(jobId)).ShouldBeEmpty();
        (await context.CountJobEventsAsync(jobId)).ShouldBe(0);
    }

    [Fact]
    public async Task CleanupTerminalJobs_FinishedAfterFailedAttempt_UsesFinalStateTime()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        var now = DateTimeOffset.UtcNow;
        var canceledAfterRetry = await EnqueueAfterOldFailedAttemptAsync(context, now.AddDays(-10));
        (await context.Store.CancelAsync(new CancelJobRequest(canceledAfterRetry, now))).ShouldBe(JobCancellationResult.Canceled);
        var completedAfterRetry = await EnqueueAfterOldFailedAttemptAsync(context, now.AddDays(-10));
        await context.MakeJobDueAsync(completedAfterRetry);
        var retry = await PostgresTestData.ClaimAsync(context.Store);
        retry.JobId.ShouldBe(completedAfterRetry);
        (await context.Store.MarkCompletedAsync(new CompleteJobRequest(completedAfterRetry, "node-1", retry.LeaseToken, now))).ShouldBeTrue();
        var failedLongAgo = Guid.NewGuid();
        await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(failedLongAgo));
        var failedClaim = await PostgresTestData.ClaimAsync(context.Store);
        (await context.Store.MarkFailedAsync(new FailJobRequest(
          failedLongAgo,
          "node-1",
          failedClaim.LeaseToken,
          now.AddDays(-10),
          PostgresTestData.CreateFailure()))).ShouldBeTrue();

        var result = await ((IJobRetentionStore)context.Store).CleanupTerminalJobsAsync(
          new JobRetentionCleanupRequest(now.AddDays(-1), now.AddDays(-1), now.AddDays(-1), batchSize: 10));

        result.DeletedCount.ShouldBe(1);
        (await context.ReadJobAsync(canceledAfterRetry)).State.ShouldBe("Canceled");
        (await context.ReadJobAsync(completedAfterRetry)).State.ShouldBe("Completed");
        (await ((IJobInspectionReader)context.Store).GetJobAsync(failedLongAgo)).ShouldBeNull();
    }

    [Fact]
    public async Task CleanupEvents_CanceledAfterFailedAttempt_KeepsEventsUntilCancellationExpires()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        var now = DateTimeOffset.UtcNow;
        var canceledAfterRetry = await EnqueueAfterOldFailedAttemptAsync(context, now.AddDays(-10));
        (await context.Store.CancelAsync(new CancelJobRequest(canceledAfterRetry, now))).ShouldBe(JobCancellationResult.Canceled);
        var canceledLongAgo = Guid.NewGuid();
        await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(canceledLongAgo));
        (await context.Store.CancelAsync(new CancelJobRequest(canceledLongAgo, now.AddDays(-2)))).ShouldBe(JobCancellationResult.Canceled);
        var retainedEventCount = await context.CountJobEventsAsync(canceledAfterRetry);

        var deleted = await ((IJobEventRetentionStore)context.Store).CleanupAsync(TimeSpan.FromDays(1));

        deleted.ShouldBe(2);
        (await context.CountJobEventsAsync(canceledLongAgo)).ShouldBe(0);
        (await context.CountJobEventsAsync(canceledAfterRetry)).ShouldBe(retainedEventCount);
    }

    [Fact]
    public async Task CleanupEvents_MoreEventsThanOneBatch_DeletesEveryExpiredEventAndNoOthers()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        var now = DateTimeOffset.UtcNow;
        var expired = await CompleteAsync(context, now.AddDays(-2));
        var recent = await CompleteAsync(context, now);
        var queued = Guid.NewGuid();
        await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(queued));
        var logCount = (2 * PostgresJobRetentionOperation.JobEventCleanupBatchSize) + 5;
        await context.InsertLogEventsAsync(expired, logCount);
        await context.InsertLogEventsAsync(recent, 3);
        await context.InsertLogEventsAsync(queued, 3);
        var expiredEventCount = await context.CountJobEventsAsync(expired);
        var recentEventCount = await context.CountJobEventsAsync(recent);
        var queuedEventCount = await context.CountJobEventsAsync(queued);

        var deleted = await ((IJobEventRetentionStore)context.Store).CleanupAsync(TimeSpan.FromDays(1));

        deleted.ShouldBe(expiredEventCount);
        (await context.CountJobEventsAsync(expired)).ShouldBe(0);
        (await context.CountJobEventsAsync(recent)).ShouldBe(recentEventCount);
        (await context.CountJobEventsAsync(queued)).ShouldBe(queuedEventCount);
    }

    [Fact]
    public async Task CleanupEvents_RetentionLockHeld_SkipsUntilTheLockIsFree()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        var expired = await CompleteAsync(context, DateTimeOffset.UtcNow.AddDays(-2));
        var eventCount = await context.CountJobEventsAsync(expired);
        var retentionStore = (IJobEventRetentionStore)context.Store;

        await using (var connection = await context.DataSource.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "select pg_advisory_xact_lock(@lock_key, hashtext(@schema_name));";
                command.Parameters.AddWithValue("lock_key", PostgresAdvisoryLocks.RetentionLockKey);
                command.Parameters.AddWithValue("schema_name", context.SchemaName);
                await command.ExecuteNonQueryAsync();
            }

            (await retentionStore.CleanupAsync(TimeSpan.FromDays(1))).ShouldBe(0);
            (await context.CountJobEventsAsync(expired)).ShouldBe(eventCount);
        }

        (await retentionStore.CleanupAsync(TimeSpan.FromDays(1))).ShouldBe(eventCount);
    }

    private static async ValueTask<Guid> EnqueueAfterOldFailedAttemptAsync(
        PostgresTestContext context,
        DateTimeOffset failedAtUtc)
    {
        var jobId = Guid.NewGuid();
        await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(
          jobId,
          maxAttempts: 2,
          retryBackoffKind: RetryBackoffKind.Fixed,
          retryBaseDelay: TimeSpan.FromHours(1)));
        var attempt = await PostgresTestData.ClaimAsync(context.Store);
        attempt.JobId.ShouldBe(jobId);
        (await context.Store.MarkFailedAsync(new FailJobRequest(
          jobId,
          "node-1",
          attempt.LeaseToken,
          failedAtUtc,
          PostgresTestData.CreateFailure()))).ShouldBeTrue();

        return jobId;
    }

    private static async ValueTask<Guid> CompleteAsync(
        PostgresTestContext context,
        DateTimeOffset completedAtUtc)
    {
        var jobId = Guid.NewGuid();
        await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(jobId));
        var claimed = await PostgresTestData.ClaimAsync(context.Store);
        claimed.JobId.ShouldBe(jobId);
        (await context.Store.MarkCompletedAsync(new CompleteJobRequest(jobId, "node-1", claimed.LeaseToken, completedAtUtc))).ShouldBeTrue();

        return jobId;
    }
}
