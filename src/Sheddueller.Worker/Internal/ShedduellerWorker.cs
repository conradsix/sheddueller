namespace Sheddueller.Worker.Internal;

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Sheddueller;
using Sheddueller.Enqueueing;
using Sheddueller.Runtime;
using Sheddueller.Serialization;
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
    private static readonly TimeSpan LoopRetryBaseDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan LoopRetryMaxDelay = TimeSpan.FromSeconds(30);

    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly IOptions<ShedduellerOptions> _options = options;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly IShedduellerWakeSignal _wakeSignal = wakeSignal;
    private readonly IShedduellerNodeIdProvider _nodeIdProvider = nodeIdProvider;
    private readonly IJobEventSink _jobEventSink = jobEventSink;
    private readonly ILogger<ShedduellerWorker> _logger = logger;
    private readonly ILogger<JobContext> _jobContextLogger = jobContextLogger;
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
        var cancellationState = new RunningJobCancellationState();
        var heartbeatTask = this.RenewLeaseUntilStoppedAsync(store, job, claimedAtUtc, executionTokenSource, cancellationState);

        JobOutcome outcome;
        Exception? failure = null;
        try
        {
            await this.InvokeClaimedJobAsync(job, executionTokenSource.Token).ConfigureAwait(false);
            outcome = JobOutcome.Completed;
        }
        catch (OperationCanceledException) when (executionTokenSource.IsCancellationRequested)
        {
            outcome = cancellationState.CancellationRequestedAtUtc is not null ? JobOutcome.CancellationObserved : JobOutcome.Released;
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
            await this.WaitForHeartbeatTaskAsync(heartbeatTask, job).ConfigureAwait(false);
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

    private async ValueTask InvokeClaimedJobAsync(ClaimedJob job, CancellationToken executionToken)
    {
        var serviceType = TypeNameFormatter.Resolve(job.ServiceType);
        var methodParameterTypes = job.MethodParameterTypes.Select(TypeNameFormatter.Resolve).ToArray();
        var parameterBindings = NormalizeParameterBindings(methodParameterTypes, job.MethodParameterBindings);
        var serializableParameterTypes = methodParameterTypes
          .Where((_, index) => parameterBindings[index].Kind == JobMethodParameterBindingKind.Serialized)
          .ToArray();
        var jobContext = new JobContext(job.JobId, job.AttemptCount, executionToken);

        var scope = this._scopeFactory.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            using var loggingScope = this._logger.BeginScope(CreateJobLoggingScope(job, this._nodeIdProvider.NodeId));
            using var logCapture = JobLogCaptureContext.Begin(job.JobId, job.AttemptCount);

            var (target, bindingFlags) = job.InvocationTargetKind switch
            {
                JobInvocationTargetKind.Static => (
                  Target: null,
                  BindingFlags: System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic),
                JobInvocationTargetKind.Instance => (
                  Target: scope.ServiceProvider.GetRequiredService(serviceType),
                  BindingFlags: System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic),
                _ => throw new InvalidOperationException($"Job invocation target kind '{job.InvocationTargetKind}' is not supported."),
            };
            var method = serviceType.GetMethod(
                job.MethodName,
                bindingFlags,
                binder: null,
                types: methodParameterTypes,
                modifiers: null);

            _ = method ?? throw new InvalidOperationException($"Could not resolve job method '{job.MethodName}' on service type '{serviceType}'.");

            var deserializedArguments = await scope.ServiceProvider
                .GetRequiredService<IJobPayloadSerializer>()
                .DeserializeAsync(job.SerializedArguments, serializableParameterTypes, executionToken)
                .ConfigureAwait(false);
            var progressReporter = new DecimalJobProgressReporter(
              job.JobId,
              job.AttemptCount,
              this._jobEventSink,
              this._jobContextLogger);
            var invocationArguments = BuildInvocationArguments(
              scope.ServiceProvider,
              methodParameterTypes,
              parameterBindings,
              deserializedArguments,
              jobContext,
              progressReporter,
              executionToken);
            object? result;

            try
            {
                result = method.Invoke(target, invocationArguments);
            }
            catch (System.Reflection.TargetInvocationException exception) when (exception.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                throw;
            }

            switch (result)
            {
                case Task taskResult:
                    await taskResult.ConfigureAwait(false);
                    break;
                case ValueTask valueTaskResult:
                    await valueTaskResult.ConfigureAwait(false);
                    break;
                default:
                    throw new InvalidOperationException("Job method returned an unsupported result.");
            }
        }
    }

    private static Dictionary<string, object?> CreateJobLoggingScope(
        ClaimedJob job,
        string nodeId)
      => new(StringComparer.Ordinal)
      {
          ["ShedduellerJobId"] = job.JobId,
          ["ShedduellerAttemptNumber"] = job.AttemptCount,
          ["ShedduellerNodeId"] = nodeId,
      };

    private static object?[] BuildInvocationArguments(
        IServiceProvider serviceProvider,
        Type[] methodParameterTypes,
        IReadOnlyList<JobMethodParameterBinding> parameterBindings,
        IReadOnlyList<object?> deserializedArguments,
        IJobContext jobContext,
        DecimalJobProgressReporter progressReporter,
        CancellationToken executionToken)
    {
        if (methodParameterTypes.Length != parameterBindings.Count)
        {
            throw new InvalidOperationException("Job parameter binding count did not match target method parameters.");
        }

        var invocationArguments = new object?[methodParameterTypes.Length];
        var deserializedIndex = 0;

        for (var i = 0; i < methodParameterTypes.Length; i++)
        {
            switch (parameterBindings[i].Kind)
            {
                case JobMethodParameterBindingKind.CancellationToken:
                    invocationArguments[i] = executionToken;
                    continue;

                case JobMethodParameterBindingKind.JobContext:
                    invocationArguments[i] = jobContext;
                    continue;

                case JobMethodParameterBindingKind.ProgressReporter:
                    invocationArguments[i] = GetProgressReporter(methodParameterTypes[i], progressReporter);
                    continue;

                case JobMethodParameterBindingKind.Service:
                    invocationArguments[i] = ResolveBoundService(serviceProvider, methodParameterTypes[i], parameterBindings[i]);
                    continue;

                case JobMethodParameterBindingKind.Serialized:
                    invocationArguments[i] = deserializedArguments[deserializedIndex];
                    deserializedIndex++;
                    continue;

                default:
                    throw new InvalidOperationException($"Job parameter binding kind '{parameterBindings[i].Kind}' is not supported.");
            }
        }

        if (deserializedIndex != deserializedArguments.Count)
        {
            throw new InvalidOperationException("Job payload argument count did not match target method parameters.");
        }

        return invocationArguments;
    }

    private static DecimalJobProgressReporter GetProgressReporter(
        Type parameterType,
        DecimalJobProgressReporter progressReporter)
    {
        if (parameterType != typeof(IProgress<decimal>))
        {
            throw new InvalidOperationException($"Progress reporter parameter type '{parameterType}' is not supported.");
        }

        return progressReporter;
    }

    private static object ResolveBoundService(
        IServiceProvider serviceProvider,
        Type parameterType,
        JobMethodParameterBinding binding)
    {
        if (string.IsNullOrWhiteSpace(binding.ServiceType))
        {
            throw new InvalidOperationException("Service parameter binding did not include a service type.");
        }

        var serviceType = TypeNameFormatter.Resolve(binding.ServiceType);
        if (!parameterType.IsAssignableFrom(serviceType))
        {
            throw new InvalidOperationException($"Resolved service type '{serviceType}' is not assignable to method parameter type '{parameterType}'.");
        }

        return serviceProvider.GetRequiredService(serviceType);
    }

    private static IReadOnlyList<JobMethodParameterBinding> NormalizeParameterBindings(
        Type[] methodParameterTypes,
        IReadOnlyList<JobMethodParameterBinding>? parameterBindings)
      => JobMethodParameterBindingResolver.Normalize(methodParameterTypes, parameterBindings);

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Job progress telemetry is best-effort by v4 design.")]
    private sealed class DecimalJobProgressReporter(
        Guid jobId,
        int attemptNumber,
        IJobEventSink eventSink,
        ILogger logger) : IProgress<decimal>
    {
        public void Report(decimal value)
        {
            var request = new AppendJobEventRequest(
              jobId,
              JobEventKind.Progress,
              attemptNumber,
              ProgressPercent: (double)Math.Clamp(value, 0, 100));

            try
            {
                eventSink.AppendAsync(request).AsTask().GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                logger.JobEventAppendFailed(exception, jobId);
            }
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

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A failed heartbeat is retried; the job is cancelled only once its lease has presumably expired.")]
    private async Task RenewLeaseUntilStoppedAsync(
        IJobStore store,
        ClaimedJob job,
        DateTimeOffset claimedAtUtc,
        CancellationTokenSource executionTokenSource,
        RunningJobCancellationState cancellationState)
    {
        var heartbeatInterval = this._options.Value.HeartbeatInterval;
        var leaseDuration = this._options.Value.LeaseDuration;

        // Estimated from local time taken before each request; the store starts its lease no earlier (clock skew aside).
        var leaseExpiresAtUtc = claimedAtUtc.Add(leaseDuration);
        var delay = heartbeatInterval;

        try
        {
            while (!executionTokenSource.IsCancellationRequested)
            {
                await Task.Delay(delay, this._timeProvider, executionTokenSource.Token).ConfigureAwait(false);

                var now = this._timeProvider.GetUtcNow();
                try
                {
                    var renewed = await store
                        .RenewLeaseAsync(
                            new RenewLeaseRequest(job.JobId, this._nodeIdProvider.NodeId, job.LeaseToken, now, now.Add(leaseDuration)),
                            CancellationToken.None)
                        .ConfigureAwait(false);

                    if (!renewed)
                    {
                        this._logger.JobLeaseRenewalLost(job.JobId, job.AttemptCount, this._nodeIdProvider.NodeId);
                        await executionTokenSource.CancelAsync().ConfigureAwait(false);
                        return;
                    }

                    leaseExpiresAtUtc = now.Add(leaseDuration);
                    delay = heartbeatInterval;

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
                catch (Exception exception)
                {
                    // Keep the job running while the lease may still hold. Once it has expired another node can claim
                    // the job, so stop this execution rather than run it twice.
                    var failedAtUtc = this._timeProvider.GetUtcNow();
                    if (failedAtUtc >= leaseExpiresAtUtc)
                    {
                        this._logger.JobLeasePresumedLost(exception, job.JobId, job.AttemptCount, this._nodeIdProvider.NodeId, leaseExpiresAtUtc);
                        await executionTokenSource.CancelAsync().ConfigureAwait(false);
                        return;
                    }

                    this._logger.JobHeartbeatRetrying(exception, job.JobId, job.AttemptCount, this._nodeIdProvider.NodeId, leaseExpiresAtUtc);

                    // Retry on the next tick, but no later than the expiry so the job is stopped on time.
                    var untilExpiry = leaseExpiresAtUtc - failedAtUtc;
                    delay = untilExpiry < heartbeatInterval ? untilExpiry : heartbeatInterval;
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

    private sealed class RunningJobCancellationState
    {
        public DateTimeOffset? CancellationRequestedAtUtc { get; set; }
    }

    private enum JobOutcome
    {
        Completed,
        CancellationObserved,
        Released,
        Failed,
    }
}
