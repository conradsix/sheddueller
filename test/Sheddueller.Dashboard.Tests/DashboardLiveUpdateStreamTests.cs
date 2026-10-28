namespace Sheddueller.Dashboard.Tests;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Sheddueller.Dashboard.Internal;
using Sheddueller.Storage;
using Sheddueller.Tests.Logging;

using Shouldly;

public sealed class DashboardLiveUpdateStreamTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Notify_SlowSubscriber_DoesNotDelayOtherSubscribers()
    {
        var stream = new DashboardLiveUpdateStream(NullLogger<DashboardLiveUpdateStream>.Instance);
        var slowSubscriberRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var otherSubscriberReceived = new TaskCompletionSource<JobEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var jobEvent = CreateEvent();

        stream.JobEventPublished += (_, _) => new ValueTask(slowSubscriberRelease.Task);
        stream.JobEventPublished += async (published, _) =>
        {
            await Task.Yield();
            otherSubscriberReceived.SetResult(published);
        };

        var notify = stream.NotifyAsync(jobEvent, CancellationToken.None).AsTask();

        (await otherSubscriberReceived.Task.WaitAsync(Timeout)).ShouldBe(jobEvent);
        notify.IsCompleted.ShouldBeFalse();

        slowSubscriberRelease.SetResult();
        await notify.WaitAsync(Timeout);
    }

    [Fact]
    public async Task Notify_FailingSubscribers_StillReachOthersAndAreLogged()
    {
        using var logs = new TestLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder
          .SetMinimumLevel(LogLevel.Trace)
          .AddProvider(logs));
        var stream = new DashboardLiveUpdateStream(loggerFactory.CreateLogger<DashboardLiveUpdateStream>());
        var received = new List<JobEvent>();
        var jobEvent = CreateEvent();

        stream.JobEventPublished += (_, _) => throw new InvalidOperationException("circuit gone");
        stream.JobEventPublished += async (_, _) =>
        {
            await Task.Yield();
            throw new InvalidOperationException("render failed");
        };
        stream.JobEventPublished += (published, _) =>
        {
            received.Add(published);
            return ValueTask.CompletedTask;
        };

        await stream.NotifyAsync(jobEvent, CancellationToken.None).AsTask().WaitAsync(Timeout);

        received.ShouldBe([jobEvent]);
        var failures = logs.Entries.Where(entry => entry.EventId.Id == 1310).ToArray();
        failures.Select(entry => entry.Exception?.Message).ShouldBe(["circuit gone", "render failed"], ignoreOrder: true);
        failures.ShouldAllBe(entry => entry.Level == LogLevel.Warning);
        failures.ShouldAllBe(entry => Equals(entry.Properties["JobId"], jobEvent.JobId));
    }

    private static JobEvent CreateEvent()
      => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        EventSequence: 7,
        JobEventKind.Log,
        DateTimeOffset.UtcNow,
        AttemptNumber: 1,
        Message: "line");
}
