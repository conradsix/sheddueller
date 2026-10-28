namespace Sheddueller.Postgres.Tests.Operations;

using Sheddueller.Storage;

using Shouldly;

public sealed class MaterializeDueRecurringSchedulesOperationTests(PostgresFixture fixture)
{
    [Fact]
    public async Task MaterializeDueRecurringSchedules_NoDueSchedules_ReturnsZero()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        await context.Store.CreateOrUpdateRecurringScheduleAsync(PostgresTestData.CreateSchedule("schedule-a"));

        (await context.Store.MaterializeDueRecurringSchedulesAsync(new MaterializeDueRecurringSchedulesRequest(DateTimeOffset.UtcNow, null))).ShouldBe(0);
    }

    [Fact]
    public async Task MaterializeDueRecurringSchedules_DueSchedule_InsertsJobAndAdvancesSchedule()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        await context.Store.CreateOrUpdateRecurringScheduleAsync(PostgresTestData.CreateSchedule(
          "schedule-a",
          priority: 9,
          groupKeys: ["shared"],
          retryPolicy: new RetryPolicy(3, RetryBackoffKind.Fixed, TimeSpan.FromSeconds(2))));
        await context.MakeScheduleDueAsync("schedule-a");
        var before = await context.ReadScheduleAsync("schedule-a");

        (await context.Store.MaterializeDueRecurringSchedulesAsync(new MaterializeDueRecurringSchedulesRequest(DateTimeOffset.UtcNow, null))).ShouldBe(1);

        var after = await context.ReadScheduleAsync("schedule-a");
        after.NextFireAtUtc.ShouldNotBeNull();
        after.NextFireAtUtc.ShouldNotBe(before.NextFireAtUtc);

        var claimed = await PostgresTestData.ClaimAsync(context.Store);
        claimed.SourceScheduleKey.ShouldBe("schedule-a");
        claimed.Priority.ShouldBe(9);
        claimed.ConcurrencyGroupKeys.ShouldBe(["shared"]);
        claimed.MaxAttempts.ShouldBe(3);
        claimed.RetryBackoffKind.ShouldBe(RetryBackoffKind.Fixed);
        claimed.RetryBaseDelay.ShouldBe(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task MaterializeDueRecurringSchedules_DefaultRetryPolicy_AppliesWhenScheduleHasNoRetry()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        await context.Store.CreateOrUpdateRecurringScheduleAsync(PostgresTestData.CreateSchedule("schedule-a"));
        await context.MakeScheduleDueAsync("schedule-a");

        await context.Store.MaterializeDueRecurringSchedulesAsync(new MaterializeDueRecurringSchedulesRequest(
          DateTimeOffset.UtcNow,
          new RetryPolicy(4, RetryBackoffKind.Exponential, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(6))));

        var claimed = await PostgresTestData.ClaimAsync(context.Store);
        claimed.MaxAttempts.ShouldBe(4);
        claimed.RetryBackoffKind.ShouldBe(RetryBackoffKind.Exponential);
        claimed.RetryBaseDelay.ShouldBe(TimeSpan.FromSeconds(3));
        claimed.RetryMaxDelay.ShouldBe(TimeSpan.FromSeconds(6));
    }

    [Fact]
    public async Task MaterializeDueRecurringSchedules_SkipOverlap_DoesNotInsertSecondNonTerminalOccurrence()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        await context.Store.CreateOrUpdateRecurringScheduleAsync(PostgresTestData.CreateSchedule("schedule-a", overlapMode: RecurringOverlapMode.Skip));
        await context.MakeScheduleDueAsync("schedule-a");
        (await context.Store.MaterializeDueRecurringSchedulesAsync(new MaterializeDueRecurringSchedulesRequest(DateTimeOffset.UtcNow, null))).ShouldBe(1);

        await context.MakeScheduleDueAsync("schedule-a");
        (await context.Store.MaterializeDueRecurringSchedulesAsync(new MaterializeDueRecurringSchedulesRequest(DateTimeOffset.UtcNow, null))).ShouldBe(0);

        (await context.CountJobsForScheduleAsync("schedule-a")).ShouldBe(1);
    }

    [Fact]
    public async Task MaterializeDueRecurringSchedules_AllowOverlap_InsertsSecondNonTerminalOccurrence()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        await context.Store.CreateOrUpdateRecurringScheduleAsync(PostgresTestData.CreateSchedule("schedule-a", overlapMode: RecurringOverlapMode.Allow));
        await context.MakeScheduleDueAsync("schedule-a");
        (await context.Store.MaterializeDueRecurringSchedulesAsync(new MaterializeDueRecurringSchedulesRequest(DateTimeOffset.UtcNow, null))).ShouldBe(1);

        await context.MakeScheduleDueAsync("schedule-a");
        (await context.Store.MaterializeDueRecurringSchedulesAsync(new MaterializeDueRecurringSchedulesRequest(DateTimeOffset.UtcNow, null))).ShouldBe(1);

        (await context.CountJobsForScheduleAsync("schedule-a")).ShouldBe(2);
    }

    [Fact]
    public async Task MaterializeDueRecurringSchedules_ConcurrentCalls_MaterializeScheduleOnce()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        await context.Store.CreateOrUpdateRecurringScheduleAsync(PostgresTestData.CreateSchedule("schedule-a"));
        await context.MakeScheduleDueAsync("schedule-a");

        var results = await Task.WhenAll(Enumerable.Range(0, 2)
          .Select(_ => context.Store.MaterializeDueRecurringSchedulesAsync(new MaterializeDueRecurringSchedulesRequest(DateTimeOffset.UtcNow, null)).AsTask()));

        results.Sum().ShouldBe(1);
        (await context.CountJobsForScheduleAsync("schedule-a")).ShouldBe(1);
    }
}
