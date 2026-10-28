namespace Sheddueller.Worker.Tests;

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

using Sheddueller.Serialization;
using Sheddueller.Storage;
using Sheddueller.Tests.Logging;
using Sheddueller.Worker.Internal;

using Shouldly;

public sealed class WorkerReliabilityTests
{
    private const int WorkerLoopFailedEventId = 1103;
    private const int JobFailedEventId = 1121;
    private const int JobOutcomeRecordFailedEventId = 1122;
    private const int JobCancellationObservedEventId = 1130;
    private const int JobReleasedEventId = 1131;
    private const int JobCancellationRequestedObservedEventId = 1133;
    private const int JobHeartbeatRetryingEventId = 1135;
    private const int JobLeasePresumedLostEventId = 1136;

    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan IdlePollingInterval = TimeSpan.FromMilliseconds(10);

    [Fact]
    public async Task LeaseHeartbeat_StoreErrorThenRecovers_KeepsRenewingAndCompletesJob()
    {
        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var timeProvider = new FakeTimeProvider();
        var store = new ScriptedJobStore
        {
            RenewFailure = attempt => attempt == 1 ? new InvalidOperationException("store unavailable") : null,
        };
        var job = CreateClaimedJob(nameof(ReliabilityJob.WaitForReleaseAsync));
        store.AddClaimable(job);
        var gate = new JobGate();
        using var logs = new TestLoggerProvider();

        // A long lease, so the failed renewal can't run into its expiry however far the clock is advanced.
        await using var provider = CreateProvider(store, gate, logs, timeProvider, options => options.LeaseDuration = TimeSpan.FromHours(1));
        var worker = GetWorker(provider);

        await worker.StartAsync(cancellationTokenSource.Token);
        await gate.Started.Task.WaitAsync(cancellationTokenSource.Token);
        await AdvanceUntilAsync(timeProvider, () => store.RenewAttempts >= 2, cancellationTokenSource.Token);
        gate.Release.SetResult();
        await WaitUntilAsync(() => store.Completed.Count == 1, cancellationTokenSource.Token);
        await worker.StopAsync(cancellationTokenSource.Token);

        store.Completed.ShouldHaveSingleItem().JobId.ShouldBe(job.JobId);
        store.Released.ShouldBeEmpty();
        store.Failed.ShouldBeEmpty();
        logs.SingleByEventId(JobHeartbeatRetryingEventId).Exception.ShouldBeOfType<InvalidOperationException>();
        logs.HasEventId(JobLeasePresumedLostEventId).ShouldBeFalse();
    }

    [Fact]
    public async Task LeaseHeartbeat_FailsUntilLeaseExpires_CancelsJobOnceLeaseHasExpired()
    {
        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var timeProvider = new FakeTimeProvider();
        var store = new ScriptedJobStore
        {
            RenewFailure = _ => new InvalidOperationException("store unavailable"),
        };
        var job = CreateClaimedJob(nameof(ReliabilityJob.WaitForReleaseAsync));
        store.AddClaimable(job);
        var gate = new JobGate();
        using var logs = new TestLoggerProvider();
        await using var provider = CreateProvider(store, gate, logs, timeProvider);
        var worker = GetWorker(provider);

        // The fake clock only moves when the test advances it, so the job is claimed at its current time.
        var leaseExpiresAtUtc = timeProvider.GetUtcNow().Add(LeaseDuration);
        await worker.StartAsync(cancellationTokenSource.Token);
        await gate.Started.Task.WaitAsync(cancellationTokenSource.Token);
        await AdvanceUntilAsync(timeProvider, () => store.Released.Count == 1, cancellationTokenSource.Token);
        await worker.StopAsync(cancellationTokenSource.Token);

        var cancelledAtUtc = await gate.Cancelled.Task.WaitAsync(cancellationTokenSource.Token);
        cancelledAtUtc.ShouldBeGreaterThanOrEqualTo(leaseExpiresAtUtc);
        store.RenewAttempts.ShouldBeGreaterThanOrEqualTo(3);
        store.Released.ShouldHaveSingleItem().JobId.ShouldBe(job.JobId);
        store.Completed.ShouldBeEmpty();
        store.Failed.ShouldBeEmpty();
        logs.HasEventId(JobHeartbeatRetryingEventId).ShouldBeTrue();
        logs.SingleByEventId(JobLeasePresumedLostEventId).Properties["LeaseExpiresAtUtc"].ShouldBe(leaseExpiresAtUtc);
    }

    [Fact]
    public async Task JobOutcome_StoreFailsToRecordCompletion_IsNotRecordedAsJobFailure()
    {
        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var store = new ScriptedJobStore
        {
            CompleteFailure = new InvalidOperationException("store unavailable"),
        };
        var job = CreateClaimedJob(nameof(ReliabilityJob.CompleteAsync));
        store.AddClaimable(job);
        using var logs = new TestLoggerProvider();
        await using var provider = CreateProvider(store, new JobGate(), logs);
        var worker = GetWorker(provider);

        await worker.StartAsync(cancellationTokenSource.Token);
        await WaitUntilAsync(() => store.CompletionAttempts == 1, cancellationTokenSource.Token);
        await worker.StopAsync(cancellationTokenSource.Token);

        store.Failed.ShouldBeEmpty();
        logs.HasEventId(JobFailedEventId).ShouldBeFalse();
        var entry = logs.SingleByEventId(JobOutcomeRecordFailedEventId);
        entry.Exception.ShouldBeSameAs(store.CompleteFailure);
        entry.Properties["Outcome"].ShouldBe("Completed");
        entry.Properties["JobId"].ShouldBe(job.JobId);
    }

    [Fact]
    public async Task WorkerLoop_StoreErrorWhileClaiming_KeepsClaimingAndAwaitsRunningJobOnStop()
    {
        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var timeProvider = new FakeTimeProvider();
        var store = new ScriptedJobStore
        {
            ClaimFailure = attempt => attempt == 2 ? new InvalidOperationException("store unavailable") : null,
        };
        var job = CreateClaimedJob(nameof(ReliabilityJob.WaitForReleaseAsync));
        store.AddClaimable(job);
        var gate = new JobGate();
        using var logs = new TestLoggerProvider();
        await using var provider = CreateProvider(store, gate, logs, timeProvider);
        var worker = GetWorker(provider);

        await worker.StartAsync(cancellationTokenSource.Token);
        await gate.Started.Task.WaitAsync(cancellationTokenSource.Token);
        await AdvanceUntilAsync(timeProvider, () => store.ClaimAttempts >= 3, cancellationTokenSource.Token);

        worker.ExecuteTask.ShouldNotBeNull().IsCompleted.ShouldBeFalse();
        gate.Cancelled.Task.IsCompleted.ShouldBeFalse();
        await worker.StopAsync(cancellationTokenSource.Token);

        store.Released.ShouldHaveSingleItem().JobId.ShouldBe(job.JobId);
        var entry = logs.SingleByEventId(WorkerLoopFailedEventId);
        entry.Level.ShouldBe(LogLevel.Error);
        entry.Exception.ShouldBeOfType<InvalidOperationException>();
        entry.Properties["RetryDelayMs"].ShouldBe(1000L);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(5, 16)]
    [InlineData(6, 30)]
    [InlineData(1000, 30)]
    public void WorkerLoop_ConsecutiveFailures_BacksOffExponentiallyUpToThirtySeconds(int consecutiveFailures, int expectedSeconds)
      => ShedduellerWorker.CalculateLoopRetryDelay(consecutiveFailures).ShouldBe(TimeSpan.FromSeconds(expectedSeconds));

    [Fact]
    public async Task ClaimLoop_SynchronousJobRunning_ClaimsNextJob()
    {
        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var store = new ScriptedJobStore();
        var blockingJob = CreateClaimedJob(nameof(ReliabilityJob.BlockSynchronously));
        var nextJob = CreateClaimedJob(nameof(ReliabilityJob.CompleteAsync));
        store.AddClaimable(blockingJob, nextJob);
        var gate = new JobGate();
        using var logs = new TestLoggerProvider();
        await using var provider = CreateProvider(store, gate, logs);
        var worker = GetWorker(provider);

        await worker.StartAsync(cancellationTokenSource.Token);
        try
        {
            await gate.Started.Task.WaitAsync(cancellationTokenSource.Token);
            await WaitUntilAsync(() => store.Completed.Count == 1, cancellationTokenSource.Token);

            store.Completed.ShouldHaveSingleItem().JobId.ShouldBe(nextJob.JobId);
        }
        finally
        {
            gate.Release.TrySetResult();
        }

        await worker.StopAsync(cancellationTokenSource.Token);
    }

    [Fact]
    public async Task Shutdown_JobInFlight_ReleasesJob()
    {
        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var store = new ScriptedJobStore();
        var job = CreateClaimedJob(nameof(ReliabilityJob.WaitForReleaseAsync));
        store.AddClaimable(job);
        var gate = new JobGate();
        using var logs = new TestLoggerProvider();
        await using var provider = CreateProvider(store, gate, logs);
        var worker = GetWorker(provider);

        await worker.StartAsync(cancellationTokenSource.Token);
        await gate.Started.Task.WaitAsync(cancellationTokenSource.Token);
        await worker.StopAsync(cancellationTokenSource.Token);

        store.Released.ShouldHaveSingleItem().JobId.ShouldBe(job.JobId);
        store.Completed.ShouldBeEmpty();
        store.Failed.ShouldBeEmpty();
        store.CancellationObserved.ShouldBeEmpty();
        logs.SingleByEventId(JobReleasedEventId).Properties["JobId"].ShouldBe(job.JobId);
    }

    [Fact]
    public async Task JobCancellation_RequestedWhileRunning_MarksCancellationObserved()
    {
        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var timeProvider = new FakeTimeProvider();
        var store = new ScriptedJobStore
        {
            CancellationRequestedAtUtc = timeProvider.GetUtcNow(),
        };
        var job = CreateClaimedJob(nameof(ReliabilityJob.WaitForReleaseAsync));
        store.AddClaimable(job);
        var gate = new JobGate();
        using var logs = new TestLoggerProvider();
        await using var provider = CreateProvider(store, gate, logs, timeProvider);
        var worker = GetWorker(provider);

        await worker.StartAsync(cancellationTokenSource.Token);
        await gate.Started.Task.WaitAsync(cancellationTokenSource.Token);
        await AdvanceUntilAsync(timeProvider, () => store.CancellationObserved.Count == 1, cancellationTokenSource.Token);
        await worker.StopAsync(cancellationTokenSource.Token);

        store.CancellationObserved.ShouldHaveSingleItem().JobId.ShouldBe(job.JobId);
        store.Released.ShouldBeEmpty();
        store.Failed.ShouldBeEmpty();
        logs.HasEventId(JobCancellationRequestedObservedEventId).ShouldBeTrue();
        logs.HasEventId(JobCancellationObservedEventId).ShouldBeTrue();
    }

    [Fact]
    public async Task WakeSignal_RunningJobFinishesWhileWaiting_LaterEnqueueIsClaimedPromptly()
    {
        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var store = new ScriptedJobStore();
        var runningJob = CreateClaimedJob(nameof(ReliabilityJob.WaitForReleaseAsync));
        var enqueuedJob = CreateClaimedJob(nameof(ReliabilityJob.CompleteAsync));
        store.AddClaimable(runningJob);
        store.AddEnqueueable(enqueuedJob);
        var gate = new JobGate();
        using var logs = new TestLoggerProvider();

        // Polling would only find the enqueued job after a minute, so within the timeout only the wake-up can.
        await using var provider = CreateProvider(store, gate, logs, configureOptions: options => options.IdlePollingInterval = TimeSpan.FromMinutes(1));
        var worker = GetWorker(provider);
        var enqueuer = provider.GetRequiredService<IJobEnqueuer>();

        // Claims 1 and 2 take the running job and find nothing more; claim 3 finds nothing, so the worker waits for a
        // wake-up or for the running job to finish. When it finishes, claim 4 finds nothing and the worker waits again:
        // only then is the enqueue's wake-up the one that has to get the job claimed.
        await worker.StartAsync(cancellationTokenSource.Token);
        await gate.Started.Task.WaitAsync(cancellationTokenSource.Token);
        await WaitUntilAsync(() => store.ClaimAttempts == 3, cancellationTokenSource.Token);
        gate.Release.SetResult();
        await WaitUntilAsync(() => store.Completed.Count == 1 && store.ClaimAttempts == 4, cancellationTokenSource.Token);
        await enqueuer.EnqueueAsync<ReliabilityJob>(
            (reliabilityJob, cancellationToken) => reliabilityJob.CompleteAsync(cancellationToken),
            cancellationToken: cancellationTokenSource.Token);
        await WaitUntilAsync(() => store.Completed.Count == 2, cancellationTokenSource.Token);
        await worker.StopAsync(cancellationTokenSource.Token);

        store.Completed.Select(request => request.JobId).ShouldBe([runningJob.JobId, enqueuedJob.JobId]);
    }

    [Fact]
    public async Task PeriodicStoreWork_JobsCompletingBackToBack_RunsOncePerPollingInterval()
    {
        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var timeProvider = new FakeTimeProvider();
        var store = new ScriptedJobStore();
        var jobs = Enumerable.Range(0, 6).Select(_ => CreateClaimedJob(nameof(ReliabilityJob.CompleteAsync))).ToArray();
        store.AddClaimable(jobs);
        using var logs = new TestLoggerProvider();
        await using var provider = CreateProvider(store, new JobGate(), logs, timeProvider);
        var worker = GetWorker(provider);

        await worker.StartAsync(cancellationTokenSource.Token);
        await WaitUntilAsync(() => store.Completed.Count == jobs.Length, cancellationTokenSource.Token);
        var runsWhileClockStood = store.LeaseRecoveryRuns;
        timeProvider.Advance(IdlePollingInterval);
        await WaitUntilAsync(() => store.LeaseRecoveryRuns > runsWhileClockStood, cancellationTokenSource.Token);
        await worker.StopAsync(cancellationTokenSource.Token);

        runsWhileClockStood.ShouldBe(1);
    }

    private static ServiceProvider CreateProvider(
        ScriptedJobStore store,
        JobGate gate,
        TestLoggerProvider logs,
        TimeProvider? timeProvider = null,
        Action<ShedduellerOptions>? configureOptions = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder
          .SetMinimumLevel(LogLevel.Trace)
          .AddProvider(logs));
        if (timeProvider is not null)
        {
            services.AddSingleton(timeProvider);
        }

        services.AddSingleton(gate);
        services.AddSingleton<IJobStore>(store);
        services.AddTransient<ReliabilityJob>();
        services.AddShedduellerWorker(builder => builder.ConfigureOptions(options =>
        {
            options.NodeId = "worker-reliability";
            options.IdlePollingInterval = IdlePollingInterval;
            options.HeartbeatInterval = HeartbeatInterval;
            options.LeaseDuration = LeaseDuration;
            options.MaxConcurrentExecutionsPerNode = 4;
            configureOptions?.Invoke(options);
        }));

        return services.BuildServiceProvider();
    }

    private static ShedduellerWorker GetWorker(ServiceProvider provider)
      => provider.GetServices<IHostedService>().OfType<ShedduellerWorker>().Single();

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(5), cancellationToken);
        }
    }

    private static async Task AdvanceUntilAsync(FakeTimeProvider timeProvider, Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition())
        {
            timeProvider.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(TimeSpan.FromMilliseconds(5), cancellationToken);
        }
    }

    private static ClaimedJob CreateClaimedJob(string methodName)
      => new(
          Guid.NewGuid(),
          EnqueueSequence: 1,
          Priority: 0,
          ServiceType: typeof(ReliabilityJob).AssemblyQualifiedName!,
          MethodName: methodName,
          MethodParameterTypes: [typeof(CancellationToken).AssemblyQualifiedName!],
          SerializedArguments: new SerializedJobPayload(SystemTextJsonJobPayloadSerializer.JsonContentType, "[]"u8.ToArray()),
          ConcurrencyGroupKeys: [],
          AttemptCount: 1,
          MaxAttempts: 1,
          LeaseToken: Guid.NewGuid(),
          LeaseExpiresAtUtc: DateTimeOffset.UtcNow.Add(LeaseDuration),
          RetryBackoffKind: null,
          RetryBaseDelay: null,
          RetryMaxDelay: null,
          SourceScheduleKey: null,
          ScheduledFireAtUtc: null,
          MethodParameterBindings: [new JobMethodParameterBinding(JobMethodParameterBindingKind.CancellationToken)]);

    private sealed class JobGate
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<DateTimeOffset> Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class ReliabilityJob(JobGate gate, TimeProvider timeProvider)
    {
        public async Task WaitForReleaseAsync(CancellationToken cancellationToken)
        {
            gate.Started.TrySetResult();
            try
            {
                await gate.Release.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                gate.Cancelled.TrySetResult(timeProvider.GetUtcNow());
                throw;
            }
        }

        [SuppressMessage("Performance", "CA1849:Call async methods when in an async method", Justification = "Stands in for a job that blocks its thread before returning.")]
        public Task BlockSynchronously(CancellationToken cancellationToken)
        {
            gate.Started.TrySetResult();
            gate.Release.Task.Wait(cancellationToken);
            return Task.CompletedTask;
        }

        public Task CompleteAsync(CancellationToken cancellationToken)
          => Task.CompletedTask;
    }

    private sealed class ScriptedJobStore : IJobStore
    {
        private readonly ConcurrentQueue<ClaimedJob> _claimable = new();
        private readonly ConcurrentQueue<ClaimedJob> _enqueueable = new();
        private readonly ConcurrentQueue<CompleteJobRequest> _completed = new();
        private readonly ConcurrentQueue<FailJobRequest> _failed = new();
        private readonly ConcurrentQueue<ReleaseJobRequest> _released = new();
        private readonly ConcurrentQueue<ObserveJobCancellationRequest> _cancellationObserved = new();
        private int _claimAttempts;
        private int _renewAttempts;
        private int _completionAttempts;
        private int _leaseRecoveryRuns;

        public Func<int, Exception?> ClaimFailure { get; init; } = static _ => null;

        public Func<int, Exception?> RenewFailure { get; init; } = static _ => null;

        public Exception? CompleteFailure { get; init; }

        public DateTimeOffset? CancellationRequestedAtUtc { get; init; }

        public int ClaimAttempts
          => Volatile.Read(ref this._claimAttempts);

        public int RenewAttempts
          => Volatile.Read(ref this._renewAttempts);

        public int CompletionAttempts
          => Volatile.Read(ref this._completionAttempts);

        public int LeaseRecoveryRuns
          => Volatile.Read(ref this._leaseRecoveryRuns);

        public IReadOnlyList<CompleteJobRequest> Completed
          => [.. this._completed];

        public IReadOnlyList<FailJobRequest> Failed
          => [.. this._failed];

        public IReadOnlyList<ReleaseJobRequest> Released
          => [.. this._released];

        public IReadOnlyList<ObserveJobCancellationRequest> CancellationObserved
          => [.. this._cancellationObserved];

        public void AddClaimable(params ClaimedJob[] jobs)
        {
            foreach (var job in jobs)
            {
                this._claimable.Enqueue(job);
            }
        }

        public void AddEnqueueable(ClaimedJob job)
          => this._enqueueable.Enqueue(job);

        public ValueTask<EnqueueJobResult> EnqueueAsync(
            EnqueueJobRequest request,
            CancellationToken cancellationToken = default)
        {
            if (!this._enqueueable.TryDequeue(out var job))
            {
                throw new InvalidOperationException("No job was staged for enqueueing.");
            }

            this._claimable.Enqueue(job);
            return ValueTask.FromResult(new EnqueueJobResult(job.JobId, job.EnqueueSequence));
        }

        public ValueTask<IReadOnlyList<EnqueueJobResult>> EnqueueManyAsync(
            IReadOnlyList<EnqueueJobRequest> requests,
            CancellationToken cancellationToken = default)
          => throw new NotSupportedException();

        public ValueTask<ClaimJobResult> TryClaimNextAsync(
            ClaimJobRequest request,
            CancellationToken cancellationToken = default)
        {
            if (this.ClaimFailure(Interlocked.Increment(ref this._claimAttempts)) is { } failure)
            {
                throw failure;
            }

            return ValueTask.FromResult<ClaimJobResult>(
              this._claimable.TryDequeue(out var job)
                ? new ClaimJobResult.Claimed(job)
                : new ClaimJobResult.NoJobAvailable());
        }

        public ValueTask<bool> MarkCompletedAsync(
            CompleteJobRequest request,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref this._completionAttempts);
            if (this.CompleteFailure is { } failure)
            {
                throw failure;
            }

            this._completed.Enqueue(request);
            return ValueTask.FromResult(true);
        }

        public ValueTask<bool> MarkFailedAsync(
            FailJobRequest request,
            CancellationToken cancellationToken = default)
        {
            this._failed.Enqueue(request);
            return ValueTask.FromResult(true);
        }

        public ValueTask<bool> RenewLeaseAsync(
            RenewLeaseRequest request,
            CancellationToken cancellationToken = default)
        {
            if (this.RenewFailure(Interlocked.Increment(ref this._renewAttempts)) is { } failure)
            {
                throw failure;
            }

            return ValueTask.FromResult(true);
        }

        public ValueTask<bool> ReleaseJobAsync(
            ReleaseJobRequest request,
            CancellationToken cancellationToken = default)
        {
            this._released.Enqueue(request);
            return ValueTask.FromResult(true);
        }

        public ValueTask<int> RecoverExpiredLeasesAsync(
            RecoverExpiredLeasesRequest request,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref this._leaseRecoveryRuns);
            return ValueTask.FromResult(0);
        }

        public ValueTask<JobCancellationResult> CancelAsync(
            CancelJobRequest request,
            CancellationToken cancellationToken = default)
          => ValueTask.FromResult(JobCancellationResult.NotFound);

        public ValueTask<int> CancelQueuedJobsAsync(
            CancelQueuedJobsRequest request,
            CancellationToken cancellationToken = default)
          => ValueTask.FromResult(0);

        public ValueTask<DateTimeOffset?> GetCancellationRequestedAtAsync(
            JobCancellationStatusRequest request,
            CancellationToken cancellationToken = default)
          => ValueTask.FromResult(this.CancellationRequestedAtUtc);

        public ValueTask<bool> MarkCancellationObservedAsync(
            ObserveJobCancellationRequest request,
            CancellationToken cancellationToken = default)
        {
            this._cancellationObserved.Enqueue(request);
            return ValueTask.FromResult(true);
        }

        public ValueTask RecordWorkerNodeHeartbeatAsync(
            WorkerNodeHeartbeatRequest request,
            CancellationToken cancellationToken = default)
          => ValueTask.CompletedTask;

        public ValueTask SetConcurrencyLimitAsync(
            SetConcurrencyLimitRequest request,
            CancellationToken cancellationToken = default)
          => throw new NotSupportedException();

        public ValueTask SetConcurrencyDefaultLimitAsync(
            SetConcurrencyDefaultLimitRequest request,
            CancellationToken cancellationToken = default)
          => throw new NotSupportedException();

        public ValueTask ClearConcurrencyLimitOverrideAsync(
            ClearConcurrencyLimitOverrideRequest request,
            CancellationToken cancellationToken = default)
          => throw new NotSupportedException();

        public ValueTask<int?> GetConfiguredConcurrencyLimitAsync(
            string groupKey,
            CancellationToken cancellationToken = default)
          => throw new NotSupportedException();

        public ValueTask<RecurringScheduleUpsertResult> CreateOrUpdateRecurringScheduleAsync(
            UpsertRecurringScheduleRequest request,
            CancellationToken cancellationToken = default)
          => throw new NotSupportedException();

        public ValueTask<RecurringScheduleTriggerResult> TriggerRecurringScheduleAsync(
            TriggerRecurringScheduleRequest request,
            CancellationToken cancellationToken = default)
          => throw new NotSupportedException();

        public ValueTask<bool> DeleteRecurringScheduleAsync(
            string scheduleKey,
            CancellationToken cancellationToken = default)
          => throw new NotSupportedException();

        public ValueTask<bool> PauseRecurringScheduleAsync(
            string scheduleKey,
            DateTimeOffset pausedAtUtc,
            CancellationToken cancellationToken = default)
          => throw new NotSupportedException();

        public ValueTask<bool> ResumeRecurringScheduleAsync(
            string scheduleKey,
            DateTimeOffset resumedAtUtc,
            CancellationToken cancellationToken = default)
          => throw new NotSupportedException();

        public ValueTask<RecurringScheduleInfo?> GetRecurringScheduleAsync(
            string scheduleKey,
            CancellationToken cancellationToken = default)
          => throw new NotSupportedException();

        public ValueTask<IReadOnlyList<RecurringScheduleInfo>> ListRecurringSchedulesAsync(
            CancellationToken cancellationToken = default)
          => throw new NotSupportedException();

        public ValueTask<int> MaterializeDueRecurringSchedulesAsync(
            MaterializeDueRecurringSchedulesRequest request,
            CancellationToken cancellationToken = default)
          => ValueTask.FromResult(0);
    }
}
