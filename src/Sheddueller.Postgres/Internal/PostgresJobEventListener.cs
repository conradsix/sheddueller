namespace Sheddueller.Postgres.Internal;

using System.Globalization;
using System.Threading.Channels;

using Microsoft.Extensions.Logging;

using Sheddueller.Postgres.Internal.Operations;
using Sheddueller.Runtime;
using Sheddueller.Storage;

internal sealed class PostgresJobEventListener(
    PostgresOperationContext context,
    IJobEventNotifier publisher,
    ILogger<PostgresJobEventListener> logger) : IShedduellerJobEventListener
{
    /// <summary>
    /// The most notifications that may wait to be published; further notifications are dropped until the publisher catches up.
    /// </summary>
    internal const int PendingNotificationCapacity = 10_000;

    /// <summary>
    /// The most pending notifications read from the database in one query.
    /// </summary>
    internal const int PublishBatchSize = 256;

    private readonly Channel<(Guid JobId, long EventSequence)> _pendingNotifications = Channel.CreateBounded<(Guid JobId, long EventSequence)>(
      new BoundedChannelOptions(PendingNotificationCapacity)
      {
          FullMode = BoundedChannelFullMode.Wait,
          SingleReader = true,
      });

    public async Task ListenAsync(CancellationToken cancellationToken)
    {
        var listenLoop = new PostgresListenLoop(
          context.Options.DataSource,
          PostgresNames.JobEventChannel,
          this.HandleNotificationPayload,
          () => logger.PostgresJobEventListenerStarted(context.Options.SchemaName),
          (exception, retryDelay) => logger.PostgresJobEventListenerRetrying(exception, context.Options.SchemaName, (long)retryDelay.TotalMilliseconds),
          PostgresListenLoop.DefaultKeepAliveInterval,
          PostgresListenLoop.DefaultRetryDelay);

        var publishTask = this.PublishPendingNotificationsAsync(cancellationToken);
        await listenLoop.RunAsync(cancellationToken).ConfigureAwait(false);
        await publishTask.ConfigureAwait(false);
    }

    /// <summary>
    /// Queues a notification for publishing. Runs on the listening connection's read path, so it never blocks:
    /// live events are best-effort, and a notification that doesn't fit in the queue is dropped.
    /// </summary>
    internal void HandleNotificationPayload(string payload)
    {
        if (!TryParsePayload(payload, context.Options.SchemaName, out var jobId, out var eventSequence))
        {
            logger.PostgresJobEventNotificationPayloadInvalid(context.Options.SchemaName);
            return;
        }

        if (!this._pendingNotifications.Writer.TryWrite((jobId, eventSequence)))
        {
            logger.PostgresJobEventNotificationDropped(jobId, eventSequence, PendingNotificationCapacity);
        }
    }

    private async Task PublishPendingNotificationsAsync(CancellationToken cancellationToken)
    {
        var reader = this._pendingNotifications.Reader;
        var batch = new List<(Guid JobId, long EventSequence)>(PublishBatchSize);

        try
        {
            while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (batch.Count < PublishBatchSize && reader.TryRead(out var notification))
                {
                    batch.Add(notification);
                }

                await this.PublishBatchAsync(batch, cancellationToken).ConfigureAwait(false);
                batch.Clear();
            }
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down: notifications still queued are dropped rather than published after the listener stops.
        }
    }

    private async Task PublishBatchAsync(
        List<(Guid JobId, long EventSequence)> batch,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<JobEvent?> jobEvents;
        try
        {
            jobEvents = await PostgresJobEvents.ReadEventsAsync(context, batch, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.PostgresJobEventNotificationBatchFailed(exception, context.Options.SchemaName, batch.Count);
            return;
        }

        // Notifications arrive in commit order, and a job's events are appended under its row lock, so publishing in
        // arrival order keeps each job's events in sequence order.
        for (var i = 0; i < batch.Count; i++)
        {
            var (jobId, eventSequence) = batch[i];
            if (jobEvents[i] is not { } jobEvent)
            {
                logger.PostgresJobEventNotificationMissing(jobId, eventSequence);
                continue;
            }

            try
            {
                await publisher.NotifyAsync(jobEvent, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                logger.PostgresJobEventNotificationFailed(exception, jobId, eventSequence);
            }
        }
    }

    private static bool TryParsePayload(
        string payload,
        string schemaName,
        out Guid jobId,
        out long eventSequence)
    {
        jobId = default;
        eventSequence = default;
        var parts = payload.Split('|', StringSplitOptions.None);
        return parts.Length == 3
          && string.Equals(parts[0], schemaName, StringComparison.Ordinal)
          && Guid.TryParseExact(parts[1], "N", out jobId)
          && long.TryParse(parts[2], CultureInfo.InvariantCulture, out eventSequence);
    }
}
