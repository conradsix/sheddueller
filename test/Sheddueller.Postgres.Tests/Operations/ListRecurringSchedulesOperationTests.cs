namespace Sheddueller.Postgres.Tests.Operations;

using Shouldly;

public sealed class ListRecurringSchedulesOperationTests(PostgresFixture fixture)
{
    [Fact]
    public async Task ListRecurringSchedules_ReturnsSchedulesOrderedByKey()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        await context.Store.CreateOrUpdateRecurringScheduleAsync(PostgresTestData.CreateSchedule("schedule-b"));
        await context.Store.CreateOrUpdateRecurringScheduleAsync(PostgresTestData.CreateSchedule("schedule-a"));

        var schedules = await context.Store.ListRecurringSchedulesAsync();

        schedules.Select(schedule => schedule.ScheduleKey).ShouldBe(["schedule-a", "schedule-b"]);
    }

    [Fact]
    public async Task ListRecurringSchedules_NoSchedules_ReturnsEmpty()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);

        (await context.Store.ListRecurringSchedulesAsync()).ShouldBeEmpty();
    }
}
