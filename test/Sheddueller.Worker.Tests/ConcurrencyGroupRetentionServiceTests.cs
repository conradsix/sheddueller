namespace Sheddueller.Worker.Tests;

using System.Collections.Concurrent;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;

using Sheddueller.Storage;

using Shouldly;

public sealed class ConcurrencyGroupRetentionServiceTests
{
    [Fact]
    public async Task Cleanup_JobRetentionDisabled_StillDrainsGroupBatches()
    {
        var store = new RetentionStore();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConcurrencyGroupRetentionStore>(store);
        services.AddShedduellerWorker();
        services.Configure<ShedduellerOptions>(options =>
        {
            options.JobRetention.Enabled = false;
            options.ConcurrencyGroupCleanup.BatchSize = 2;
        });
        await using var provider = services.BuildServiceProvider();
        var descriptor = services.Single(service => service.ImplementationType?.Name == "ConcurrencyGroupRetentionHostedService");
        using var service = (BackgroundService)ActivatorUtilities.CreateInstance(provider, descriptor.ImplementationType!);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await service.StartAsync(cancellation.Token);
        await store.Drained.Task.WaitAsync(cancellation.Token);
        await service.StopAsync(cancellation.Token);

        store.Calls.Count.ShouldBe(2);
        store.Calls.ShouldAllBe(call => call.Retention == TimeSpan.FromDays(1) && call.Batch == 2);
    }

    [Fact]
    public async Task Cleanup_Disabled_RegistersOnlyOneLoopAndDoesNotClean()
    {
        var clock = new FakeTimeProvider();
        var store = new RetentionStore();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton<IConcurrencyGroupRetentionStore>(store);
        services.AddShedduellerWorker();
        services.AddShedduellerDashboard();
        services.Configure<ShedduellerOptions>(options => options.ConcurrencyGroupCleanup.Enabled = false);
        await using var provider = services.BuildServiceProvider();
        var descriptor = services.Single(service => service.ImplementationType?.Name == "ConcurrencyGroupRetentionHostedService");
        using var service = (BackgroundService)ActivatorUtilities.CreateInstance(provider, descriptor.ImplementationType!);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await service.StartAsync(cancellation.Token);
        clock.Advance(TimeSpan.FromHours(2));
        await service.StopAsync(cancellation.Token);

        store.Calls.ShouldBeEmpty();
    }

    private sealed class RetentionStore : IConcurrencyGroupRetentionStore
    {
        public ConcurrentQueue<(TimeSpan Retention, int Batch)> Calls { get; } = new();

        public TaskCompletionSource Drained { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<int> CleanupConcurrencyGroupsAsync(TimeSpan idleRetention, int batchSize, CancellationToken cancellationToken = default)
        {
            this.Calls.Enqueue((idleRetention, batchSize));
            if (this.Calls.Count == 1)
            {
                return ValueTask.FromResult(batchSize);
            }

            this.Drained.TrySetResult();
            return ValueTask.FromResult(0);
        }
    }
}
