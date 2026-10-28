namespace Sheddueller.Postgres.Tests;

using Microsoft.Extensions.DependencyInjection;

using Sheddueller.Inspection.Schedules;
using Sheddueller.Storage;

using Shouldly;

public sealed class PostgresScheduleInspectionTests(PostgresFixture fixture)
{
    [Fact]
    public async Task ScheduleDetail_MoreOccurrencesThanListed_ListsNewestAndFindsOlderOutcomes()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        var retryPolicy = new RetryPolicy(3, RetryBackoffKind.Fixed, TimeSpan.FromSeconds(2));
        await context.Store.CreateOrUpdateRecurringScheduleAsync(PostgresTestData.CreateSchedule(
          "busy",
          retryPolicy: retryPolicy,
          overlapMode: RecurringOverlapMode.Allow));

        var completed = await TriggerAsync(context);
        var completedClaim = await PostgresTestData.ClaimAsync(context.Store);
        await context.Store.MarkCompletedAsync(new CompleteJobRequest(completed, "node-1", completedClaim.LeaseToken, DateTimeOffset.UtcNow));
        var failed = await TriggerAsync(context);
        await context.ExecuteAsync(
          $"update {context.Table("jobs")} set state = 'Failed', failed_at_utc = transaction_timestamp() where job_id = @job_id;",
          command => command.Parameters.AddWithValue("job_id", failed));
        var queued = new List<Guid>();
        for (var i = 0; i < 25; i++)
        {
            queued.Add(await TriggerAsync(context));
        }

        var detail = await context.Provider.GetRequiredService<IScheduleInspectionReader>().GetScheduleAsync("busy");

        detail.ShouldNotBeNull();
        detail.RetryPolicy.ShouldBe(retryPolicy);
        detail.RecentOccurrences.Select(static occurrence => occurrence.JobId).ShouldBe(Enumerable.Reverse(queued).Take(20));
        detail.RecentOccurrences.ShouldAllBe(occurrence => occurrence.State == JobState.Queued);
        detail.RecentOccurrences.ShouldAllBe(occurrence => occurrence.Kind == ScheduleOccurrenceKind.ManualTrigger);
        detail.LastSuccessfulOccurrence.ShouldNotBeNull().JobId.ShouldBe(completed);
        detail.LastSuccessfulOccurrence.State.ShouldBe(JobState.Completed);
        detail.LastSuccessfulOccurrence.CompletedAtUtc.ShouldNotBeNull();
        detail.LastFailedOccurrence.ShouldNotBeNull().JobId.ShouldBe(failed);
        detail.LastFailedOccurrence.FailedAtUtc.ShouldNotBeNull();
    }

    [Fact]
    public async Task ScheduleDetail_NoFinishedOccurrences_ReportsNoOutcomes()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        await context.Store.CreateOrUpdateRecurringScheduleAsync(PostgresTestData.CreateSchedule("quiet"));
        var queued = await TriggerAsync(context, "quiet");

        var detail = await context.Provider.GetRequiredService<IScheduleInspectionReader>().GetScheduleAsync("quiet");

        detail.ShouldNotBeNull();
        detail.RetryPolicy.ShouldBeNull();
        detail.RecentOccurrences.ShouldHaveSingleItem().JobId.ShouldBe(queued);
        detail.LastSuccessfulOccurrence.ShouldBeNull();
        detail.LastFailedOccurrence.ShouldBeNull();
    }

    [Fact]
    public async Task ScheduleDetail_MissingSchedule_ReturnsNull()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);

        (await context.Provider.GetRequiredService<IScheduleInspectionReader>().GetScheduleAsync("missing")).ShouldBeNull();
    }

    private static async ValueTask<Guid> TriggerAsync(PostgresTestContext context, string scheduleKey = "busy")
      => (await context.Store.TriggerRecurringScheduleAsync(new TriggerRecurringScheduleRequest(scheduleKey, DefaultRetryPolicy: null)))
        .JobId.ShouldNotBeNull();
}
