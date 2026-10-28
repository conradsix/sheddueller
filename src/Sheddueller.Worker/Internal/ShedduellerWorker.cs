namespace Sheddueller.Worker.Internal;

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Sheddueller;
using Sheddueller.Runtime;
using Sheddueller.Storage;

/// <summary>
/// The worker loop: claims jobs up to the node's capacity, runs each on the thread pool under a lease heartbeat, records
/// its outcome, and does the periodic cluster-wide store work.
/// </summary>
internal sealed class ShedduellerWorker(
    IServiceProvider serviceProvider,
    IServiceScopeFactory scopeFactory,
    IOptions<ShedduellerOptions> options,
    TimeProvider timeProvider,
    IShedduellerWakeSignal wakeSignal,
    IShedduellerNodeIdProvider nodeIdProvider,
    ShedduellerJobLogEventQueue jobEventQueue,
    ILogger<ShedduellerWorker> logger) : BackgroundService
{
    private static readonly TimeSpan LoopRetryBaseDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan LoopRetryMaxDelay = TimeSpan.FromSeconds(30);

    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly IOptions<ShedduellerOptions> _options = options;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly IShedduellerWakeSignal _wakeSignal = wakeSignal;
    private readonly IShedduellerNodeIdProvider _nodeIdProvider = nodeIdProvider;
    private readonly ILogger<ShedduellerWorker> _logger = logger;
    private readonly JobInvoker _jobInvoker = new(scopeFactory, jobEventQueue, nodeIdProvider.NodeId, logger);
    private readonly ConcurrentDictionary<Task, byte> _runningJobs = new();
    private DateTimeOffset _nextPeriodicStoreWorkAtUtc = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var store = this._serviceProvider.GetRequiredService<IJobStore>();
        this._logger.WorkerStarted(this._nodeIdProvider.NodeId);

        try
        {
            var consecutiveFailures = 0;
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await this.RunClaimCycleAsync(store, stoppingToken).ConfigureAwait(false);
                    consecutiveFailures = 0;
                }
                catch (Exception exception) when (exception is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    // A store outage must not stop the worker (by default the host would stop with it) or abandon the
                    // jobs it is running: back off and try again.
                    consecutiveFailures++;
                    var retryDelay = CalculateLoopRetryDelay(consecutiveFailures);
                    this._logger.WorkerLoopFailed(exception, this._nodeIdProvider.NodeId, (long)retryDelay.TotalMilliseconds);
                    await Task.Delay(retryDelay, this._timeProvider, stoppingToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown stops claiming. Running jobs are awaited below so terminal state can be recorded.
        }
        catch (Exception exception)
        {
            this._logger.WorkerFailed(exception, this._nodeIdProvider.NodeId);
            throw;
        }
        finally
        {
            await this.WaitForRunningJobsAsync().ConfigureAwait(false);
        }

        this._logger.WorkerStopped(this._nodeIdProvider.NodeId);
    }

    private async Task RunClaimCycleAsync(IJobStore store, CancellationToken stoppingToken)
    {
        this.PruneCompletedJobs();
        await this.RunPeriodicStoreWorkIfDueAsync(store, stoppingToken).ConfigureAwait(false);

        var claimedJob = false;
        DateTimeOffset? nextClaimAtUtc = null;
        while (!stoppingToken.IsCancellationRequested && this._runningJobs.Count < this._options.Value.MaxConcurrentExecutionsPerNode)
        {
            var claimedAtUtc = this._timeProvider.GetUtcNow();
            var claimResult = await store
                .TryClaimNextAsync(new ClaimJobRequest(this._nodeIdProvider.NodeId, claimedAtUtc, claimedAtUtc.Add(this._options.Value.LeaseDuration)), stoppingToken)
                .ConfigureAwait(false);

            if (claimResult is not ClaimJobResult.Claimed claimed)
            {
                nextClaimAtUtc = ((ClaimJobResult.NoJobAvailable)claimResult).NextClaimAtUtc;
                break;
            }

            claimedJob = true;
            this._logger.JobClaimed(claimed.Job.JobId, claimed.Job.AttemptCount, this._nodeIdProvider.NodeId);

            // Start the job on the thread pool. Called inline, everything a job does before its first await would run on
            // this loop, so synchronous or CPU-bound jobs would block claiming and run one at a time.
            var job = claimed.Job;
            this._runningJobs.TryAdd(
                Task.Run(() => this.ExecuteClaimedJobAsync(store, job, claimedAtUtc, stoppingToken), CancellationToken.None),
                0);
        }

        if (!claimedJob)
        {
            await this.WaitForWorkOrCapacityAsync(nextClaimAtUtc, stoppingToken).ConfigureAwait(false);
        }
    }

    internal static TimeSpan CalculateLoopRetryDelay(int consecutiveFailures)
    {
        // 1 s, 2 s, 4 s, ... up to 30 s. Clamping the exponent keeps the multiplication in range.
        var delay = LoopRetryBaseDelay * Math.Pow(2, Math.Clamp(consecutiveFailures - 1, 0, 5));
        return delay < LoopRetryMaxDelay ? delay : LoopRetryMaxDelay;
    }

    private async ValueTask WaitForWorkOrCapacityAsync(
        DateTimeOffset? nextClaimAtUtc,
        CancellationToken stoppingToken)
    {
        // Wake no later than the next periodic store work, so an idle worker keeps doing it every polling interval.
        var now = this._timeProvider.GetUtcNow();
        var idlePollingInterval = this._options.Value.IdlePollingInterval;
        var untilPeriodicStoreWork = this._nextPeriodicStoreWorkAtUtc - now;
        var timeout = CalculateWaitTimeout(
          untilPeriodicStoreWork < idlePollingInterval ? untilPeriodicStoreWork : idlePollingInterval,
          now,
          nextClaimAtUtc);
        if (timeout <= TimeSpan.Zero)
        {
            return;
        }

        if (this._runningJobs.IsEmpty)
        {
            await this._wakeSignal.WaitAsync(timeout, stoppingToken).ConfigureAwait(false);
            return;
        }

        // A running job finishing frees capacity, so wake for that too. Then cancel the signal wait: left pending, it
        // would consume the next wake-up and the following wait would sleep through it.
        using var signalWaitTokenSource = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var signalTask = this._wakeSignal.WaitAsync(timeout, signalWaitTokenSource.Token).AsTask();
        await Task.WhenAny(this._runningJobs.Keys.Append(signalTask)).ConfigureAwait(false);
        await signalWaitTokenSource.CancelAsync().ConfigureAwait(false);

        try
        {
            await signalTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (signalWaitTokenSource.IsCancellationRequested)
        {
            // Cancelled above, or the worker is stopping; the loop checks the stopping token.
        }
    }

    internal static TimeSpan CalculateWaitTimeout(
        TimeSpan idlePollingInterval,
        DateTimeOffset nowUtc,
        DateTimeOffset? nextClaimAtUtc)
    {
        if (nextClaimAtUtc is not { } nextClaim)
        {
            return idlePollingInterval;
        }

        var rateDelay = nextClaim - nowUtc;
        return rateDelay <= TimeSpan.Zero
          ? TimeSpan.Zero
          : TimeSpan.FromTicks(Math.Min(idlePollingInterval.Ticks, rateDelay.Ticks));
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A job's exception is its outcome, recorded in the store instead of escaping the worker.")]
    private async Task ExecuteClaimedJobAsync(IJobStore store, ClaimedJob job, DateTimeOffset claimedAtUtc, CancellationToken stoppingToken)
    {
        var executionTokenSource = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var heartbeat = LeaseHeartbeat.Start(
            store,
            job,
            claimedAtUtc,
            executionTokenSource,
            this._options.Value,
            this._timeProvider,
            this._nodeIdProvider.NodeId,
            this._logger);

        JobOutcome outcome;
        Exception? failure = null;
        try
        {
            await this._jobInvoker.InvokeAsync(job, executionTokenSource.Token).ConfigureAwait(false);
            outcome = JobOutcome.Completed;
        }
        catch (OperationCanceledException) when (executionTokenSource.IsCancellationRequested)
        {
            outcome = heartbeat.CancellationRequested ? JobOutcome.CancellationObserved : JobOutcome.Released;
        }
        catch (Exception exception)
        {
            this._logger.JobFailed(exception, job.JobId, job.AttemptCount, this._nodeIdProvider.NodeId);
            outcome = JobOutcome.Failed;
            failure = exception;
        }
        finally
        {
            await executionTokenSource.CancelAsync().ConfigureAwait(false);
            await heartbeat.StopAsync().ConfigureAwait(false);
            executionTokenSource.Dispose();
        }

        await this.RecordJobOutcomeAsync(store, job, outcome, failure).ConfigureAwait(false);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A store failure while recording an outcome is not the job's failure; lease expiry recovers the job.")]
    private async ValueTask RecordJobOutcomeAsync(IJobStore store, ClaimedJob job, JobOutcome outcome, Exception? failure)
    {
        var nodeId = this._nodeIdProvider.NodeId;
        var now = this._timeProvider.GetUtcNow();

        try
        {
            switch (outcome)
            {
                case JobOutcome.Completed:
                    if (await store.MarkCompletedAsync(new CompleteJobRequest(job.JobId, nodeId, job.LeaseToken, now), CancellationToken.None).ConfigureAwait(false))
                    {
                        this._logger.JobCompleted(job.JobId, job.AttemptCount, nodeId);
                    }

                    break;

                case JobOutcome.CancellationObserved:
                    if (await store.MarkCancellationObservedAsync(new ObserveJobCancellationRequest(job.JobId, nodeId, job.LeaseToken, now), CancellationToken.None).ConfigureAwait(false))
                    {
                        this._logger.JobCancellationObserved(job.JobId, job.AttemptCount, nodeId);
                    }

                    break;

                case JobOutcome.Released:
                    if (await store.ReleaseJobAsync(new ReleaseJobRequest(job.JobId, nodeId, job.LeaseToken, now), CancellationToken.None).ConfigureAwait(false))
                    {
                        this._logger.JobReleased(job.JobId, job.AttemptCount, nodeId);
                    }

                    break;

                case JobOutcome.Failed:
                    ArgumentNullException.ThrowIfNull(failure);
                    await store
                        .MarkFailedAsync(new FailJobRequest(job.JobId, nodeId, job.LeaseToken, now, CreateFailureInfo(failure)), CancellationToken.None)
                        .ConfigureAwait(false);
                    break;

                default:
                    throw new InvalidOperationException($"Job outcome '{outcome}' is not supported.");
            }
        }
        catch (Exception exception)
        {
            this._logger.JobOutcomeRecordFailed(exception, outcome.ToString(), job.JobId, job.AttemptCount, nodeId);
        }
    }

    private async ValueTask RunPeriodicStoreWorkIfDueAsync(IJobStore store, CancellationToken cancellationToken)
    {
        // These are cluster-wide queries. The loop comes round after every claim burst, wake-up and job completion, so
        // run them at most once per idle polling interval: the cadence an idle worker has always had.
        var now = this._timeProvider.GetUtcNow();
        if (now < this._nextPeriodicStoreWorkAtUtc)
        {
            return;
        }

        await store
            .RecordWorkerNodeHeartbeatAsync(
                new WorkerNodeHeartbeatRequest(
                    this._nodeIdProvider.NodeId,
                    now,
                    this._options.Value.MaxConcurrentExecutionsPerNode,
                    this._runningJobs.Count),
                cancellationToken)
            .ConfigureAwait(false);

        var recovered = await store
            .RecoverExpiredLeasesAsync(new RecoverExpiredLeasesRequest(now), cancellationToken)
            .ConfigureAwait(false);
        var materialized = await store
            .MaterializeDueRecurringSchedulesAsync(new MaterializeDueRecurringSchedulesRequest(now, this._options.Value.DefaultRetryPolicy), cancellationToken)
            .ConfigureAwait(false);

        if (recovered > 0 || materialized > 0)
        {
            this._wakeSignal.Notify();
            this._logger.WorkerPeriodicStoreWorkCompleted(this._nodeIdProvider.NodeId, recovered, materialized);
        }

        this._nextPeriodicStoreWorkAtUtc = now.Add(this._options.Value.IdlePollingInterval);
    }

    private void PruneCompletedJobs()
    {
        foreach (var job in this._runningJobs.Keys.Where(job => job.IsCompleted))
        {
            this._runningJobs.TryRemove(job, out _);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Individual execution jobs persist failure details before shutdown wait observes them.")]
    private async Task WaitForRunningJobsAsync()
    {
        while (!this._runningJobs.IsEmpty)
        {
            Task[] snapshot = [.. this._runningJobs.Keys];
            try
            {
                await Task.WhenAll(snapshot).ConfigureAwait(false);
            }
            catch
            {
                // Individual execution jobs record their own failure state.
            }

            this.PruneCompletedJobs();
        }
    }

    private static JobFailureInfo CreateFailureInfo(Exception exception)
        => new(
            exception.GetType().FullName ?? exception.GetType().Name,
            exception.Message,
            exception.StackTrace);

    private enum JobOutcome
    {
        Completed,
        CancellationObserved,
        Released,
        Failed,
    }
}
