namespace Sheddueller.Worker.Internal;

using System.Diagnostics.CodeAnalysis;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Sheddueller.Logging;
using Sheddueller.Storage;

internal sealed class ShedduellerJobLogEventDispatcher(
    ShedduellerJobLogEventQueue queue,
    IJobEventSink eventSink,
    TimeProvider timeProvider,
    ILogger<ShedduellerJobLogEventDispatcher> logger) : BackgroundService
{
    private const int MaxBatchSize = 64;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(250);

    private readonly ShedduellerJobLogEventQueue _queue = queue;
    private readonly IJobEventSink _eventSink = eventSink;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger<ShedduellerJobLogEventDispatcher> _logger = logger;

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);

        // BackgroundService starts ExecuteAsync on the thread pool and skips it altogether when stopped before it gets a
        // thread, so the drain in ExecuteAsync may never run. Drain what is still queued here too, but only once
        // ExecuteAsync has finished: the queue has a single reader.
        if (this.ExecuteTask is null || this.ExecuteTask.IsCompleted)
        {
            var remaining = new List<AppendJobEventRequest>();
            this.DrainAvailable(remaining, int.MaxValue);
            await this.FlushAsync(remaining)
                .AsTask()
                .WaitAsync(cancellationToken)
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batch = new List<AppendJobEventRequest>(MaxBatchSize);

        try
        {
            while (await this._queue.Reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false))
            {
                this.DrainAvailable(batch);
                if (batch.Count < MaxBatchSize)
                {
                    await Task.Delay(FlushInterval, this._timeProvider, stoppingToken).ConfigureAwait(false);
                    this.DrainAvailable(batch);
                }

                await this.FlushAsync(batch).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown drains any queued log events below.
        }
        finally
        {
            this.DrainAvailable(batch, int.MaxValue);
            await this.FlushAsync(batch).ConfigureAwait(false);
        }
    }

    private void DrainAvailable(List<AppendJobEventRequest> batch, int maxBatchSize = MaxBatchSize)
    {
        while (batch.Count < maxBatchSize && this._queue.Reader.TryRead(out var request))
        {
            batch.Add(request);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Captured job logging is best-effort and must not fail the dispatcher.")]
    private async ValueTask FlushAsync(List<AppendJobEventRequest> batch)
    {
        try
        {
            foreach (var request in batch)
            {
                try
                {
                    await this._eventSink.AppendAsync(request).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    this._logger.JobEventAppendFailed(exception, request.JobId);
                }
            }
        }
        finally
        {
            batch.Clear();
            this.ReportDroppedEvents();
        }
    }

    // The queue only drops while it is full, so a flush always follows a drop and reports it here, outside any job.
    private void ReportDroppedEvents()
    {
        var droppedCount = this._queue.TakeDroppedCount();
        if (droppedCount > 0)
        {
            this._logger.JobEventsDropped(droppedCount, this._queue.Capacity);
        }
    }
}
