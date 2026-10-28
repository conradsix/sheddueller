namespace Sheddueller.Postgres.Tests.Operations;

using Sheddueller.Storage;

using Shouldly;

public sealed class MarkJobFailedOperationTests(PostgresFixture fixture)
{
    [Fact]
    public async Task MarkFailed_NoRetryPolicy_FailsTerminallyAndDecrementsGroups()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        var jobId = Guid.NewGuid();
        await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(jobId, groupKeys: ["shared"]));
        var claimed = await PostgresTestData.ClaimAsync(context.Store);

        (await context.Store.MarkFailedAsync(new FailJobRequest(jobId, "node-1", claimed.LeaseToken, DateTimeOffset.UtcNow, PostgresTestData.CreateFailure()))).ShouldBeTrue();

        var job = await context.ReadJobAsync(jobId);
        job.State.ShouldBe("Failed");
        job.FailedAtUtc.ShouldNotBeNull();
        job.FailureTypeName.ShouldBe("TestException");
        job.FailureMessage.ShouldBe("failed");
        job.FailureStackTrace.ShouldBe("stack");
        (await context.ReadConcurrencyGroupAsync("shared")).ShouldNotBeNull().InUseCount.ShouldBe(0);
    }

    [Fact]
    public async Task MarkFailed_RetryPolicy_RequeuesWithRetryDelay()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        var jobId = Guid.NewGuid();
        await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(
          jobId,
          maxAttempts: 2,
          retryBackoffKind: RetryBackoffKind.Fixed,
          retryBaseDelay: TimeSpan.FromMilliseconds(500)));
        var claimed = await PostgresTestData.ClaimAsync(context.Store);

        (await context.Store.MarkFailedAsync(new FailJobRequest(jobId, "node-1", claimed.LeaseToken, DateTimeOffset.UtcNow, PostgresTestData.CreateFailure()))).ShouldBeTrue();

        var job = await context.ReadJobAsync(jobId);
        job.State.ShouldBe("Queued");
        job.AttemptCount.ShouldBe(1);
        job.NotBeforeUtc.ShouldNotBeNull();
        job.LeaseToken.ShouldBeNull();
    }

    [Fact]
    public async Task MarkFailed_RetryPolicy_SecondFailureFailsTerminally()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        var jobId = Guid.NewGuid();
        await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(
          jobId,
          maxAttempts: 2,
          retryBackoffKind: RetryBackoffKind.Fixed,
          retryBaseDelay: TimeSpan.FromMilliseconds(10)));
        var firstClaim = await PostgresTestData.ClaimAsync(context.Store);
        (await context.Store.MarkFailedAsync(new FailJobRequest(jobId, "node-1", firstClaim.LeaseToken, DateTimeOffset.UtcNow, PostgresTestData.CreateFailure()))).ShouldBeTrue();
        await context.MakeJobDueAsync(jobId);
        var secondClaim = await PostgresTestData.ClaimAsync(context.Store);

        (await context.Store.MarkFailedAsync(new FailJobRequest(jobId, "node-1", secondClaim.LeaseToken, DateTimeOffset.UtcNow, PostgresTestData.CreateFailure()))).ShouldBeTrue();

        var job = await context.ReadJobAsync(jobId);
        job.State.ShouldBe("Failed");
        job.AttemptCount.ShouldBe(2);
        job.NotBeforeUtc.ShouldBeNull();
        job.LeaseToken.ShouldBeNull();
        job.FailureTypeName.ShouldBe("TestException");
        job.FailureMessage.ShouldBe("failed");
    }

    [Fact]
    public async Task MarkFailed_ExponentialBackoffWithMaxDelay_CapsRetryDelay()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        var jobId = Guid.NewGuid();
        await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(
          jobId,
          maxAttempts: 3,
          retryBackoffKind: RetryBackoffKind.Exponential,
          retryBaseDelay: TimeSpan.FromMilliseconds(20),
          retryMaxDelay: TimeSpan.FromMilliseconds(30)));
        var firstClaim = await PostgresTestData.ClaimAsync(context.Store);
        (await context.Store.MarkFailedAsync(new FailJobRequest(jobId, "node-1", firstClaim.LeaseToken, DateTimeOffset.UtcNow, PostgresTestData.CreateFailure()))).ShouldBeTrue();
        await context.MakeJobDueAsync(jobId);
        var secondClaim = await PostgresTestData.ClaimAsync(context.Store);

        (await context.Store.MarkFailedAsync(new FailJobRequest(jobId, "node-1", secondClaim.LeaseToken, DateTimeOffset.UtcNow, PostgresTestData.CreateFailure()))).ShouldBeTrue();

        var job = await context.ReadJobAsync(jobId);
        job.State.ShouldBe("Queued");
        job.AttemptCount.ShouldBe(2);
        var retryDelay = job.NotBeforeUtc.ShouldNotBeNull() - job.FailedAtUtc.ShouldNotBeNull();
        retryDelay.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(25));
        retryDelay.ShouldBeLessThanOrEqualTo(TimeSpan.FromMilliseconds(35));
    }

    [Fact]
    public async Task MarkFailed_RetryWithQueuedIdempotentReplacement_FailsOriginalWithExpectedFieldsAndEvents()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        var running = Guid.NewGuid();
        var replacement = Guid.NewGuid();
        await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(
          running,
          maxAttempts: 2,
          retryBackoffKind: RetryBackoffKind.Fixed,
          retryBaseDelay: TimeSpan.FromHours(1),
          groupKeys: ["shared"],
          idempotencyKey: "listing:3"));
        var claimed = await PostgresTestData.ClaimAsync(context.Store);
        await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(replacement, idempotencyKey: "listing:3"));

        (await context.Store.MarkFailedAsync(new FailJobRequest(running, "node-1", claimed.LeaseToken, DateTimeOffset.UtcNow, PostgresTestData.CreateFailure()))).ShouldBeTrue();

        var failed = await context.ReadJobAsync(running);
        failed.State.ShouldBe("Failed");
        failed.FailedAtUtc.ShouldNotBeNull();
        failed.NotBeforeUtc.ShouldBeNull();
        failed.LeaseToken.ShouldBeNull();
        failed.ClaimedByNodeId.ShouldBeNull();
        failed.FailureTypeName.ShouldBe("TestException");
        failed.FailureMessage.ShouldBe("failed");
        failed.FailureStackTrace.ShouldBe("stack");
        (await context.ReadConcurrencyGroupAsync("shared")).ShouldNotBeNull().InUseCount.ShouldBe(0);

        var queuedReplacement = await context.ReadJobAsync(replacement);
        queuedReplacement.State.ShouldBe("Queued");
        queuedReplacement.IdempotencyKey.ShouldBe("listing:3");
        (await context.CountQueuedJobsWithIdempotencyKeyAsync("listing:3")).ShouldBe(1);

        var events = await context.ReadJobEventsAsync(running);
        events.Count(jobEvent => jobEvent.Kind == JobEventKind.AttemptFailed && jobEvent.Message == "failed").ShouldBe(1);
        events.Count(jobEvent => jobEvent.Kind == JobEventKind.Lifecycle
          && jobEvent.Message is not null
          && jobEvent.Message.Contains("superseded by queued idempotent job", StringComparison.Ordinal)).ShouldBe(1);
    }

    [Fact]
    public async Task MarkFailed_RetryRacingSameKeyEnqueue_LeavesOneQueuedRepresentative()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        var running = Guid.NewGuid();
        var contender = Guid.NewGuid();
        await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(
          running,
          maxAttempts: 2,
          retryBackoffKind: RetryBackoffKind.Fixed,
          retryBaseDelay: TimeSpan.FromHours(1),
          idempotencyKey: "listing:race"));
        var claimed = await PostgresTestData.ClaimAsync(context.Store);

        var failureTask = context.Store.MarkFailedAsync(
          new FailJobRequest(running, "node-1", claimed.LeaseToken, DateTimeOffset.UtcNow, PostgresTestData.CreateFailure()))
          .AsTask();
        var enqueueTask = context.Store.EnqueueAsync(PostgresTestData.CreateRequest(contender, idempotencyKey: "listing:race")).AsTask();
        await Task.WhenAll(failureTask, enqueueTask);

        (await failureTask).ShouldBeTrue();
        (await context.CountQueuedJobsWithIdempotencyKeyAsync("listing:race")).ShouldBe(1);
    }

    [Fact]
    public async Task MarkFailed_StaleToken_ReturnsFalse()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        var jobId = Guid.NewGuid();
        await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(jobId));
        await PostgresTestData.ClaimAsync(context.Store);

        (await context.Store.MarkFailedAsync(new FailJobRequest(jobId, "node-1", Guid.NewGuid(), DateTimeOffset.UtcNow, PostgresTestData.CreateFailure()))).ShouldBeFalse();

        (await context.ReadJobAsync(jobId)).State.ShouldBe("Claimed");
    }
}
