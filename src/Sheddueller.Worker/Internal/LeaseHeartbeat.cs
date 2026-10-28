namespace Sheddueller.Worker.Internal;

using System.Diagnostics.CodeAnalysis;

using Microsoft.Extensions.Logging;

using Sheddueller.Storage;

/// <summary>
/// Renews a running job's lease every heartbeat interval and checks for cancellation requests. It cancels the job's
/// execution when the lease is lost, when cancellation is requested, or when renewals keep failing until the lease has
/// presumably expired.
/// </summary>
internal sealed class LeaseHeartbeat
{
    private readonly IJobStore _store;
    private readonly ClaimedJob _job;
    private readonly CancellationTokenSource _executionTokenSource;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _heartbeatInterval;
    private readonly TimeSpan _leaseDuration;
    private readonly string _nodeId;
    private readonly ILogger _logger;
    private readonly Task _renewal;
    private DateTimeOffset? _cancellationRequestedAtUtc;

    private LeaseHeartbeat(
        IJobStore store,
        ClaimedJob job,
        DateTimeOffset claimedAtUtc,
        CancellationTokenSource executionTokenSource,
        ShedduellerOptions options,
        TimeProvider timeProvider,
        string nodeId,
        ILogger logger)
    {
        this._store = store;
        this._job = job;
        this._executionTokenSource = executionTokenSource;
        this._timeProvider = timeProvider;
        this._heartbeatInterval = options.HeartbeatInterval;
        this._leaseDuration = options.LeaseDuration;
        this._nodeId = nodeId;
        this._logger = logger;
        this._renewal = this.RenewUntilStoppedAsync(claimedAtUtc);
    }

    /// <summary>
    /// Gets a value indicating whether the heartbeat cancelled the execution because cancellation was requested, rather
    /// than because the lease was lost or the worker is stopping.
    /// </summary>
    public bool CancellationRequested
      => this._cancellationRequestedAtUtc is not null;

    /// <summary>
    /// Starts renewing the lease of a job claimed at <paramref name="claimedAtUtc"/>.
    /// </summary>
    public static LeaseHeartbeat Start(
        IJobStore store,
        ClaimedJob job,
        DateTimeOffset claimedAtUtc,
        CancellationTokenSource executionTokenSource,
        ShedduellerOptions options,
        TimeProvider timeProvider,
        string nodeId,
        ILogger logger)
      => new(store, job, claimedAtUtc, executionTokenSource, options, timeProvider, nodeId, logger);

    /// <summary>
    /// Waits for the heartbeat to finish once the execution token has been cancelled.
    /// </summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Heartbeat failures should not fault the job execution cleanup path.")]
    public async ValueTask StopAsync()
    {
        try
        {
            await this._renewal.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            this._logger.JobHeartbeatFailed(exception, this._job.JobId, this._job.AttemptCount, this._nodeId);
            // Failure to renew will be handled by lease expiry recovery.
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A failed heartbeat is retried; the job is cancelled only once its lease has presumably expired.")]
    private async Task RenewUntilStoppedAsync(DateTimeOffset claimedAtUtc)
    {
        var job = this._job;

        // Estimated from local time taken before each request; the store starts its lease no earlier (clock skew aside).
        var leaseExpiresAtUtc = claimedAtUtc.Add(this._leaseDuration);
        var delay = this._heartbeatInterval;

        try
        {
            while (!this._executionTokenSource.IsCancellationRequested)
            {
                await Task.Delay(delay, this._timeProvider, this._executionTokenSource.Token).ConfigureAwait(false);

                var now = this._timeProvider.GetUtcNow();
                try
                {
                    var renewed = await this._store
                        .RenewLeaseAsync(
                            new RenewLeaseRequest(job.JobId, this._nodeId, job.LeaseToken, now, now.Add(this._leaseDuration)),
                            CancellationToken.None)
                        .ConfigureAwait(false);

                    if (!renewed)
                    {
                        this._logger.JobLeaseRenewalLost(job.JobId, job.AttemptCount, this._nodeId);
                        await this._executionTokenSource.CancelAsync().ConfigureAwait(false);
                        return;
                    }

                    leaseExpiresAtUtc = now.Add(this._leaseDuration);
                    delay = this._heartbeatInterval;

                    var cancellationRequestedAtUtc = await this._store
                        .GetCancellationRequestedAtAsync(
                            new JobCancellationStatusRequest(job.JobId, this._nodeId, job.LeaseToken, now),
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    if (cancellationRequestedAtUtc is not null && this._cancellationRequestedAtUtc is null)
                    {
                        // Recorded before cancelling, so the job sees it when it observes the cancellation.
                        this._cancellationRequestedAtUtc = cancellationRequestedAtUtc;
                        this._logger.JobCancellationRequestedObserved(job.JobId, job.AttemptCount, this._nodeId);
                        await this._executionTokenSource.CancelAsync().ConfigureAwait(false);
                        return;
                    }
                }
                catch (Exception exception)
                {
                    // Keep the job running while the lease may still hold. Once it has expired another node can claim
                    // the job, so stop this execution rather than run it twice.
                    var failedAtUtc = this._timeProvider.GetUtcNow();
                    if (failedAtUtc >= leaseExpiresAtUtc)
                    {
                        this._logger.JobLeasePresumedLost(exception, job.JobId, job.AttemptCount, this._nodeId, leaseExpiresAtUtc);
                        await this._executionTokenSource.CancelAsync().ConfigureAwait(false);
                        return;
                    }

                    this._logger.JobHeartbeatRetrying(exception, job.JobId, job.AttemptCount, this._nodeId, leaseExpiresAtUtc);

                    // Retry on the next tick, but no later than the expiry so the job is stopped on time.
                    var untilExpiry = leaseExpiresAtUtc - failedAtUtc;
                    delay = untilExpiry < this._heartbeatInterval ? untilExpiry : this._heartbeatInterval;
                }
            }
        }
        catch (OperationCanceledException) when (this._executionTokenSource.IsCancellationRequested)
        {
            // Expected when the job completes, the host stops, or ownership is lost.
        }
    }
}
