namespace Sheddueller.Postgres.Tests.Operations;

using Microsoft.Extensions.DependencyInjection;

using Sheddueller.Inspection.Jobs;
using Sheddueller.Runtime;
using Sheddueller.Storage;

using Shouldly;

public sealed class TriggerRecurringScheduleOperationTests(PostgresFixture fixture)
{
    [Fact]
    public async Task TriggerRecurringSchedule_MissingSchedule_ReturnsNotFound()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);

        var result = await context.Store.TriggerRecurringScheduleAsync(new TriggerRecurringScheduleRequest("missing", DefaultRetryPolicy: null));

        result.Status.ShouldBe(RecurringScheduleTriggerStatus.NotFound);
        result.JobId.ShouldBeNull();
        result.EnqueueSequence.ShouldBeNull();
        (await context.CountJobsAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task TriggerRecurringSchedule_ExistingSchedule_InsertsClaimableManualJob()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        var retryPolicy = new RetryPolicy(3, RetryBackoffKind.Fixed, TimeSpan.FromSeconds(2));
        await context.Store.CreateOrUpdateRecurringScheduleAsync(PostgresTestData.CreateSchedule(
          "schedule-a",
          priority: 9,
          groupKeys: ["shared"],
          retryPolicy,
          tags: [new JobTag("tenant", "acme"), new JobTag("domain", "payments")],
          invocationTargetKind: JobInvocationTargetKind.Static,
          methodParameterBindings:
          [
              new JobMethodParameterBinding(JobMethodParameterBindingKind.CancellationToken),
          ]));

        var result = await context.Store.TriggerRecurringScheduleAsync(new TriggerRecurringScheduleRequest("schedule-a", DefaultRetryPolicy: null));

        result.Status.ShouldBe(RecurringScheduleTriggerStatus.Enqueued);
        result.JobId.ShouldNotBeNull();
        result.EnqueueSequence.ShouldNotBeNull();

        var jobId = result.JobId.Value;
        var job = await context.ReadJobAsync(jobId);
        job.EnqueueSequence.ShouldBe(result.EnqueueSequence.Value);
        job.SourceScheduleKey.ShouldBe("schedule-a");
        job.ScheduledFireAtUtc.ShouldBeNull();
        job.ScheduleOccurrenceKind.ShouldBe(nameof(ScheduleOccurrenceKind.ManualTrigger));
        job.Priority.ShouldBe(9);
        job.MaxAttempts.ShouldBe(3);
        job.RetryBackoffKind.ShouldBe(nameof(RetryBackoffKind.Fixed));
        job.RetryBaseDelayMs.ShouldBe(2000);
        job.InvocationTargetKind.ShouldBe(nameof(JobInvocationTargetKind.Static));
        job.MethodParameterBindings.ShouldBe([nameof(JobMethodParameterBindingKind.CancellationToken)]);
        (await context.ReadJobGroupKeysAsync(jobId)).ShouldBe(["shared"]);
        (await context.ReadJobTagsAsync(jobId)).ShouldBe([new JobTag("tenant", "acme"), new JobTag("domain", "payments")]);

        var inspectionReader = context.InspectionReader;
        var inspected = await inspectionReader.GetJobAsync(jobId);
        inspected.ShouldNotBeNull();
        inspected.ScheduledFireAtUtc.ShouldBeNull();
        inspected.Summary.ScheduleOccurrenceKind.ShouldBe(ScheduleOccurrenceKind.ManualTrigger);

        var claimed = await PostgresTestData.ClaimAsync(context.Store);
        claimed.JobId.ShouldBe(jobId);
        claimed.SourceScheduleKey.ShouldBe("schedule-a");
        claimed.ScheduledFireAtUtc.ShouldBeNull();
        claimed.Priority.ShouldBe(9);
        claimed.ConcurrencyGroupKeys.ShouldBe(["shared"]);
        claimed.MaxAttempts.ShouldBe(3);
        claimed.RetryBackoffKind.ShouldBe(RetryBackoffKind.Fixed);
        claimed.RetryBaseDelay.ShouldBe(TimeSpan.FromSeconds(2));
        claimed.InvocationTargetKind.ShouldBe(JobInvocationTargetKind.Static);
        claimed.MethodParameterBindings.ShouldBe([
            new JobMethodParameterBinding(JobMethodParameterBindingKind.CancellationToken),
        ]);
    }

    [Fact]
    public async Task TriggerRecurringSchedule_PausedSchedule_EnqueuesJobWithoutResuming()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        await context.Store.CreateOrUpdateRecurringScheduleAsync(PostgresTestData.CreateSchedule("schedule-a"));
        await context.Store.PauseRecurringScheduleAsync("schedule-a", DateTimeOffset.UtcNow);

        var result = await context.Store.TriggerRecurringScheduleAsync(new TriggerRecurringScheduleRequest("schedule-a", DefaultRetryPolicy: null));

        result.Status.ShouldBe(RecurringScheduleTriggerStatus.Enqueued);
        (await context.ReadScheduleAsync("schedule-a")).IsPaused.ShouldBeTrue();
        (await PostgresTestData.ClaimAsync(context.Store)).SourceScheduleKey.ShouldBe("schedule-a");
    }

    [Fact]
    public async Task TriggerRecurringSchedule_SkipOverlap_DoesNotInsertSecondActiveOccurrence()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        await context.Store.CreateOrUpdateRecurringScheduleAsync(PostgresTestData.CreateSchedule("schedule-a", overlapMode: RecurringOverlapMode.Skip));

        (await context.Store.TriggerRecurringScheduleAsync(new TriggerRecurringScheduleRequest("schedule-a", DefaultRetryPolicy: null)))
          .Status.ShouldBe(RecurringScheduleTriggerStatus.Enqueued);
        var skipped = await context.Store.TriggerRecurringScheduleAsync(new TriggerRecurringScheduleRequest("schedule-a", DefaultRetryPolicy: null));

        skipped.Status.ShouldBe(RecurringScheduleTriggerStatus.SkippedActiveOccurrence);
        skipped.JobId.ShouldBeNull();
        skipped.EnqueueSequence.ShouldBeNull();
        (await context.CountJobsForScheduleAsync("schedule-a")).ShouldBe(1);
    }

    [Fact]
    public async Task TriggerRecurringSchedule_AllowOverlap_InsertsSecondActiveOccurrence()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        await context.Store.CreateOrUpdateRecurringScheduleAsync(PostgresTestData.CreateSchedule("schedule-a", overlapMode: RecurringOverlapMode.Allow));

        (await context.Store.TriggerRecurringScheduleAsync(new TriggerRecurringScheduleRequest("schedule-a", DefaultRetryPolicy: null)))
          .Status.ShouldBe(RecurringScheduleTriggerStatus.Enqueued);
        (await context.Store.TriggerRecurringScheduleAsync(new TriggerRecurringScheduleRequest("schedule-a", DefaultRetryPolicy: null)))
          .Status.ShouldBe(RecurringScheduleTriggerStatus.Enqueued);

        (await context.CountJobsForScheduleAsync("schedule-a")).ShouldBe(2);
    }

    [Fact]
    public async Task TriggerRecurringSchedule_ExistingSchedule_DoesNotAdvanceNextFire()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        await context.Store.CreateOrUpdateRecurringScheduleAsync(PostgresTestData.CreateSchedule("schedule-a"));
        var before = await context.ReadScheduleAsync("schedule-a");

        await context.Store.TriggerRecurringScheduleAsync(new TriggerRecurringScheduleRequest("schedule-a", DefaultRetryPolicy: null));

        var after = await context.ReadScheduleAsync("schedule-a");
        after.NextFireAtUtc.ShouldBe(before.NextFireAtUtc);
    }

    [Fact]
    public async Task TriggerRecurringSchedule_DefaultRetryPolicy_AppliesWhenScheduleHasNoRetry()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        await context.Store.CreateOrUpdateRecurringScheduleAsync(PostgresTestData.CreateSchedule("schedule-a"));

        await context.Store.TriggerRecurringScheduleAsync(new TriggerRecurringScheduleRequest(
          "schedule-a",
          new RetryPolicy(5, RetryBackoffKind.Exponential, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(4))));

        var claimed = await PostgresTestData.ClaimAsync(context.Store);
        claimed.MaxAttempts.ShouldBe(5);
        claimed.RetryBackoffKind.ShouldBe(RetryBackoffKind.Exponential);
        claimed.RetryBaseDelay.ShouldBe(TimeSpan.FromSeconds(1));
        claimed.RetryMaxDelay.ShouldBe(TimeSpan.FromSeconds(4));
    }

    [Fact]
    public async Task TriggerRecurringSchedule_EnqueuedJob_NotifiesWaitingProvider()
    {
        await using var firstContext = await PostgresTestContext.CreateMigratedAsync(fixture);
        await using var secondProvider = PostgresTestContext.CreateProvider(fixture.DataSource, firstContext.SchemaName);
        await secondProvider.GetRequiredService<IJobStore>()
          .CreateOrUpdateRecurringScheduleAsync(PostgresTestData.CreateSchedule("schedule-a"));

        var notified = await ObserveWakeAsync(
          firstContext.Provider.GetRequiredService<IShedduellerWakeSignal>(),
          () => secondProvider.GetRequiredService<IJobStore>()
            .TriggerRecurringScheduleAsync(new TriggerRecurringScheduleRequest("schedule-a", DefaultRetryPolicy: null)));

        notified.ShouldBeTrue();
    }

    [Fact]
    public async Task TriggerRecurringSchedule_NotFound_DoesNotNotifyWaitingProvider()
    {
        await using var firstContext = await PostgresTestContext.CreateMigratedAsync(fixture);
        await using var secondProvider = PostgresTestContext.CreateProvider(fixture.DataSource, firstContext.SchemaName);

        var notified = await ObserveWakeAsync(
          firstContext.Provider.GetRequiredService<IShedduellerWakeSignal>(),
          () => secondProvider.GetRequiredService<IJobStore>()
            .TriggerRecurringScheduleAsync(new TriggerRecurringScheduleRequest("missing", DefaultRetryPolicy: null)));

        notified.ShouldBeFalse();
    }

    [Fact]
    public async Task TriggerRecurringSchedule_SkippedOccurrence_DoesNotNotifyWaitingProvider()
    {
        await using var firstContext = await PostgresTestContext.CreateMigratedAsync(fixture);
        await using var secondProvider = PostgresTestContext.CreateProvider(fixture.DataSource, firstContext.SchemaName);
        var store = secondProvider.GetRequiredService<IJobStore>();
        await store.CreateOrUpdateRecurringScheduleAsync(PostgresTestData.CreateSchedule("schedule-a"));
        await store.TriggerRecurringScheduleAsync(new TriggerRecurringScheduleRequest("schedule-a", DefaultRetryPolicy: null));

        var notified = await ObserveWakeAsync(
          firstContext.Provider.GetRequiredService<IShedduellerWakeSignal>(),
          () => store.TriggerRecurringScheduleAsync(new TriggerRecurringScheduleRequest("schedule-a", DefaultRetryPolicy: null)));

        notified.ShouldBeFalse();
    }

    private static async ValueTask<bool> ObserveWakeAsync(
        IShedduellerWakeSignal wakeSignal,
        Func<ValueTask<RecurringScheduleTriggerResult>> action)
    {
        await WaitForListenerAsync(wakeSignal);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var waitTask = wakeSignal.WaitAsync(TimeSpan.FromSeconds(5), timeout.Token).AsTask();

        _ = await action().ConfigureAwait(false);

        var completedTask = await Task.WhenAny(waitTask, Task.Delay(TimeSpan.FromMilliseconds(300), CancellationToken.None))
          .ConfigureAwait(false);
        if (completedTask == waitTask)
        {
            await waitTask.ConfigureAwait(false);
            return true;
        }

        await timeout.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => waitTask);
        return false;
    }

    /// <summary>
    /// Consumes the wake the listener raises once its LISTEN succeeds, so only the action's notification is observed.
    /// </summary>
    private static async Task WaitForListenerAsync(IShedduellerWakeSignal wakeSignal)
    {
        using var listening = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await wakeSignal.WaitAsync(Timeout.InfiniteTimeSpan, listening.Token);
    }
}
