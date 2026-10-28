namespace Sheddueller.Postgres.Tests.Operations;

using Sheddueller.Storage;

using Shouldly;

public sealed class MarkCancellationObservedOperationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task CancellationObserved_CurrentLease_CancelsJobAndDecrementsGroups()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        var jobId = Guid.NewGuid();
        await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(jobId, groupKeys: ["shared"]));
        var claimed = await PostgresTestData.ClaimAsync(context.Store);
        (await context.Store.CancelAsync(new CancelJobRequest(jobId, DateTimeOffset.UtcNow))).ShouldBe(JobCancellationResult.CancellationRequested);

        (await context.Store.MarkCancellationObservedAsync(new ObserveJobCancellationRequest(jobId, "node-1", claimed.LeaseToken, DateTimeOffset.UtcNow))).ShouldBeTrue();

        var job = await context.ReadJobAsync(jobId);
        job.State.ShouldBe("Canceled");
        job.CancellationObservedAtUtc.ShouldNotBeNull();
        job.LeaseToken.ShouldBeNull();
        (await context.ReadConcurrencyGroupAsync("shared")).ShouldNotBeNull().InUseCount.ShouldBe(0);
    }

    [Fact]
    public async Task CancellationObserved_StaleToken_KeepsJobClaimedAndGroupSlotReserved()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        var jobId = Guid.NewGuid();
        await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(jobId, groupKeys: ["shared"]));
        await PostgresTestData.ClaimAsync(context.Store);
        (await context.Store.CancelAsync(new CancelJobRequest(jobId, DateTimeOffset.UtcNow))).ShouldBe(JobCancellationResult.CancellationRequested);

        (await context.Store.MarkCancellationObservedAsync(new ObserveJobCancellationRequest(jobId, "node-1", Guid.NewGuid(), DateTimeOffset.UtcNow))).ShouldBeFalse();

        (await context.ReadJobAsync(jobId)).State.ShouldBe("Claimed");
        (await context.ReadConcurrencyGroupAsync("shared")).ShouldNotBeNull().InUseCount.ShouldBe(1);
    }

    [Fact]
    public async Task CancellationObserved_NoCancellationRequested_KeepsJobClaimedAndGroupSlotReserved()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        var jobId = Guid.NewGuid();
        await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(jobId, groupKeys: ["shared"]));
        var claimed = await PostgresTestData.ClaimAsync(context.Store);

        (await context.Store.MarkCancellationObservedAsync(new ObserveJobCancellationRequest(jobId, "node-1", claimed.LeaseToken, DateTimeOffset.UtcNow))).ShouldBeFalse();

        (await context.ReadJobAsync(jobId)).State.ShouldBe("Claimed");
        (await context.ReadConcurrencyGroupAsync("shared")).ShouldNotBeNull().InUseCount.ShouldBe(1);
    }
}
