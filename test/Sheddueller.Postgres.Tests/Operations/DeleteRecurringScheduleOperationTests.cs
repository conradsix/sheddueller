namespace Sheddueller.Postgres.Tests.Operations;

using Shouldly;

public sealed class DeleteRecurringScheduleOperationTests(PostgresFixture fixture)
{
    [Fact]
    public async Task DeleteRecurringSchedule_ExistingSchedule_RemovesScheduleAndGroups()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        await context.Store.CreateOrUpdateRecurringScheduleAsync(PostgresTestData.CreateSchedule("schedule-a", groupKeys: ["shared"]));

        (await context.Store.DeleteRecurringScheduleAsync("schedule-a")).ShouldBeTrue();

        (await context.CountSchedulesAsync()).ShouldBe(0);
        (await context.ReadScheduleGroupKeysAsync("schedule-a")).ShouldBeEmpty();
    }

    [Fact]
    public async Task DeleteRecurringSchedule_MissingSchedule_ReturnsFalse()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);

        (await context.Store.DeleteRecurringScheduleAsync("missing")).ShouldBeFalse();
    }
}
