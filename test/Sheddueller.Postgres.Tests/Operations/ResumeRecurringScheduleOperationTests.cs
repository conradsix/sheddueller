namespace Sheddueller.Postgres.Tests.Operations;

using Shouldly;

public sealed class ResumeRecurringScheduleOperationTests(PostgresFixture fixture)
{
    [Fact]
    public async Task ResumeRecurringSchedule_ExistingSchedule_SetsActiveAndComputesNextFire()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        await context.Store.CreateOrUpdateRecurringScheduleAsync(PostgresTestData.CreateSchedule("schedule-a"));
        await context.Store.PauseRecurringScheduleAsync("schedule-a", DateTimeOffset.UtcNow);

        (await context.Store.ResumeRecurringScheduleAsync("schedule-a", DateTimeOffset.UtcNow)).ShouldBeTrue();

        var schedule = await context.ReadScheduleAsync("schedule-a");
        schedule.IsPaused.ShouldBeFalse();
        schedule.NextFireAtUtc.ShouldNotBeNull();
    }

    [Fact]
    public async Task ResumeRecurringSchedule_MissingSchedule_ReturnsFalse()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);

        (await context.Store.ResumeRecurringScheduleAsync("missing", DateTimeOffset.UtcNow)).ShouldBeFalse();
    }
}
