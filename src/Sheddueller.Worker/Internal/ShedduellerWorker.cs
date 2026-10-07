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

internal sealed class ShedduellerWorker(
    IServiceProvider serviceProvider,
    IServiceScopeFactory scopeFactory,
    IOptions<ShedduellerOptions> options,
    TimeProvider timeProvider,
    IShedduellerWakeSignal wakeSignal,
    IShedduellerNodeIdProvider nodeIdProvider,
    IJobEventSink jobEventSink,
    ILogger<ShedduellerWorker> logger,
    ILogger<JobContext> jobContextLogger) : BackgroundService
{
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly IOptions<ShedduellerOptions> _options = options;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly IShedduellerWakeSignal _wakeSignal = wakeSignal;
    private readonly IShedduellerNodeIdProvider _nodeIdProvider = nodeIdProvider;
    private readonly ILogger<ShedduellerWorker> _logger = logger;
    private readonly ConcurrentDictionary<Task, byte> _runningJobs = new();
    private readonly ClaimedJobExecutor _executor = new(
      options,
      timeProvider,
      nodeIdProvider,
      logger,
      new JobInvoker(scopeFactory, nodeIdProvider, jobEventSink, logger, jobContextLogger));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var store = this._serviceProvider.GetRequiredService<IJobStore>();
        this._logger.WorkerStarted(this._nodeIdProvider.NodeId);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                this.PruneCompletedJobs();
                await this.RunPeriodicStoreWorkAsync(store, stoppingToken).ConfigureAwait(false);

                var claimedJob = false;
                DateTimeOffset? nextClaimAtUtc = null;
                while (!stoppingToken.IsCancellationRequested && this._runningJobs.Count < this._options.Value.MaxConcurrentExecutionsPerNode)
                {
                    var now = this._timeProvider.GetUtcNow();
                    var claimResult = await store
                        .TryClaimNextAsync(new ClaimJobRequest(this._nodeIdProvider.NodeId, now, now.Add(this._options.Value.LeaseDuration)), stoppingToken)
                        .ConfigureAwait(false);

                    if (claimResult is not ClaimJobResult.Claimed claimed)
                    {
                        nextClaimAtUtc = ((ClaimJobResult.NoJobAvailable)claimResult).NextClaimAtUtc;
                        break;
                    }

                    claimedJob = true;
                    this._logger.JobClaimed(claimed.Job.JobId, claimed.Job.AttemptCount, this._nodeIdProvider.NodeId);
                    this.TrackRunningJob(this._executor.ExecuteAsync(store, claimed.Job, stoppingToken));
                }

                if (claimedJob)
                {
                    continue;
                }

                await this.WaitForWorkOrCapacityAsync(nextClaimAtUtc, stoppingToken).ConfigureAwait(false);
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

        await this.WaitForRunningJobsAsync().ConfigureAwait(false);
        this._logger.WorkerStopped(this._nodeIdProvider.NodeId);
    }

    private async ValueTask WaitForWorkOrCapacityAsync(
        DateTimeOffset? nextClaimAtUtc,
        CancellationToken stoppingToken)
    {
        var timeout = CalculateWaitTimeout(
          this._options.Value.IdlePollingInterval,
          this._timeProvider.GetUtcNow(),
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

        var delayTask = Task.Delay(timeout, stoppingToken);
        var signalTask = this._wakeSignal.WaitAsync(timeout, stoppingToken).AsTask();
        var completedRunningTask = await Task.WhenAny(this._runningJobs.Keys.Append(delayTask).Append(signalTask)).ConfigureAwait(false);

        if (completedRunningTask == signalTask)
        {
            await signalTask.ConfigureAwait(false);
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

    private void TrackRunningJob(Task executionTask)
    {
        this._runningJobs.TryAdd(executionTask, 0);
        executionTask.ContinueWith(
            completedTask => this._runningJobs.TryRemove(completedTask, out _),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async ValueTask RunPeriodicStoreWorkAsync(IJobStore store, CancellationToken cancellationToken)
    {
        var now = this._timeProvider.GetUtcNow();
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

}
