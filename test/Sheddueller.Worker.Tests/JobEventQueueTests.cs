namespace Sheddueller.Worker.Tests;

using System.Collections.Concurrent;

using Microsoft.Extensions.Logging;

using Sheddueller.Storage;
using Sheddueller.Tests.Logging;
using Sheddueller.Worker.Internal;

using Shouldly;

public sealed class JobEventQueueTests
{
    private const int JobEventsDroppedEventId = 1160;

    [Fact]
    public void JobEventQueue_Full_DropsNewEventsAndCountsThem()
    {
        var queue = new ShedduellerJobLogEventQueue(capacity: 2);
        var events = Enumerable.Range(0, 4).Select(CreateEvent).ToArray();

        events.Select(queue.TryEnqueue).ShouldBe([true, true, false, false]);

        queue.TakeDroppedCount().ShouldBe(2);
        queue.TakeDroppedCount().ShouldBe(0);
        queue.Reader.TryRead(out var first).ShouldBeTrue();
        queue.Reader.TryRead(out var second).ShouldBeTrue();
        queue.Reader.TryRead(out _).ShouldBeFalse();
        first.ShouldBe(events[0]);
        second.ShouldBe(events[1]);
    }

    [Fact]
    public async Task JobEventDispatcher_EventsDropped_FlushesKeptEventsAndLogsDropCountAtDebug()
    {
        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var logs = new TestLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder
          .SetMinimumLevel(LogLevel.Trace)
          .AddProvider(logs));
        var queue = new ShedduellerJobLogEventQueue(capacity: 1);
        var eventSink = new CollectingJobEventSink();
        var kept = CreateEvent(0);
        queue.TryEnqueue(kept).ShouldBeTrue();
        queue.TryEnqueue(CreateEvent(1)).ShouldBeFalse();
        using var dispatcher = new ShedduellerJobLogEventDispatcher(
          queue,
          eventSink,
          TimeProvider.System,
          loggerFactory.CreateLogger<ShedduellerJobLogEventDispatcher>());

        await dispatcher.StartAsync(cancellationTokenSource.Token);
        await dispatcher.StopAsync(cancellationTokenSource.Token);

        eventSink.Requests.ShouldHaveSingleItem().ShouldBe(kept);
        var entry = logs.SingleByEventId(JobEventsDroppedEventId);
        entry.Level.ShouldBe(LogLevel.Debug);
        entry.Properties["DroppedCount"].ShouldBe(1L);
        entry.Properties["Capacity"].ShouldBe(1);
    }

    private static AppendJobEventRequest CreateEvent(int index)
      => new(Guid.NewGuid(), JobEventKind.Progress, AttemptNumber: 1, ProgressPercent: index);

    private sealed class CollectingJobEventSink : IJobEventSink
    {
        private readonly ConcurrentQueue<AppendJobEventRequest> _requests = new();

        public IReadOnlyList<AppendJobEventRequest> Requests
          => [.. this._requests];

        public ValueTask<JobEvent> AppendAsync(
            AppendJobEventRequest request,
            CancellationToken cancellationToken = default)
        {
            this._requests.Enqueue(request);
            return ValueTask.FromResult(new JobEvent(
              Guid.NewGuid(),
              request.JobId,
              this._requests.Count,
              request.Kind,
              DateTimeOffset.UtcNow,
              request.AttemptNumber,
              request.LogLevel,
              request.Message,
              request.ProgressPercent,
              request.Fields));
        }
    }
}
