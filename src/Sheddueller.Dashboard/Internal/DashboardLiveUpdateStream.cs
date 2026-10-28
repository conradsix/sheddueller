namespace Sheddueller.Dashboard.Internal;

using System.Diagnostics.CodeAnalysis;

using Microsoft.Extensions.Logging;

using Sheddueller.Storage;

internal sealed class DashboardLiveUpdateStream(ILogger<DashboardLiveUpdateStream> logger)
{
    public event Func<JobEvent, CancellationToken, ValueTask>? JobEventPublished;

    /// <summary>
    /// Delivers the event to every subscriber at once rather than one after another. Subscribers are mostly dashboard
    /// circuits, each marshalling onto its own renderer, so a slow circuit must not hold up the others. Each subscriber
    /// is started in subscription order (a circuit still receives its events in order); a subscriber's failure is
    /// logged and doesn't affect the rest.
    /// </summary>
    public async ValueTask NotifyAsync(
        JobEvent jobEvent,
        CancellationToken cancellationToken)
    {
        var handlers = this.JobEventPublished;
        if (handlers is null)
        {
            return;
        }

        List<Task>? pending = null;
        foreach (var handler in handlers.GetInvocationList().Cast<Func<JobEvent, CancellationToken, ValueTask>>())
        {
            var delivery = this.DeliverAsync(handler, jobEvent, cancellationToken);
            if (!delivery.IsCompleted)
            {
                (pending ??= []).Add(delivery);
            }
        }

        if (pending is not null)
        {
            await Task.WhenAll(pending).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "One subscriber's failure must not stop live updates reaching the others; it is logged.")]
    private async Task DeliverAsync(
        Func<JobEvent, CancellationToken, ValueTask> handler,
        JobEvent jobEvent,
        CancellationToken cancellationToken)
    {
        try
        {
            await handler(jobEvent, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.DashboardJobEventPublishFailed(exception, jobEvent.JobId, jobEvent.EventSequence);
        }
    }
}
