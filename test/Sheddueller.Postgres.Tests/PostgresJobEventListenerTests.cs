namespace Sheddueller.Postgres.Tests;

using System.Collections.Concurrent;

using Microsoft.Extensions.Logging;

using Npgsql;

using Sheddueller.Postgres.Internal;
using Sheddueller.Storage;
using Sheddueller.Tests.Logging;

using Shouldly;

public sealed class PostgresJobEventListenerTests(PostgresFixture fixture)
{
    private const int ListenerStartedEventId = 1210;

    [Fact]
    public async Task JobEvents_Burst_PublishesEachEventOnceInPerJobOrderOverTwoConnections()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        var physicalConnections = 0;
        var listenerDataSourceBuilder = new NpgsqlDataSourceBuilder(fixture.ConnectionString);
        listenerDataSourceBuilder.UsePhysicalConnectionInitializer(
          _ => Interlocked.Increment(ref physicalConnections),
          _ =>
          {
              Interlocked.Increment(ref physicalConnections);
              return Task.CompletedTask;
          });
        await using var listenerDataSource = listenerDataSourceBuilder.Build();
        using var logs = new TestLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder
          .SetMinimumLevel(LogLevel.Trace)
          .AddProvider(logs));
        var notifier = new RecordingJobEventNotifier();
        var listener = new PostgresJobEventListener(
          new ShedduellerPostgresOptions
          {
              DataSource = listenerDataSource,
              SchemaName = context.SchemaName,
          },
          notifier,
          loggerFactory.CreateLogger<PostgresJobEventListener>());
        using var stopping = new CancellationTokenSource();
        var listenTask = listener.ListenAsync(stopping.Token);
        await WaitUntilAsync(() => logs.HasEventId(ListenerStartedEventId));

        // One bulk enqueue notifies an event per job in a single transaction; the per-job appends then interleave.
        var jobIds = Enumerable.Range(0, 10).Select(_ => Guid.NewGuid()).ToArray();
        await context.Store.EnqueueManyAsync([.. jobIds.Select(jobId => PostgresTestData.CreateRequest(jobId))]);
        var eventSink = (IJobEventSink)context.Store;
        await Task.WhenAll(jobIds.Select(async jobId =>
        {
            for (var i = 0; i < 49; i++)
            {
                await eventSink.AppendAsync(new AppendJobEventRequest(jobId, JobEventKind.Log, AttemptNumber: 0, LogLevel: JobLogLevel.Information, Message: $"event {i}"));
            }
        }));

        var persisted = await ReadPersistedEventKeysAsync(context, jobIds);
        persisted.Count.ShouldBe(500);
        await WaitUntilAsync(() => notifier.Events.Count >= persisted.Count);

        // Give any duplicate deliveries a chance to arrive before asserting on the exact set.
        await Task.Delay(TimeSpan.FromMilliseconds(250));
        var published = notifier.Events;
        published.Select(static jobEvent => (jobEvent.JobId, jobEvent.EventSequence)).ShouldBe(persisted, ignoreOrder: true);
        foreach (var jobEvents in published.GroupBy(static jobEvent => jobEvent.JobId))
        {
            var sequences = jobEvents.Select(static jobEvent => jobEvent.EventSequence).ToArray();
            sequences.ShouldBe([.. sequences.Order()], $"Events for job {jobEvents.Key} were published out of order.");
        }

        // One connection listens and one reads each batch; publishing never fans out a connection per notification.
        Volatile.Read(ref physicalConnections).ShouldBeLessThanOrEqualTo(2);

        await stopping.CancelAsync();
        await listenTask.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static async ValueTask<List<(Guid JobId, long EventSequence)>> ReadPersistedEventKeysAsync(
        PostgresTestContext context,
        Guid[] jobIds)
    {
        await using var command = context.DataSource.CreateCommand(
          $"""
          select job_id, event_sequence
          from {context.Table("job_events")}
          where job_id = any(@job_ids);
          """);
        command.Parameters.AddWithValue("job_ids", jobIds);

        var keys = new List<(Guid JobId, long EventSequence)>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            keys.Add((reader.GetGuid(0), reader.GetInt64(1)));
        }

        return keys;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20), timeout.Token);
        }
    }

    private sealed class RecordingJobEventNotifier : IJobEventNotifier
    {
        private readonly ConcurrentQueue<JobEvent> _events = new();

        public IReadOnlyList<JobEvent> Events
          => [.. this._events];

        public ValueTask NotifyAsync(
            JobEvent jobEvent,
            CancellationToken cancellationToken = default)
        {
            this._events.Enqueue(jobEvent);
            return ValueTask.CompletedTask;
        }
    }
}
