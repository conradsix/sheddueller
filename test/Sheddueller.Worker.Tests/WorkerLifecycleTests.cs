namespace Sheddueller.Worker.Tests;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Sheddueller.Serialization;
using Sheddueller.Storage;
using Sheddueller.Worker.Internal;

using Shouldly;

public sealed class WorkerLifecycleTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task JobExecution_CancellationOrOwnershipLoss_PreservesTerminalDispositionAndDisposesScope(bool requested, bool leaseLost)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var state = new ExecutionState();
        var job = CreateJob();
        var store = new CancellationStore(job, state, requested, leaseLost);
        var services = new ServiceCollection();
        services.AddSingleton(state);
        services.AddSingleton<IJobStore>(store);
        services.AddScoped<WaitingJob>();
        services.AddShedduellerWorker(builder => builder.ConfigureOptions(options =>
        {
            options.NodeId = "lifecycle-worker";
            options.HeartbeatInterval = TimeSpan.FromMilliseconds(10);
            options.IdlePollingInterval = TimeSpan.FromMilliseconds(10);
            options.LeaseDuration = TimeSpan.FromSeconds(30);
        }));
        await using var provider = services.BuildServiceProvider();
        var worker = provider.GetServices<IHostedService>().OfType<ShedduellerWorker>().Single();

        await worker.StartAsync(timeout.Token);
        await state.Started.Task.WaitAsync(timeout.Token);
        if (requested)
        {
            var observed = await store.CancellationObserved.Task.WaitAsync(timeout.Token);
            observed.JobId.ShouldBe(job.JobId);
            observed.LeaseToken.ShouldBe(job.LeaseToken);
        }
        else if (leaseLost)
        {
            var released = await store.Released.Task.WaitAsync(timeout.Token);
            released.JobId.ShouldBe(job.JobId);
            released.LeaseToken.ShouldBe(job.LeaseToken);
        }

        await worker.StopAsync(timeout.Token);

        state.Disposed.ShouldBeTrue();
        store.Completed.Task.IsCompleted.ShouldBeFalse();
        store.Failed.Task.IsCompleted.ShouldBeFalse();
        store.CancellationObserved.Task.IsCompleted.ShouldBe(requested);
        store.Released.Task.IsCompleted.ShouldBe(!requested);
    }

    private static ClaimedJob CreateJob()
      => new(
          Guid.NewGuid(), 1, 0, typeof(WaitingJob).AssemblyQualifiedName!, nameof(WaitingJob.WaitAsync),
          [typeof(CancellationToken).AssemblyQualifiedName!],
          new SerializedJobPayload(SystemTextJsonJobPayloadSerializer.JsonContentType, "[]"u8.ToArray()),
          [], 1, 1, Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(30), null, null, null, null, null,
          MethodParameterBindings: [new JobMethodParameterBinding(JobMethodParameterBindingKind.CancellationToken)]);

    private sealed class ExecutionState
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Disposed { get; set; }
    }

    private sealed class WaitingJob(ExecutionState state) : IAsyncDisposable
    {
        public async Task WaitAsync(CancellationToken cancellationToken)
        {
            state.Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            state.Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CancellationStore(ClaimedJob job, ExecutionState state, bool requested, bool leaseLost) : WorkerTestJobStore(job)
    {
        public override ValueTask<bool> RenewLeaseAsync(RenewLeaseRequest request, CancellationToken cancellationToken = default)
          => ValueTask.FromResult(!leaseLost || !state.Started.Task.IsCompleted);

        public override ValueTask<DateTimeOffset?> GetCancellationRequestedAtAsync(JobCancellationStatusRequest request, CancellationToken cancellationToken = default)
          => ValueTask.FromResult<DateTimeOffset?>(requested && state.Started.Task.IsCompleted ? DateTimeOffset.UtcNow : null);
    }
}
