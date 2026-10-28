namespace Sheddueller.Postgres.Tests.Operations;

using Shouldly;

public sealed class PauseRecurringScheduleOperationTests(PostgresFixture fixture)
{
    [Fact]
    public async Task PauseRecurringSchedule_ExistingSchedule_SetsPausedAndClearsNextFire()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        await context.Store.CreateOrUpdateRecurringScheduleAsync(PostgresTestData.CreateSchedule("schedule-a"));

        (await context.Store.PauseRecurringScheduleAsync("schedule-a", DateTimeOffset.UtcNow)).ShouldBeTrue();

        var schedule = await context.ReadScheduleAsync("schedule-a");
        schedule.IsPaused.ShouldBeTrue();
        schedule.NextFireAtUtc.ShouldBeNull();
    }

    [Fact]
    public async Task PauseRecurringSchedule_MissingSchedule_ReturnsFalse()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);

        (await context.Store.PauseRecurringScheduleAsync("missing", DateTimeOffset.UtcNow)).ShouldBeFalse();
    }
}
