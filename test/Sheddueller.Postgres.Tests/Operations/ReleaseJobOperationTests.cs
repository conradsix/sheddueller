namespace Sheddueller.Postgres.Tests.Operations;

using Sheddueller.Storage;

using Shouldly;

public sealed class ReleaseJobOperationTests(PostgresFixture fixture)
{
    [Fact]
    public async Task ReleaseJob_CurrentLease_RequeuesWithoutRetryConsumptionAndDecrementsGroups()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        var jobId = Guid.NewGuid();
        await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(jobId, groupKeys: ["shared"]));
        var claimed = await PostgresTestData.ClaimAsync(context.Store);

        (await context.Store.ReleaseJobAsync(new ReleaseJobRequest(jobId, "node-1", claimed.LeaseToken, DateTimeOffset.UtcNow))).ShouldBeTrue();

        var job = await context.ReadJobAsync(jobId);
        job.State.ShouldBe("Queued");
        job.AttemptCount.ShouldBe(0);
        job.LeaseToken.ShouldBeNull();
        (await context.ReadConcurrencyGroupAsync("shared")).ShouldNotBeNull().InUseCount.ShouldBe(0);
    }

    [Fact]
    public async Task ReleaseJob_StaleToken_ReturnsFalse()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        var jobId = Guid.NewGuid();
        await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(jobId));
        await PostgresTestData.ClaimAsync(context.Store);

        (await context.Store.ReleaseJobAsync(new ReleaseJobRequest(jobId, "node-1", Guid.NewGuid(), DateTimeOffset.UtcNow))).ShouldBeFalse();

        (await context.ReadJobAsync(jobId)).State.ShouldBe("Claimed");
    }

    [Fact]
    public async Task ReleaseJob_StaleToken_KeepsGroupSlotReserved()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        var jobId = Guid.NewGuid();
        await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(jobId, groupKeys: ["shared"]));
        var claimed = await PostgresTestData.ClaimAsync(context.Store);

        (await context.Store.ReleaseJobAsync(new ReleaseJobRequest(jobId, "node-1", Guid.NewGuid(), DateTimeOffset.UtcNow))).ShouldBeFalse();
        (await context.ReadConcurrencyGroupAsync("shared")).ShouldNotBeNull().InUseCount.ShouldBe(1);

        (await context.Store.ReleaseJobAsync(new ReleaseJobRequest(jobId, "node-1", claimed.LeaseToken, DateTimeOffset.UtcNow))).ShouldBeTrue();
        (await context.ReadConcurrencyGroupAsync("shared")).ShouldNotBeNull().InUseCount.ShouldBe(0);
    }

    [Fact]
    public async Task ReleaseJob_QueuedIdempotentDuplicate_FailsSupersededJobAndDecrementsGroups()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        var running = Guid.NewGuid();
        var next = Guid.NewGuid();
        await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(running, groupKeys: ["shared"], idempotencyKey: "listing:3"));
        var claimed = await PostgresTestData.ClaimAsync(context.Store);
        await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(next, groupKeys: ["shared"], idempotencyKey: "listing:3"));

        (await context.Store.ReleaseJobAsync(new ReleaseJobRequest(running, "node-1", claimed.LeaseToken, DateTimeOffset.UtcNow))).ShouldBeTrue();

        (await context.ReadJobAsync(running)).State.ShouldBe("Failed");
        (await context.ReadJobAsync(next)).State.ShouldBe("Queued");
        (await context.ReadConcurrencyGroupAsync("shared")).ShouldNotBeNull().InUseCount.ShouldBe(0);
    }
}
