namespace Sheddueller.Worker.Tests;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Sheddueller.Serialization;
using Sheddueller.Storage;
using Sheddueller.Worker.Internal;

using Shouldly;

public sealed class WorkerProgressTests
{
    [Fact]
    public async Task JobExecution_ProgressReporter_RecordsProgressEvent()
    {
        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var job = CreateClaimedJob(nameof(ProgressJob.ReportProgress));
        var store = new WorkerTestJobStore(job);
        var eventSink = new RecordingJobEventSink();
        await using var provider = CreateProvider(store, eventSink);
        var worker = provider.GetServices<IHostedService>().OfType<ShedduellerWorker>().Single();

        await worker.StartAsync(cancellationTokenSource.Token);
        await store.Completed.Task.WaitAsync(cancellationTokenSource.Token);
        await worker.StopAsync(cancellationTokenSource.Token);

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
        var store = new WorkerTestJobStore(job);
        var eventSink = new RecordingJobEventSink();
        await using var provider = CreateProvider(store, eventSink);
        var worker = provider.GetServices<IHostedService>().OfType<ShedduellerWorker>().Single();

        await worker.StartAsync(cancellationTokenSource.Token);
        await store.Completed.Task.WaitAsync(cancellationTokenSource.Token);
        await worker.StopAsync(cancellationTokenSource.Token);

        eventSink.Requests.Select(request => request.ProgressPercent).ShouldBe([100, 0]);
    }

    private static ServiceProvider CreateProvider(
        WorkerTestJobStore store,
        RecordingJobEventSink eventSink)
    {
        var services = new ServiceCollection();
        services.AddSingleton(eventSink);
        services.AddSingleton<IJobEventSink>(serviceProvider => serviceProvider.GetRequiredService<RecordingJobEventSink>());
        services.AddSingleton(store);
        services.AddSingleton<IJobStore>(serviceProvider => serviceProvider.GetRequiredService<WorkerTestJobStore>());
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


}
