namespace Sheddueller.Postgres.Tests.Operations;

using Shouldly;

public sealed class GetRecurringScheduleOperationTests(PostgresFixture fixture)
{
    [Fact]
    public async Task GetRecurringSchedule_ExistingSchedule_ReturnsDefinition()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        await context.Store.CreateOrUpdateRecurringScheduleAsync(PostgresTestData.CreateSchedule("schedule-a", priority: 3, groupKeys: ["shared"]));

        var schedule = await context.Store.GetRecurringScheduleAsync("schedule-a");

        schedule.ShouldNotBeNull();
        schedule.ScheduleKey.ShouldBe("schedule-a");
        schedule.Priority.ShouldBe(3);
        schedule.ConcurrencyGroupKeys.ShouldBe(["shared"]);
        schedule.NextFireAtUtc.ShouldNotBeNull();
    }

    [Fact]
    public async Task GetRecurringSchedule_MissingSchedule_ReturnsNull()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);

        (await context.Store.GetRecurringScheduleAsync("missing")).ShouldBeNull();
    }
}
