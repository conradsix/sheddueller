namespace Sheddueller.Tests;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Sheddueller.Runtime;
using Sheddueller.Serialization;

using Shouldly;

public sealed class RecurringScheduleManagerTests
{
    [Fact]
    public async Task CreateOrUpdate_UpdateOptions_PassesUpdateOptionsToStore()
    {
        var store = new RecordingJobStore();
        var wakeSignal = new RecordingWakeSignal();
        var manager = CreateManager(store, wakeSignal);
        var updateOptions = new RecurringScheduleUpdateOptions(
          OverwriteCronExpression: false,
          OverwritePausedState: false);

        var result = await manager.CreateOrUpdateAsync<TestScheduleService>(
          "schedule-a",
          "* * * * *",
          (service, cancellationToken) => service.ExecuteAsync("feed-1", "job-1", cancellationToken),
          new RecurringScheduleOptions(Priority: 7),
          updateOptions);

        result.ShouldBe(RecurringScheduleUpsertResult.Created);
        var request = store.RecurringScheduleRequests.ShouldHaveSingleItem();
        request.ScheduleKey.ShouldBe("schedule-a");
        request.Priority.ShouldBe(7);
        request.UpdateOptions.ShouldBe(updateOptions);
        wakeSignal.NotifyCount.ShouldBe(1);
    }

    [Theory]
    [InlineData(RecurringScheduleUpsertResult.Created, 1)]
    [InlineData(RecurringScheduleUpsertResult.Updated, 1)]
    [InlineData(RecurringScheduleUpsertResult.Unchanged, 0)]
    public async Task CreateOrUpdate_Result_WakesOnlyWhenScheduleChanged(
        RecurringScheduleUpsertResult result,
        int expectedNotifyCount)
    {
        var store = new RecordingJobStore { CreateOrUpdateRecurringScheduleResult = result };
        var wakeSignal = new RecordingWakeSignal();
        var manager = CreateManager(store, wakeSignal);

        (await manager.CreateOrUpdateAsync<TestScheduleService>(
          "schedule-a",
          "* * * * *",
          (service, cancellationToken) => service.ExecuteAsync("feed-1", "job-1", cancellationToken))).ShouldBe(result);

        wakeSignal.NotifyCount.ShouldBe(expectedNotifyCount);
    }

    [Fact]
    public async Task Trigger_InvalidScheduleKey_ThrowsWithoutCallingStore()
    {
        var store = new RecordingJobStore();
        var wakeSignal = new RecordingWakeSignal();
        var manager = CreateManager(store, wakeSignal);

        await Should.ThrowAsync<ArgumentException>(() => manager.TriggerAsync(string.Empty).AsTask());

        store.TriggerRequests.ShouldBeEmpty();
        wakeSignal.NotifyCount.ShouldBe(0);
    }

    [Fact]
    public async Task Trigger_ValidScheduleKey_PassesDefaultRetryPolicyToStore()
    {
        var store = new RecordingJobStore
        {
            TriggerResult = new RecurringScheduleTriggerResult(
              RecurringScheduleTriggerStatus.Enqueued,
              Guid.Parse("2964dcd3-4dc9-4762-a8f7-3d5da608b1ed"),
              EnqueueSequence: 12),
        };
        var defaultRetryPolicy = new RetryPolicy(4, RetryBackoffKind.Fixed, TimeSpan.FromSeconds(2));
        var manager = CreateManager(
          store,
          new RecordingWakeSignal(),
          new ShedduellerOptions { DefaultRetryPolicy = defaultRetryPolicy });

        var result = await manager.TriggerAsync("schedule-a");

        result.Status.ShouldBe(RecurringScheduleTriggerStatus.Enqueued);
        store.TriggerRequests.ShouldHaveSingleItem().DefaultRetryPolicy.ShouldBe(defaultRetryPolicy);
    }

    [Theory]
    [InlineData(RecurringScheduleTriggerStatus.Enqueued, 1)]
    [InlineData(RecurringScheduleTriggerStatus.NotFound, 0)]
    [InlineData(RecurringScheduleTriggerStatus.SkippedActiveOccurrence, 0)]
    public async Task Trigger_Result_WakesOnlyWhenJobWasEnqueued(
        RecurringScheduleTriggerStatus status,
        int expectedNotifyCount)
    {
        var store = new RecordingJobStore
        {
            TriggerResult = status == RecurringScheduleTriggerStatus.Enqueued
              ? new RecurringScheduleTriggerResult(status, Guid.NewGuid(), EnqueueSequence: 1)
              : new RecurringScheduleTriggerResult(status),
        };
        var wakeSignal = new RecordingWakeSignal();
        var manager = CreateManager(store, wakeSignal);

        await manager.TriggerAsync("schedule-a");

        wakeSignal.NotifyCount.ShouldBe(expectedNotifyCount);
    }

    private static RecurringScheduleManager CreateManager(
        RecordingJobStore store,
        RecordingWakeSignal wakeSignal,
        ShedduellerOptions? options = null)
      => new(
        store,
        new SystemTextJsonJobPayloadSerializer(),
        Options.Create(options ?? new ShedduellerOptions()),
        TimeProvider.System,
        wakeSignal,
        NullLogger<RecurringScheduleManager>.Instance);

    private sealed class RecordingWakeSignal : IShedduellerWakeSignal
    {
        public int NotifyCount { get; private set; }

        public void Notify()
          => this.NotifyCount++;

        public ValueTask WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
          => ValueTask.CompletedTask;
    }

    private sealed class TestScheduleService
    {
        public Task ExecuteAsync(string feedId, string jobKey, CancellationToken cancellationToken)
          => Task.CompletedTask;
    }
}
