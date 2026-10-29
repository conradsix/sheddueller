#pragma warning disable CA2100 // Schema identifiers are generated and quoted by the isolated fixture.
namespace Sheddueller.Postgres.Tests.Operations;

using Microsoft.Extensions.DependencyInjection;

using Sheddueller.Storage;

using Shouldly;

public sealed class ConcurrencyGroupRetentionTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Cleanup_UnreferencedGroups_DeletesOnlyOneBatch()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        await SeedAsync(context, "a", "b", "c");
        var store = context.Provider.GetRequiredService<IConcurrencyGroupRetentionStore>();

        (await store.CleanupConcurrencyGroupsAsync(TimeSpan.FromDays(1), 2)).ShouldBe(2);
        (await store.CleanupConcurrencyGroupsAsync(TimeSpan.FromDays(1), 2)).ShouldBe(1);
        (await store.CleanupConcurrencyGroupsAsync(TimeSpan.FromDays(1), 2)).ShouldBe(0);
    }

    [Fact]
    public async Task Cleanup_ConfiguredOccupiedRecentOrCoolingGroups_PreservesThem()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        await SeedAsync(context, "idle", "default", "override", "rate", "unlimited", "occupied", "recent", "cooldown");
        await context.ExecuteAsync($"""
          update {context.Table("concurrency_groups")} set default_limit = 2 where group_key = 'default';
          update {context.Table("concurrency_groups")} set configured_limit = 3 where group_key = 'override';
          update {context.Table("concurrency_groups")} set default_rate_permit_count = 1, default_rate_period = interval '1 second' where group_key = 'rate';
          update {context.Table("concurrency_groups")} set rate_limit_override_enabled = true where group_key = 'unlimited';
          update {context.Table("concurrency_groups")} set in_use_count = 1 where group_key = 'occupied';
          update {context.Table("concurrency_groups")} set updated_at_utc = transaction_timestamp() where group_key = 'recent';
          update {context.Table("concurrency_groups")} set rate_theoretical_arrival_at_utc = transaction_timestamp() + interval '1 hour' where group_key = 'cooldown';
          """, _ => { });

        (await context.Provider.GetRequiredService<IConcurrencyGroupRetentionStore>().CleanupConcurrencyGroupsAsync(TimeSpan.FromDays(1), 100)).ShouldBe(1);
        foreach (var key in new[] { "default", "override", "rate", "unlimited", "occupied", "recent", "cooldown" })
        {
            (await context.ReadConcurrencyGroupAsync(key)).ShouldNotBeNull();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cleanup_RetainedJobReferences_PreservesGroups(bool complete)
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        await SeedAsync(context, "job");
        var id = Guid.NewGuid();
        await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(id, groupKeys: ["job"]));
        if (complete)
        {
            var claimed = await PostgresTestData.ClaimAsync(context.Store);
            await context.Store.MarkCompletedAsync(new(id, "node-1", claimed.LeaseToken, DateTimeOffset.UtcNow));
        }

        await context.ExecuteAsync($"update {context.Table("concurrency_groups")} set updated_at_utc = transaction_timestamp() - interval '2 days';", _ => { });
        (await context.Provider.GetRequiredService<IConcurrencyGroupRetentionStore>().CleanupConcurrencyGroupsAsync(TimeSpan.FromDays(1), 100)).ShouldBe(0);
        (await context.ReadConcurrencyGroupAsync("job")).ShouldNotBeNull();
    }

    [Fact]
    public async Task Cleanup_PausedScheduleReference_PreservesGroup()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        await SeedAsync(context, "schedule");
        await context.Store.CreateOrUpdateRecurringScheduleAsync(PostgresTestData.CreateSchedule("scheduled", groupKeys: ["schedule"]));
        await context.ExecuteAsync($"update {context.Table("recurring_schedules")} set is_paused = true;", _ => { });

        (await context.Provider.GetRequiredService<IConcurrencyGroupRetentionStore>().CleanupConcurrencyGroupsAsync(TimeSpan.FromDays(1), 100)).ShouldBe(0);
    }

    [Fact]
    public async Task Cleanup_GroupLockedForConfiguration_SkipsThenPreservesUpdatedGroup()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        await SeedAsync(context, "locked", "free");
        await using var connection = await context.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"update {context.Table("concurrency_groups")} set configured_limit = 4 where group_key = 'locked';";
        await command.ExecuteNonQueryAsync();
        var store = context.Provider.GetRequiredService<IConcurrencyGroupRetentionStore>();

        (await store.CleanupConcurrencyGroupsAsync(TimeSpan.FromDays(1), 100)).ShouldBe(1);
        await transaction.CommitAsync();
        (await store.CleanupConcurrencyGroupsAsync(TimeSpan.FromDays(1), 100)).ShouldBe(0);
        (await context.ReadConcurrencyGroupAsync("locked")).ShouldNotBeNull().ConfiguredLimit.ShouldBe(4);
    }

    [Fact]
    public async Task Cleanup_ConcurrentClaim_PreservesTheJobGroupAndReservation()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        await SeedAsync(context, "claimed");
        var id = Guid.NewGuid();
        await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(id, groupKeys: ["claimed"]));
        var claim = PostgresTestData.ClaimAsync(context.Store).AsTask();
        var cleanup = context.Provider.GetRequiredService<IConcurrencyGroupRetentionStore>()
          .CleanupConcurrencyGroupsAsync(TimeSpan.FromDays(1), 1000).AsTask();
        await Task.WhenAll(claim, cleanup);

        (await claim).JobId.ShouldBe(id);
        (await cleanup).ShouldBe(0);
        (await context.ReadConcurrencyGroupAsync("claimed")).ShouldNotBeNull().InUseCount.ShouldBe(1);
    }

    [Fact]
    public async Task Cleanup_ConcurrentHosts_DeleteEachGroupOnce()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        await SeedAsync(context, [.. Enumerable.Range(0, 2000).Select(i => $"group-{i:D4}")]);
        var store = context.Provider.GetRequiredService<IConcurrencyGroupRetentionStore>();
        var deleted = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => store.CleanupConcurrencyGroupsAsync(TimeSpan.FromDays(1), 1000).AsTask()));
        var remainder = await store.CleanupConcurrencyGroupsAsync(TimeSpan.FromDays(1), 2000);

        (deleted.Sum() + remainder).ShouldBe(2000);
    }

    private static ValueTask SeedAsync(PostgresTestContext context, params string[] keys)
      => context.ExecuteAsync($"""
        insert into {context.Table("concurrency_groups")} (group_key, in_use_count, updated_at_utc)
        select key, 0, transaction_timestamp() - interval '2 days' from unnest(@keys::text[]) key;
        """, command => command.Parameters.AddWithValue("keys", keys));
}
