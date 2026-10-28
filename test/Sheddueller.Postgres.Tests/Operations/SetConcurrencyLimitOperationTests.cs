namespace Sheddueller.Postgres.Tests.Operations;

using Sheddueller.Storage;

using Shouldly;

public sealed class SetConcurrencyLimitOperationTests(PostgresFixture fixture)
{
    [Fact]
    public async Task SetConcurrencyLimit_InsertAndUpdate_ConfiguresLimit()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);

        await context.Store.SetConcurrencyLimitAsync(new SetConcurrencyLimitRequest("shared", 3, DateTimeOffset.UtcNow));
        (await context.ReadConcurrencyGroupAsync("shared")).ShouldNotBeNull().ConfiguredLimit.ShouldBe(3);

        await context.Store.SetConcurrencyLimitAsync(new SetConcurrencyLimitRequest("shared", 5, DateTimeOffset.UtcNow));
        (await context.ReadConcurrencyGroupAsync("shared")).ShouldNotBeNull().ConfiguredLimit.ShouldBe(5);
    }

    [Fact]
    public async Task SetConcurrencyLimit_ExistingOccupiedGroup_PreservesInUseCount()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(Guid.NewGuid(), groupKeys: ["shared"]));
        await PostgresTestData.ClaimAsync(context.Store);

        await context.Store.SetConcurrencyLimitAsync(new SetConcurrencyLimitRequest("shared", 2, DateTimeOffset.UtcNow));

        var row = (await context.ReadConcurrencyGroupAsync("shared")).ShouldNotBeNull();
        row.ConfiguredLimit.ShouldBe(2);
        row.InUseCount.ShouldBe(1);
    }

    [Fact]
    public async Task SetConcurrencyDefaultLimit_ExistingOverride_PreservesOverrideAndSetsEffectiveOverride()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);

        await context.Store.SetConcurrencyLimitAsync(new SetConcurrencyLimitRequest("shared", 5, DateTimeOffset.UtcNow));
        await context.Store.SetConcurrencyDefaultLimitAsync(new SetConcurrencyDefaultLimitRequest("shared", 2, DateTimeOffset.UtcNow));

        var row = (await context.ReadConcurrencyGroupAsync("shared")).ShouldNotBeNull();
        row.ConfiguredLimit.ShouldBe(5);
        row.DefaultLimit.ShouldBe(2);
        row.EffectiveLimit.ShouldBe(5);
    }

    [Fact]
    public async Task ClearConcurrencyLimitOverride_DefaultLimit_FallsBackToDefaultAndPreservesInUseCount()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        await context.Store.SetConcurrencyDefaultLimitAsync(new SetConcurrencyDefaultLimitRequest("shared", 2, DateTimeOffset.UtcNow));
        await context.Store.SetConcurrencyLimitAsync(new SetConcurrencyLimitRequest("shared", 5, DateTimeOffset.UtcNow));
        await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(Guid.NewGuid(), groupKeys: ["shared"]));
        await PostgresTestData.ClaimAsync(context.Store);

        await context.Store.ClearConcurrencyLimitOverrideAsync(new ClearConcurrencyLimitOverrideRequest("shared", DateTimeOffset.UtcNow));

        var row = (await context.ReadConcurrencyGroupAsync("shared")).ShouldNotBeNull();
        row.ConfiguredLimit.ShouldBeNull();
        row.DefaultLimit.ShouldBe(2);
        row.EffectiveLimit.ShouldBe(2);
        row.InUseCount.ShouldBe(1);
    }
}
