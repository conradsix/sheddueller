namespace Sheddueller.Worker.Tests;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Sheddueller.Storage;
using Sheddueller.Tests.Logging;
using Sheddueller.Worker.Internal;

using Shouldly;

public sealed class WorkerLoggingTests
{
    [Fact]
    public async Task JobExecution_Exception_LogsFailureAndMarksJobFailed()
    {
        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var job = WorkerTestJobs.CreateClaimedJob<FailingJob>(nameof(FailingJob.FailAsync));
        var store = new SingleClaimJobStore(job);
        using var logs = new TestLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(builder => builder
          .SetMinimumLevel(LogLevel.Trace)
          .AddProvider(logs));
        services.AddSingleton<IJobStore>(store);
        services.AddTransient<FailingJob>();
        services.AddShedduellerWorker(builder => builder.ConfigureOptions(options =>
        {
            options.NodeId = "worker-a";
            options.IdlePollingInterval = TimeSpan.FromMilliseconds(10);
            options.HeartbeatInterval = TimeSpan.FromSeconds(5);
            options.LeaseDuration = TimeSpan.FromSeconds(30);
        }));
        await using var provider = services.BuildServiceProvider();
        var worker = provider.GetServices<IHostedService>().OfType<ShedduellerWorker>().Single();

        await worker.StartAsync(cancellationTokenSource.Token);
        var failed = await store.Failed.Task.WaitAsync(cancellationTokenSource.Token);
        await worker.StopAsync(cancellationTokenSource.Token);

        failed.JobId.ShouldBe(job.JobId);
        failed.Failure.ExceptionType.ShouldBe(typeof(InvalidOperationException).FullName);

        var entry = logs.SingleByEventId(1121);
        entry.Level.ShouldBe(LogLevel.Error);
        entry.Exception.ShouldBeOfType<InvalidOperationException>();
        entry.Properties["JobId"].ShouldBe(job.JobId);
        entry.Properties["AttemptNumber"].ShouldBe(job.AttemptCount);
        entry.Properties["NodeId"].ShouldBe("worker-a");
        entry.MessageTemplate.ShouldBe("Job {JobId} attempt {AttemptNumber} failed on node {NodeId}.");
    }

    private sealed class FailingJob
    {
        public Task FailAsync(CancellationToken cancellationToken)
          => throw new InvalidOperationException("job failed");
    }
}
