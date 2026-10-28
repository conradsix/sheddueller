namespace Sheddueller.Postgres.Tests.Operations;

using Sheddueller.Storage;

using Shouldly;

public sealed class RenewJobLeaseOperationTests(PostgresFixture fixture)
{
    [Fact]
    public async Task RenewLease_CurrentLease_UpdatesHeartbeatAndExpiry()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        var jobId = Guid.NewGuid();
        await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(jobId));
        var claimed = await PostgresTestData.ClaimAsync(context.Store);
        var heartbeatAt = DateTimeOffset.UtcNow;

        (await context.Store.RenewLeaseAsync(new RenewLeaseRequest(jobId, "node-1", claimed.LeaseToken, heartbeatAt, heartbeatAt.AddMinutes(2)))).ShouldBeTrue();

        var job = await context.ReadJobAsync(jobId);
        job.LastHeartbeatAtUtc.ShouldNotBeNull();
        job.LeaseExpiresAtUtc.ShouldNotBeNull();
        job.LeaseExpiresAtUtc.Value.ShouldBeGreaterThan(claimed.LeaseExpiresAtUtc);
    }

    [Fact]
    public async Task RenewLease_StaleToken_ReturnsFalse()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        var jobId = Guid.NewGuid();
        await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(jobId));
        await PostgresTestData.ClaimAsync(context.Store);
        var heartbeatAt = DateTimeOffset.UtcNow;

        (await context.Store.RenewLeaseAsync(new RenewLeaseRequest(jobId, "node-1", Guid.NewGuid(), heartbeatAt, heartbeatAt.AddMinutes(1)))).ShouldBeFalse();
    }

    [Fact]
    public async Task RenewLease_InvalidLeaseDuration_Throws()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        var now = DateTimeOffset.UtcNow;

        await Should.ThrowAsync<ArgumentException>(() =>
          context.Store.RenewLeaseAsync(new RenewLeaseRequest(Guid.NewGuid(), "node-1", Guid.NewGuid(), now, now)).AsTask());
    }
}
