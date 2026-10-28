namespace Sheddueller.Postgres.Tests.Operations;

using Sheddueller.Storage;

using Shouldly;

public sealed class RecoverExpiredLeasesOperationTests(PostgresFixture fixture)
{
    [Fact]
    public async Task RecoverExpiredLeases_NoExpiredClaims_ReturnsZero()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(Guid.NewGuid()));

        (await context.Store.RecoverExpiredLeasesAsync(new RecoverExpiredLeasesRequest(DateTimeOffset.UtcNow))).ShouldBe(0);
    }

    [Fact]
    public async Task RecoverExpiredLeases_ExpiredClaimWithRetry_RequeuesAndDecrementsGroups()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        var jobId = Guid.NewGuid();
        await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(
          jobId,
          maxAttempts: 2,
          retryBackoffKind: RetryBackoffKind.Fixed,
          retryBaseDelay: TimeSpan.FromMilliseconds(1),
          groupKeys: ["shared"]));
        await PostgresTestData.ClaimAsync(context.Store);
        await context.ForceClaimExpiredAsync(jobId);

        (await context.Store.RecoverExpiredLeasesAsync(new RecoverExpiredLeasesRequest(DateTimeOffset.UtcNow))).ShouldBe(1);

        var job = await context.ReadJobAsync(jobId);
        job.State.ShouldBe("Queued");
        job.AttemptCount.ShouldBe(1);
        job.NotBeforeUtc.ShouldNotBeNull();
        (await context.ReadConcurrencyGroupAsync("shared")).ShouldNotBeNull().InUseCount.ShouldBe(0);
    }

    [Fact]
    public async Task RecoverExpiredLeases_RetryWithQueuedIdempotentReplacement_FailsExpiredOriginalAndKeepsReplacement()
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
        await PostgresTestData.ClaimAsync(context.Store);
        await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(replacement, idempotencyKey: "listing:3"));
        await context.ForceClaimExpiredAsync(running);

        (await context.Store.RecoverExpiredLeasesAsync(new RecoverExpiredLeasesRequest(DateTimeOffset.UtcNow))).ShouldBe(1);

        var failed = await context.ReadJobAsync(running);
        failed.State.ShouldBe("Failed");
        failed.FailedAtUtc.ShouldNotBeNull();
        failed.NotBeforeUtc.ShouldBeNull();
        failed.LeaseToken.ShouldBeNull();
        failed.ClaimedByNodeId.ShouldBeNull();
        failed.FailureTypeName.ShouldBe("Sheddueller.LeaseExpired");
        failed.FailureMessage.ShouldBe("The job lease expired before the owning node renewed it.");
        (await context.ReadConcurrencyGroupAsync("shared")).ShouldNotBeNull().InUseCount.ShouldBe(0);

        var queuedReplacement = await context.ReadJobAsync(replacement);
        queuedReplacement.State.ShouldBe("Queued");
        queuedReplacement.IdempotencyKey.ShouldBe("listing:3");
        (await context.CountQueuedJobsWithIdempotencyKeyAsync("listing:3")).ShouldBe(1);

        var events = await context.ReadJobEventsAsync(running);
        events.Count(jobEvent => jobEvent.Kind == JobEventKind.AttemptFailed
          && jobEvent.Message == "The job lease expired before the owning node renewed it.").ShouldBe(1);
        events.Count(jobEvent => jobEvent.Kind == JobEventKind.Lifecycle
          && jobEvent.Message is not null
          && jobEvent.Message.Contains("superseded by queued idempotent job", StringComparison.Ordinal)).ShouldBe(1);
    }

    [Fact]
    public async Task RecoverExpiredLeases_ExpiredClaimWithoutRetry_FailsTerminally()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        var jobId = Guid.NewGuid();
        await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(jobId));
        await PostgresTestData.ClaimAsync(context.Store);
        await context.ForceClaimExpiredAsync(jobId);

        (await context.Store.RecoverExpiredLeasesAsync(new RecoverExpiredLeasesRequest(DateTimeOffset.UtcNow))).ShouldBe(1);

        var job = await context.ReadJobAsync(jobId);
        job.State.ShouldBe("Failed");
        job.FailureTypeName.ShouldBe("Sheddueller.LeaseExpired");
    }
}
