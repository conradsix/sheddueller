namespace Sheddueller.Worker.Tests;

using Microsoft.Extensions.DependencyInjection;

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
        var hostedServices = await HostedServices.StartAsync(provider, cancellationTokenSource.Token);

        await store.Completed.Task.WaitAsync(cancellationTokenSource.Token);
        await HostedServices.StopAsync(hostedServices, cancellationTokenSource.Token);

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
        var hostedServices = await HostedServices.StartAsync(provider, cancellationTokenSource.Token);

        await store.Completed.Task.WaitAsync(cancellationTokenSource.Token);
        await HostedServices.StopAsync(hostedServices, cancellationTokenSource.Token);

        eventSink.Requests.Select(request => request.ProgressPercent).ShouldBe([100, 0]);
    }

    [Fact]
    public async Task JobExecution_ProgressSinkBlocks_StillCompletesJob()
    {
        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var job = CreateClaimedJob(nameof(ProgressJob.ReportProgress));
        var store = new SingleClaimJobStore(job);
        var eventSink = new BlockingJobEventSink();
        await using var provider = CreateProvider(store, eventSink);
        var hostedServices = await HostedServices.StartAsync(provider, cancellationTokenSource.Token);

        var completed = await store.Completed.Task.WaitAsync(cancellationTokenSource.Token);
        eventSink.AllowAppend.SetResult();
        await HostedServices.StopAsync(hostedServices, cancellationTokenSource.Token);

        completed.JobId.ShouldBe(job.JobId);
    }

    private static ServiceProvider CreateProvider(
        SingleClaimJobStore store,
        IJobEventSink eventSink)
    {
        var services = new ServiceCollection();
        services.AddSingleton(eventSink);
        services.AddSingleton<IJobStore>(store);
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
      => WorkerTestJobs.CreateClaimedJob<ProgressJob>(methodName, typeof(IProgress<decimal>), typeof(CancellationToken));

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
}
