namespace Sheddueller.Postgres.Tests.Operations;

using Sheddueller.Storage;

using Shouldly;

public sealed class TryClaimNextJobOperationTests(PostgresFixture fixture)
{
    [Fact]
    public async Task TryClaim_NoQueuedJobs_ReturnsNoJobAvailable()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);

        (await context.Store.TryClaimNextAsync(PostgresTestData.ClaimRequest())).ShouldBeOfType<ClaimJobResult.NoJobAvailable>();
    }

    [Fact]
    public async Task TryClaim_InvalidLeaseDuration_Throws()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        var now = DateTimeOffset.UtcNow;

        await Should.ThrowAsync<ArgumentException>(() =>
          context.Store.TryClaimNextAsync(new ClaimJobRequest("node-1", now, now)).AsTask());
    }

    [Fact]
    public async Task TryClaim_PriorityAndFifo_ClaimsHigherPriorityThenOldest()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        var firstLow = Guid.NewGuid();
        var secondLow = Guid.NewGuid();
        var high = Guid.NewGuid();

        await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(firstLow, priority: 0));
        await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(secondLow, priority: 0));
        await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(high, priority: 10));

        (await PostgresTestData.ClaimAsync(context.Store)).JobId.ShouldBe(high);
        (await PostgresTestData.ClaimAsync(context.Store)).JobId.ShouldBe(firstLow);
        (await PostgresTestData.ClaimAsync(context.Store)).JobId.ShouldBe(secondLow);
    }

    [Fact]
    public async Task TryClaim_FutureNotBefore_IsNotClaimableUntilDatabaseTimePasses()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        var jobId = Guid.NewGuid();

        await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(jobId, notBeforeUtc: DateTimeOffset.UtcNow.AddMilliseconds(250)));

        (await context.Store.TryClaimNextAsync(PostgresTestData.ClaimRequest())).ShouldBeOfType<ClaimJobResult.NoJobAvailable>();
        await context.MakeJobDueAsync(jobId);

        (await PostgresTestData.ClaimAsync(context.Store)).JobId.ShouldBe(jobId);
    }

    [Fact]
    public async Task TryClaim_ReservedGroups_IncrementsInUseCountAndReturnsClaim()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        var jobId = Guid.NewGuid();

        await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(jobId, groupKeys: ["a", "b"]));

        var claimed = await PostgresTestData.ClaimAsync(context.Store);

        claimed.JobId.ShouldBe(jobId);
        (await context.ReadConcurrencyGroupAsync("a")).ShouldNotBeNull().InUseCount.ShouldBe(1);
        (await context.ReadConcurrencyGroupAsync("b")).ShouldNotBeNull().InUseCount.ShouldBe(1);
    }

    [Fact]
    public async Task TryClaim_SaturatedGroup_ClaimsNextEligibleJob()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        var running = Guid.NewGuid();
        var blocked = Guid.NewGuid();
        var eligible = Guid.NewGuid();

        await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(running, priority: 100, groupKeys: ["shared"]));
        (await PostgresTestData.ClaimAsync(context.Store)).JobId.ShouldBe(running);

        await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(blocked, priority: 100, groupKeys: ["shared"]));
        await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(eligible, priority: 0));

        (await PostgresTestData.ClaimAsync(context.Store)).JobId.ShouldBe(eligible);
        (await context.ReadJobAsync(blocked)).State.ShouldBe("Queued");
    }

    [Fact]
    public async Task TryClaim_SaturatedGroupPrefixBeyondOldCandidateLimit_ClaimsLaterEligibleJob()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        var running = Guid.NewGuid();
        var eligible = Guid.NewGuid();

        await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(running, priority: 100, groupKeys: ["shared"]));
        (await PostgresTestData.ClaimAsync(context.Store)).JobId.ShouldBe(running);

        for (var i = 0; i < 64; i++)
        {
            await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(Guid.NewGuid(), priority: 100, groupKeys: ["shared"]));
        }

        await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(eligible, priority: 0));

        (await PostgresTestData.ClaimAsync(context.Store)).JobId.ShouldBe(eligible);
    }

    [Fact]
    public async Task TryClaim_ConcurrentNodes_ClaimsJobOnlyOnce()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(Guid.NewGuid()));

        var results = await Task.WhenAll(Enumerable.Range(0, 2)
          .Select(index => context.Store.TryClaimNextAsync(PostgresTestData.ClaimRequest($"node-{index}")).AsTask()));

        results.Count(result => result is ClaimJobResult.Claimed).ShouldBe(1);
        results.Count(result => result is ClaimJobResult.NoJobAvailable).ShouldBe(1);
    }

    [Fact]
    public async Task TryClaim_SmoothRateLimit_SpacesClaimsAndReturnsNextClaimTime()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        await ConfigureRateAsync(context, "rate", new ConcurrencyGroupRateLimit(2, TimeSpan.FromSeconds(1)));
        await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(Guid.NewGuid(), groupKeys: ["rate"]));
        await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(Guid.NewGuid(), groupKeys: ["rate"]));

        await PostgresTestData.ClaimAsync(context.Store);
        var blockedAt = DateTimeOffset.UtcNow;
        var unavailable = (await context.Store.TryClaimNextAsync(PostgresTestData.ClaimRequest()))
          .ShouldBeOfType<ClaimJobResult.NoJobAvailable>();

        unavailable.NextClaimAtUtc.ShouldNotBeNull().ShouldBeGreaterThan(blockedAt);
        await context.AdvanceRateLimitClockAsync("rate", TimeSpan.FromMilliseconds(550));
        _ = await PostgresTestData.ClaimAsync(context.Store);
    }

    [Fact]
    public async Task TryClaim_SmoothRateLimit_IdleTimeDoesNotCreateBurstCredit()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        await ConfigureRateAsync(context, "rate", new ConcurrencyGroupRateLimit(2, TimeSpan.FromSeconds(1)));
        await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(Guid.NewGuid(), groupKeys: ["rate"]));
        await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(Guid.NewGuid(), groupKeys: ["rate"]));
        await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(Guid.NewGuid(), groupKeys: ["rate"]));
        await PostgresTestData.ClaimAsync(context.Store);

        // A long idle spell after the first claim: the next claim may take one permit, but no saved-up burst.
        await context.AdvanceRateLimitClockAsync("rate", TimeSpan.FromSeconds(10));
        await PostgresTestData.ClaimAsync(context.Store);

        (await context.Store.TryClaimNextAsync(PostgresTestData.ClaimRequest()))
          .ShouldBeOfType<ClaimJobResult.NoJobAvailable>()
          .NextClaimAtUtc.ShouldNotBeNull();
    }

    [Fact]
    public async Task TryClaim_RateBlockedHighPriority_ClaimsLaterEligibleJob()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        await ConfigureRateAsync(context, "rate", new ConcurrencyGroupRateLimit(1, TimeSpan.FromSeconds(1)));
        await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(Guid.NewGuid(), priority: 200, groupKeys: ["rate"]));
        await PostgresTestData.ClaimAsync(context.Store);
        var blocked = Guid.NewGuid();
        var eligible = Guid.NewGuid();
        await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(blocked, priority: 100, groupKeys: ["rate"]));
        await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(eligible, priority: 0));

        (await PostgresTestData.ClaimAsync(context.Store)).JobId.ShouldBe(eligible);
        (await context.ReadJobAsync(blocked)).State.ShouldBe("Queued");
    }

    [Fact]
    public async Task TryClaim_MultipleGroups_RateBlockedGroupDoesNotConsumeOtherGroup()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        var rate = new ConcurrencyGroupRateLimit(1, TimeSpan.FromSeconds(1));
        await ConfigureRateAsync(context, "rate-a", rate);
        await ConfigureRateAsync(context, "rate-b", rate);
        await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(Guid.NewGuid(), groupKeys: ["rate-a"]));
        await PostgresTestData.ClaimAsync(context.Store);
        await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(Guid.NewGuid(), priority: 100, groupKeys: ["rate-a", "rate-b"]));
        var bOnly = Guid.NewGuid();
        await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(bOnly, groupKeys: ["rate-b"]));

        (await PostgresTestData.ClaimAsync(context.Store)).JobId.ShouldBe(bOnly);
    }

    [Fact]
    public async Task TryClaim_ConcurrencyBlockedGroup_DoesNotConsumeRatePermit()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        await ConfigureRateAsync(context, "rate", new ConcurrencyGroupRateLimit(1, TimeSpan.FromSeconds(1)));
        var holder = Guid.NewGuid();
        await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(holder, groupKeys: ["capacity"]));
        var holderClaim = await PostgresTestData.ClaimAsync(context.Store);
        var blocked = Guid.NewGuid();
        await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(blocked, groupKeys: ["capacity", "rate"]));

        (await context.Store.TryClaimNextAsync(PostgresTestData.ClaimRequest()))
          .ShouldBeOfType<ClaimJobResult.NoJobAvailable>();
        (await context.ReadConcurrencyGroupAsync("rate")).ShouldNotBeNull().RateTheoreticalArrivalAtUtc.ShouldBeNull();

        await context.Store.MarkCompletedAsync(
          new CompleteJobRequest(holder, "node-1", holderClaim.LeaseToken, DateTimeOffset.UtcNow));
        (await PostgresTestData.ClaimAsync(context.Store)).JobId.ShouldBe(blocked);
    }

    [Fact]
    public async Task TryClaim_ConcurrentNodes_ConsumeOnlyOneRatePermit()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        await ConfigureRateAsync(context, "rate", new ConcurrencyGroupRateLimit(1, TimeSpan.FromMilliseconds(300)));
        await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(Guid.NewGuid(), groupKeys: ["rate"]));
        await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(Guid.NewGuid(), groupKeys: ["rate"]));

        var results = await Task.WhenAll(
          context.Store.TryClaimNextAsync(PostgresTestData.ClaimRequest("node-a")).AsTask(),
          context.Store.TryClaimNextAsync(PostgresTestData.ClaimRequest("node-b")).AsTask());

        results.Count(result => result is ClaimJobResult.Claimed).ShouldBe(1);
        results.Count(result => result is ClaimJobResult.NoJobAvailable).ShouldBe(1);
    }

    [Fact]
    public async Task TryClaim_RetryAttempt_ConsumesAnotherRatePermit()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        await ConfigureRateAsync(context, "rate", new ConcurrencyGroupRateLimit(1, TimeSpan.FromMilliseconds(300)));
        var jobId = Guid.NewGuid();
        await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(
          jobId,
          maxAttempts: 2,
          retryBackoffKind: RetryBackoffKind.Fixed,
          retryBaseDelay: TimeSpan.FromMilliseconds(1),
          groupKeys: ["rate"]));
        var first = await PostgresTestData.ClaimAsync(context.Store);
        await context.Store.MarkFailedAsync(
          new FailJobRequest(jobId, "node-1", first.LeaseToken, DateTimeOffset.UtcNow, PostgresTestData.CreateFailure()));
        await context.MakeJobDueAsync(jobId);

        (await context.Store.TryClaimNextAsync(PostgresTestData.ClaimRequest()))
          .ShouldBeOfType<ClaimJobResult.NoJobAvailable>()
          .NextClaimAtUtc.ShouldNotBeNull();

        await context.AdvanceRateLimitClockAsync("rate", TimeSpan.FromMilliseconds(330));
        (await PostgresTestData.ClaimAsync(context.Store)).AttemptCount.ShouldBe(2);
    }

    private static async ValueTask ConfigureRateAsync(
        PostgresTestContext context,
        string groupKey,
        ConcurrencyGroupRateLimit rateLimit)
    {
        await context.Store.SetConcurrencyLimitAsync(
          new SetConcurrencyLimitRequest(groupKey, 10, DateTimeOffset.UtcNow));
        await context.Store.SetConcurrencyDefaultRateLimitAsync(
          new SetConcurrencyDefaultRateLimitRequest(groupKey, rateLimit, DateTimeOffset.UtcNow));
    }
}
