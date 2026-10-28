namespace Sheddueller.Postgres.Tests.Operations;

using Sheddueller.Storage;

using Shouldly;

public sealed class SetConcurrencyRateLimitOperationTests(PostgresFixture fixture)
{
    private static readonly ConcurrencyGroupRateLimit DefaultRate = new(2, TimeSpan.FromSeconds(1));
    private static readonly ConcurrencyGroupRateLimit OverrideRate = new(3, TimeSpan.FromSeconds(2));

    [Fact]
    public async Task RateLimit_DefaultOverrideUnlimitedAndClear_UsesEffectivePrecedence()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);

        await context.Store.SetConcurrencyDefaultRateLimitAsync(
          new SetConcurrencyDefaultRateLimitRequest("shared", DefaultRate, DateTimeOffset.UtcNow));
        var defaultOnly = (await context.ReadConcurrencyGroupAsync("shared")).ShouldNotBeNull();
        defaultOnly.EffectiveRateLimit.ShouldBe(DefaultRate);
        (await context.Store.GetConcurrencyRateLimitOverrideAsync("shared")).Kind
          .ShouldBe(ConcurrencyGroupRateLimitOverrideKind.Inherit);

        await context.Store.SetConcurrencyRateLimitAsync(
          new SetConcurrencyRateLimitRequest("shared", OverrideRate, DateTimeOffset.UtcNow));
        var overridden = (await context.ReadConcurrencyGroupAsync("shared")).ShouldNotBeNull();
        overridden.EffectiveRateLimit.ShouldBe(OverrideRate);
        (await context.Store.GetConcurrencyRateLimitOverrideAsync("shared"))
          .ShouldBe(new ConcurrencyGroupRateLimitOverride(
            ConcurrencyGroupRateLimitOverrideKind.Limited,
            OverrideRate));

        await context.Store.SetConcurrencyUnlimitedRateLimitAsync(
          new SetConcurrencyUnlimitedRateLimitRequest("shared", DateTimeOffset.UtcNow));
        var unlimited = (await context.ReadConcurrencyGroupAsync("shared")).ShouldNotBeNull();
        unlimited.RateLimitOverrideEnabled.ShouldBeTrue();
        unlimited.EffectiveRateLimit.ShouldBeNull();
        (await context.Store.GetConcurrencyRateLimitOverrideAsync("shared")).Kind
          .ShouldBe(ConcurrencyGroupRateLimitOverrideKind.Unlimited);

        await context.Store.ClearConcurrencyRateLimitOverrideAsync(
          new ClearConcurrencyRateLimitOverrideRequest("shared", DateTimeOffset.UtcNow));
        var inherited = (await context.ReadConcurrencyGroupAsync("shared")).ShouldNotBeNull();
        inherited.RateLimitOverrideEnabled.ShouldBeFalse();
        inherited.EffectiveRateLimit.ShouldBe(DefaultRate);

        await context.Store.ClearConcurrencyDefaultRateLimitAsync(
          new ClearConcurrencyDefaultRateLimitRequest("shared", DateTimeOffset.UtcNow));
        (await context.ReadConcurrencyGroupAsync("shared")).ShouldNotBeNull().EffectiveRateLimit.ShouldBeNull();
    }

    [Fact]
    public async Task RateLimit_UnchangedDefault_PreservesTimingState()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        var jobId = Guid.NewGuid();

        await context.Store.SetConcurrencyDefaultRateLimitAsync(
          new SetConcurrencyDefaultRateLimitRequest("shared", DefaultRate, DateTimeOffset.UtcNow));
        await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(jobId, groupKeys: ["shared"]));
        await PostgresTestData.ClaimAsync(context.Store);
        var before = (await context.ReadConcurrencyGroupAsync("shared")).ShouldNotBeNull().RateTheoreticalArrivalAtUtc;
        before.ShouldNotBeNull();

        await context.Store.SetConcurrencyDefaultRateLimitAsync(
          new SetConcurrencyDefaultRateLimitRequest("shared", DefaultRate, DateTimeOffset.UtcNow));

        (await context.ReadConcurrencyGroupAsync("shared")).ShouldNotBeNull().RateTheoreticalArrivalAtUtc.ShouldBe(before);
    }

    [Fact]
    public async Task RateLimit_EffectiveChange_ResetsTimingState()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        var jobId = Guid.NewGuid();

        await context.Store.SetConcurrencyDefaultRateLimitAsync(
          new SetConcurrencyDefaultRateLimitRequest("shared", DefaultRate, DateTimeOffset.UtcNow));
        await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(jobId, groupKeys: ["shared"]));
        await PostgresTestData.ClaimAsync(context.Store);
        (await context.ReadConcurrencyGroupAsync("shared")).ShouldNotBeNull().RateTheoreticalArrivalAtUtc.ShouldNotBeNull();

        await context.Store.SetConcurrencyDefaultRateLimitAsync(
          new SetConcurrencyDefaultRateLimitRequest("shared", OverrideRate, DateTimeOffset.UtcNow));

        (await context.ReadConcurrencyGroupAsync("shared")).ShouldNotBeNull().RateTheoreticalArrivalAtUtc.ShouldBeNull();
    }
}
