namespace Sheddueller.Worker.Tests;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Sheddueller.Serialization;
using Sheddueller.Storage;

using Shouldly;

public sealed class WorkerProgressTests
{
    [Fact]
    public async Task JobExecution_ProgressReporter_RecordsProgressEvent()
    {
        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var job = CreateClaimedJob(nameof(ProgressJob.ReportProgress));
        var store = new SingleClaimJobStore(job);
        var eventSink = new RecordingJobEventSink();
        await using var provider = CreateProvider(store, eventSink);
        var hostedServices = await StartHostedServicesAsync(provider, cancellationTokenSource.Token);

        await store.Completed.Task.WaitAsync(cancellationTokenSource.Token);
        await StopHostedServicesAsync(hostedServices, cancellationTokenSource.Token);

        var request = eventSink.Requests.ShouldHaveSingleItem();
        request.JobId.ShouldBe(job.JobId);
        request.Kind.ShouldBe(JobEventKind.Progress);
        request.AttemptNumber.ShouldBe(job.AttemptCount);
        request.ProgressPercent.ShouldBe(42.5);
        request.Message.ShouldBeNull();
    }

    [Fact]
    public async Task JobExecution_ProgressReporterOutOfRange_ClampsProgressEvent()
    {
        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var job = CreateClaimedJob(nameof(ProgressJob.ReportOutOfRangeAsync));
        var store = new SingleClaimJobStore(job);
        var eventSink = new RecordingJobEventSink();
        await using var provider = CreateProvider(store, eventSink);
        var hostedServices = await StartHostedServicesAsync(provider, cancellationTokenSource.Token);

        await store.Completed.Task.WaitAsync(cancellationTokenSource.Token);
        await StopHostedServicesAsync(hostedServices, cancellationTokenSource.Token);

        eventSink.Requests.Select(request => request.ProgressPercent).ShouldBe([100, 0]);
    }

    // Progress is appended by the job event dispatcher, which flushes what is queued when it stops after the worker.
    private static async Task<IReadOnlyList<IHostedService>> StartHostedServicesAsync(
        ServiceProvider provider,
        CancellationToken cancellationToken)
    {
        var hostedServices = provider.GetServices<IHostedService>().ToArray();
        foreach (var hostedService in hostedServices)
        {
            await hostedService.StartAsync(cancellationToken);
        }

        return hostedServices;
    }

    private static async Task StopHostedServicesAsync(
        IEnumerable<IHostedService> hostedServices,
        CancellationToken cancellationToken)
    {
        foreach (var hostedService in hostedServices.Reverse())
        {
            await hostedService.StopAsync(cancellationToken);
        }
    }

    private static ServiceProvider CreateProvider(
        SingleClaimJobStore store,
        RecordingJobEventSink eventSink)
    {
        var services = new ServiceCollection();
        services.AddSingleton(eventSink);
        services.AddSingleton<IJobEventSink>(serviceProvider => serviceProvider.GetRequiredService<RecordingJobEventSink>());
        services.AddSingleton(store);
        services.AddSingleton<IJobStore>(serviceProvider => serviceProvider.GetRequiredService<SingleClaimJobStore>());
        services.AddTransient<ProgressJob>();
        services.AddShedduellerWorker(builder => builder.ConfigureOptions(options =>
        {
            options.NodeId = "worker-progress";
            options.IdlePollingInterval = TimeSpan.FromMilliseconds(10);
            options.HeartbeatInterval = TimeSpan.FromSeconds(5);
            options.LeaseDuration = TimeSpan.FromSeconds(30);
        }));
        return services.BuildServiceProvider();
    }

    private static ClaimedJob CreateClaimedJob(string methodName)
      => new(
          Guid.NewGuid(),
          EnqueueSequence: 1,
          Priority: 0,
          ServiceType: typeof(ProgressJob).AssemblyQualifiedName!,
          MethodName: methodName,
          MethodParameterTypes:
          [
              typeof(IProgress<decimal>).AssemblyQualifiedName!,
              typeof(CancellationToken).AssemblyQualifiedName!,
          ],
          SerializedArguments: new SerializedJobPayload(SystemTextJsonJobPayloadSerializer.JsonContentType, "[]"u8.ToArray()),
          ConcurrencyGroupKeys: [],
          AttemptCount: 1,
          MaxAttempts: 1,
          LeaseToken: Guid.NewGuid(),
          LeaseExpiresAtUtc: DateTimeOffset.UtcNow.AddSeconds(30),
          RetryBackoffKind: null,
          RetryBaseDelay: null,
          RetryMaxDelay: null,
          SourceScheduleKey: null,
          ScheduledFireAtUtc: null,
          MethodParameterBindings:
          [
              new JobMethodParameterBinding(JobMethodParameterBindingKind.ProgressReporter),
              new JobMethodParameterBinding(JobMethodParameterBindingKind.CancellationToken),
          ]);

    private sealed class ProgressJob
    {
        public Task ReportProgress(IProgress<decimal> progress, CancellationToken cancellationToken)
        {
            progress.Report(42.5m);
            return Task.CompletedTask;
        }

        public Task ReportOutOfRangeAsync(IProgress<decimal> progress, CancellationToken cancellationToken)
        {
            progress.Report(100.1m);
            progress.Report(-0.1m);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingJobEventSink : IJobEventSink
    {
        private readonly Lock _syncRoot = new();
        private readonly List<AppendJobEventRequest> _requests = [];

        public IReadOnlyList<AppendJobEventRequest> Requests
        {
            get
            {
                lock (this._syncRoot)
                {
                    return Array.AsReadOnly([.. this._requests]);
                }
            }
        }

        public ValueTask<JobEvent> AppendAsync(
            AppendJobEventRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            lock (this._syncRoot)
            {
                this._requests.Add(request);
                return ValueTask.FromResult(new JobEvent(
                  Guid.NewGuid(),
                  request.JobId,
                  this._requests.Count,
                  request.Kind,
                  DateTimeOffset.UtcNow,
                  request.AttemptNumber,
                  request.LogLevel,
                  request.Message,
                  request.ProgressPercent,
                  request.Fields));
            }
        }
    }

    private sealed class SingleClaimJobStore(ClaimedJob job) : IJobStore
    {
        private int _claimed;

        public TaskCompletionSource<CompleteJobRequest> Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<FailJobRequest> Failed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<EnqueueJobResult> EnqueueAsync(
            EnqueueJobRequest request,
            CancellationToken cancellationToken = default)
          => throw new NotSupportedException();

        public ValueTask<IReadOnlyList<EnqueueJobResult>> EnqueueManyAsync(
            IReadOnlyList<EnqueueJobRequest> requests,
            CancellationToken cancellationToken = default)
          => throw new NotSupportedException();

        public ValueTask<ClaimJobResult> TryClaimNextAsync(
            ClaimJobRequest request,
            CancellationToken cancellationToken = default)
          => ValueTask.FromResult<ClaimJobResult>(
              Interlocked.Exchange(ref this._claimed, 1) == 0
                ? new ClaimJobResult.Claimed(job)
                : new ClaimJobResult.NoJobAvailable());

        public ValueTask<bool> MarkCompletedAsync(
            CompleteJobRequest request,
            CancellationToken cancellationToken = default)
        {
            this.Completed.TrySetResult(request);
            return ValueTask.FromResult(true);
        }

        public ValueTask<bool> MarkFailedAsync(
            FailJobRequest request,
            CancellationToken cancellationToken = default)
        {
            this.Failed.TrySetResult(request);
            return ValueTask.FromResult(true);
        }

        public ValueTask<bool> RenewLeaseAsync(
            RenewLeaseRequest request,
            CancellationToken cancellationToken = default)
          => ValueTask.FromResult(true);

        public ValueTask<bool> ReleaseJobAsync(
            ReleaseJobRequest request,
            CancellationToken cancellationToken = default)
          => ValueTask.FromResult(true);

        public ValueTask<int> RecoverExpiredLeasesAsync(
            RecoverExpiredLeasesRequest request,
            CancellationToken cancellationToken = default)
          => ValueTask.FromResult(0);

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
          => ValueTask.FromResult<DateTimeOffset?>(null);

        public ValueTask<bool> MarkCancellationObservedAsync(
            ObserveJobCancellationRequest request,
            CancellationToken cancellationToken = default)
          => ValueTask.FromResult(true);

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
