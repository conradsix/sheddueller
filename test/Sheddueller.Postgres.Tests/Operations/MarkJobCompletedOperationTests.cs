namespace Sheddueller.Postgres.Tests.Operations;

using Sheddueller.Storage;

using Shouldly;

public sealed class MarkJobCompletedOperationTests(PostgresFixture fixture)
{
    [Fact]
    public async Task MarkCompleted_CurrentLease_CompletesJobAndDecrementsGroups()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        var jobId = Guid.NewGuid();
        await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(jobId, groupKeys: ["shared"]));
        var claimed = await PostgresTestData.ClaimAsync(context.Store);

        (await context.Store.MarkCompletedAsync(new CompleteJobRequest(jobId, "node-1", claimed.LeaseToken, DateTimeOffset.UtcNow))).ShouldBeTrue();

        var job = await context.ReadJobAsync(jobId);
        job.State.ShouldBe("Completed");
        job.CompletedAtUtc.ShouldNotBeNull();
        (await context.ReadConcurrencyGroupAsync("shared")).ShouldNotBeNull().InUseCount.ShouldBe(0);
    }

    [Fact]
    public async Task MarkCompleted_StaleToken_ReturnsFalseWithoutChangingJob()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        var jobId = Guid.NewGuid();
        await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(jobId));
        await PostgresTestData.ClaimAsync(context.Store);

        (await context.Store.MarkCompletedAsync(new CompleteJobRequest(jobId, "node-1", Guid.NewGuid(), DateTimeOffset.UtcNow))).ShouldBeFalse();

        (await context.ReadJobAsync(jobId)).State.ShouldBe("Claimed");
    }

    [Fact]
    public async Task MarkCompleted_ExpiredLease_ReturnsFalse()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        var jobId = Guid.NewGuid();
        await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(jobId));
        var claimed = await PostgresTestData.ClaimAsync(context.Store);
        await context.ForceClaimExpiredAsync(jobId);

        (await context.Store.MarkCompletedAsync(new CompleteJobRequest(jobId, "node-1", claimed.LeaseToken, DateTimeOffset.UtcNow))).ShouldBeFalse();

        (await context.ReadJobAsync(jobId)).State.ShouldBe("Claimed");
    }

    [Fact]
    public async Task MarkCompleted_StaleToken_KeepsGroupSlotReserved()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        var jobId = Guid.NewGuid();
        await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(jobId, groupKeys: ["shared"]));
        var claimed = await PostgresTestData.ClaimAsync(context.Store);

        (await context.Store.MarkCompletedAsync(new CompleteJobRequest(jobId, "node-1", Guid.NewGuid(), DateTimeOffset.UtcNow))).ShouldBeFalse();
        (await context.ReadConcurrencyGroupAsync("shared")).ShouldNotBeNull().InUseCount.ShouldBe(1);

        (await context.Store.MarkCompletedAsync(new CompleteJobRequest(jobId, "node-1", claimed.LeaseToken, DateTimeOffset.UtcNow))).ShouldBeTrue();
        (await context.ReadConcurrencyGroupAsync("shared")).ShouldNotBeNull().InUseCount.ShouldBe(0);
    }

    [Fact]
    public async Task MarkCompleted_ExpiredLease_KeepsGroupSlotReservedUntilRecovery()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        var jobId = Guid.NewGuid();
        await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(jobId, groupKeys: ["shared"]));
        var claimed = await PostgresTestData.ClaimAsync(context.Store);
        await context.ForceClaimExpiredAsync(jobId);

        (await context.Store.MarkCompletedAsync(new CompleteJobRequest(jobId, "node-1", claimed.LeaseToken, DateTimeOffset.UtcNow))).ShouldBeFalse();
        (await context.ReadConcurrencyGroupAsync("shared")).ShouldNotBeNull().InUseCount.ShouldBe(1);

        (await context.Store.RecoverExpiredLeasesAsync(new RecoverExpiredLeasesRequest(DateTimeOffset.UtcNow))).ShouldBe(1);
        (await context.ReadConcurrencyGroupAsync("shared")).ShouldNotBeNull().InUseCount.ShouldBe(0);
    }
}
