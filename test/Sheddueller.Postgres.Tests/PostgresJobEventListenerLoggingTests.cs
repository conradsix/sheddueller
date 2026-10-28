namespace Sheddueller.Postgres.Tests;

using Microsoft.Extensions.Logging;

using Sheddueller.Postgres.Internal;
using Sheddueller.Storage;
using Sheddueller.Tests.Logging;

using Shouldly;

public sealed class PostgresJobEventListenerLoggingTests
{
    [Fact]
    public void Notification_InvalidPayload_LogsIgnoredPayload()
    {
        using var logs = new TestLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder
          .SetMinimumLevel(LogLevel.Trace)
          .AddProvider(logs));
        var listener = new PostgresJobEventListener(
          new ShedduellerPostgresOptions
          {
              DataSource = null!,
              SchemaName = "sheddueller",
          },
          new NoOpJobEventNotifier(),
          loggerFactory.CreateLogger<PostgresJobEventListener>());

        listener.HandleNotificationPayload("not-a-valid-payload");

        var entry = logs.SingleByEventId(1212);
        entry.Level.ShouldBe(LogLevel.Debug);
        entry.Properties["SchemaName"].ShouldBe("sheddueller");
        entry.MessageTemplate.ShouldBe("Ignored PostgreSQL job-event notification with invalid payload for schema {SchemaName}.");
    }

    [Fact]
    public void Notification_PendingQueueFull_DropsAndLogsNotification()
    {
        using var logs = new TestLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder
          .SetMinimumLevel(LogLevel.Trace)
          .AddProvider(logs));
        var listener = new PostgresJobEventListener(
          new ShedduellerPostgresOptions
          {
              DataSource = null!,
              SchemaName = "sheddueller",
          },
          new NoOpJobEventNotifier(),
          loggerFactory.CreateLogger<PostgresJobEventListener>());
        var jobId = Guid.NewGuid();

        // Nothing is publishing, so the queue fills; the notification callback must drop rather than block.
        for (var sequence = 1L; sequence <= PostgresJobEventListener.PendingNotificationCapacity + 1; sequence++)
        {
            listener.HandleNotificationPayload($"sheddueller|{jobId:N}|{sequence}");
        }

        var entry = logs.SingleByEventId(1215);
        entry.Level.ShouldBe(LogLevel.Debug);
        entry.Properties["JobId"].ShouldBe(jobId);
        entry.Properties["EventSequence"].ShouldBe(PostgresJobEventListener.PendingNotificationCapacity + 1L);
    }

    private sealed class NoOpJobEventNotifier : IJobEventNotifier
    {
        public ValueTask NotifyAsync(
            JobEvent jobEvent,
            CancellationToken cancellationToken = default)
          => ValueTask.CompletedTask;
    }
}
