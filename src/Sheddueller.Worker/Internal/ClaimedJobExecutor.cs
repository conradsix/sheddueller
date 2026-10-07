namespace Sheddueller.Worker.Internal;

using System.Diagnostics.CodeAnalysis;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Sheddueller;
using Sheddueller.Storage;

internal sealed class ClaimedJobExecutor(
    IOptions<ShedduellerOptions> options,
    TimeProvider timeProvider,
    IShedduellerNodeIdProvider nodeIdProvider,
    ILogger logger,
    JobInvoker invoker)
{
    private readonly IOptions<ShedduellerOptions> _options = options;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly IShedduellerNodeIdProvider _nodeIdProvider = nodeIdProvider;
    private readonly ILogger _logger = logger;
    private readonly JobInvoker _invoker = invoker;

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Job failures must be persisted instead of escaping the worker.")]
    public async Task ExecuteAsync(IJobStore store, ClaimedJob job, CancellationToken stoppingToken)
    {
        var executionTokenSource = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var cancellationState = new RunningJobCancellationState();
        var heartbeatTask = this.RenewLeaseUntilStoppedAsync(store, job, executionTokenSource, cancellationState);

        try
        {
            await this._invoker.InvokeAsync(job, executionTokenSource.Token).ConfigureAwait(false);
            var completed = await store
                .MarkCompletedAsync(new CompleteJobRequest(job.JobId, this._nodeIdProvider.NodeId, job.LeaseToken, this._timeProvider.GetUtcNow()), CancellationToken.None)
                .ConfigureAwait(false);
            if (completed)
            {
                this._logger.JobCompleted(job.JobId, job.AttemptCount, this._nodeIdProvider.NodeId);
            }
        }
        catch (OperationCanceledException) when (executionTokenSource.IsCancellationRequested)
        {
            if (cancellationState.CancellationRequestedAtUtc is not null)
            {
                var observed = await store
                    .MarkCancellationObservedAsync(
                        new ObserveJobCancellationRequest(job.JobId, this._nodeIdProvider.NodeId, job.LeaseToken, this._timeProvider.GetUtcNow()),
                        CancellationToken.None)
                    .ConfigureAwait(false);
                if (observed)
                {
                    this._logger.JobCancellationObserved(job.JobId, job.AttemptCount, this._nodeIdProvider.NodeId);
                }
            }
            else
            {
                var released = await store
                    .ReleaseJobAsync(new ReleaseJobRequest(job.JobId, this._nodeIdProvider.NodeId, job.LeaseToken, this._timeProvider.GetUtcNow()), CancellationToken.None)
                    .ConfigureAwait(false);
                if (released)
                {
                    this._logger.JobReleased(job.JobId, job.AttemptCount, this._nodeIdProvider.NodeId);
                }
            }
        }
        catch (Exception exception)
        {
            this._logger.JobFailed(exception, job.JobId, job.AttemptCount, this._nodeIdProvider.NodeId);
            await store
                .MarkFailedAsync(
                    new FailJobRequest(job.JobId, this._nodeIdProvider.NodeId, job.LeaseToken, this._timeProvider.GetUtcNow(), CreateFailureInfo(exception)),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            await executionTokenSource.CancelAsync().ConfigureAwait(false);
            await this.WaitForHeartbeatTaskAsync(heartbeatTask, job).ConfigureAwait(false);
            executionTokenSource.Dispose();
        }
    }

    private async Task RenewLeaseUntilStoppedAsync(
        IJobStore store,
        ClaimedJob job,
        CancellationTokenSource executionTokenSource,
        RunningJobCancellationState cancellationState)
    {
        try
        {
            while (!executionTokenSource.IsCancellationRequested)
            {
                await Task.Delay(this._options.Value.HeartbeatInterval, executionTokenSource.Token).ConfigureAwait(false);

                var now = this._timeProvider.GetUtcNow();
                var renewed = await store
                    .RenewLeaseAsync(
                        new RenewLeaseRequest(job.JobId, this._nodeIdProvider.NodeId, job.LeaseToken, now, now.Add(this._options.Value.LeaseDuration)),
                        CancellationToken.None)
                    .ConfigureAwait(false);

                if (!renewed)
                {
                    this._logger.JobLeaseRenewalLost(job.JobId, job.AttemptCount, this._nodeIdProvider.NodeId);
                    await executionTokenSource.CancelAsync().ConfigureAwait(false);
                    return;
                }

                var cancellationRequestedAtUtc = await store
                    .GetCancellationRequestedAtAsync(
                        new JobCancellationStatusRequest(job.JobId, this._nodeIdProvider.NodeId, job.LeaseToken, now),
                        CancellationToken.None)
                    .ConfigureAwait(false);
                if (cancellationRequestedAtUtc is not null && cancellationState.CancellationRequestedAtUtc is null)
                {
                    cancellationState.CancellationRequestedAtUtc = cancellationRequestedAtUtc;
                    this._logger.JobCancellationRequestedObserved(job.JobId, job.AttemptCount, this._nodeIdProvider.NodeId);
                    await executionTokenSource.CancelAsync().ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (executionTokenSource.IsCancellationRequested)
        {
            // Expected when the job completes, the host stops, or ownership is lost.
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Heartbeat failures should not fault the job execution cleanup path.")]
    private async ValueTask WaitForHeartbeatTaskAsync(Task heartbeatTask, ClaimedJob job)
    {
        try
        {
            await heartbeatTask.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            this._logger.JobHeartbeatFailed(exception, job.JobId, job.AttemptCount, this._nodeIdProvider.NodeId);
            // Failure to renew will be handled by lease expiry recovery.
        }
    }

    private static JobFailureInfo CreateFailureInfo(Exception exception)
        => new(
            exception.GetType().FullName ?? exception.GetType().Name,
            exception.Message,
            exception.StackTrace);

    private sealed class RunningJobCancellationState
    {
        public DateTimeOffset? CancellationRequestedAtUtc { get; set; }
    }
}
