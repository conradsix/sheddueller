namespace Sheddueller.Worker.Tests;

using System.Collections.Concurrent;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

using Sheddueller.Storage;
using Sheddueller.Tests.Logging;
using Sheddueller.Worker.Internal;

using Shouldly;

public sealed class WorkerNodeRetentionTests
{
    private const int WorkerLoopFailedEventId = 1103;
    private const int WorkerNodeCleanupFailedEventId = 1142;

    private static readonly TimeSpan IdlePollingInterval = TimeSpan.FromMilliseconds(10);

    [Fact]
    public async Task DeadNodeCleanup_WorkerRunning_RunsWithTheRetentionAtMostOnceAMinute()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var timeProvider = new FakeTimeProvider();
        var store = new HousekeepingJobStore();
        using var logs = new TestLoggerProvider();
        await using var provider = CreateProvider(store, logs, timeProvider, registerNodeRetention: true);
        var worker = GetWorker(provider);

        await worker.StartAsync(cancellation.Token);
        await WaitUntilAsync(() => store.CleanupRetentions.Count == 1, cancellation.Token);

        await AdvanceAndWaitForHousekeepingAsync(store, timeProvider, TimeSpan.FromSeconds(30), cancellation.Token);
        store.CleanupRetentions.Count.ShouldBe(1);

        timeProvider.Advance(TimeSpan.FromSeconds(31));
        await WaitUntilAsync(() => store.CleanupRetentions.Count == 2, cancellation.Token);
        await worker.StopAsync(cancellation.Token);

        store.CleanupRetentions.ShouldAllBe(retention => retention == TimeSpan.FromMinutes(30));
    }

    [Fact]
    public async Task DeadNodeCleanup_RetentionNull_NeverRuns()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var timeProvider = new FakeTimeProvider();
        var store = new HousekeepingJobStore();
        using var logs = new TestLoggerProvider();
        await using var provider = CreateProvider(
          store,
          logs,
          timeProvider,
          registerNodeRetention: true,
          options => options.DeadNodeRetention = null);
        var worker = GetWorker(provider);

        await worker.StartAsync(cancellation.Token);
        await AdvanceAndWaitForHousekeepingAsync(store, timeProvider, TimeSpan.FromMinutes(2), cancellation.Token);
        await worker.StopAsync(cancellation.Token);

        store.CleanupRetentions.ShouldBeEmpty();
    }

    [Fact]
    public async Task DeadNodeCleanup_StoreWithoutTheCapability_KeepsDoingItsOtherHousekeeping()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var timeProvider = new FakeTimeProvider();
        var store = new HousekeepingJobStore();
        using var logs = new TestLoggerProvider();
        await using var provider = CreateProvider(store, logs, timeProvider, registerNodeRetention: false);
        var worker = GetWorker(provider);

        await worker.StartAsync(cancellation.Token);
        await AdvanceAndWaitForHousekeepingAsync(store, timeProvider, TimeSpan.FromMinutes(2), cancellation.Token);
        await worker.StopAsync(cancellation.Token);

        store.CleanupRetentions.ShouldBeEmpty();
        logs.HasEventId(WorkerNodeCleanupFailedEventId).ShouldBeFalse();
    }

    [Fact]
    public async Task DeadNodeCleanup_StoreFails_IsLoggedWithoutBackingOffClaiming()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var timeProvider = new FakeTimeProvider();
        var store = new HousekeepingJobStore { CleanupFailure = new InvalidOperationException("node cleanup unavailable") };
        using var logs = new TestLoggerProvider();
        await using var provider = CreateProvider(store, logs, timeProvider, registerNodeRetention: true);
        var worker = GetWorker(provider);

        await worker.StartAsync(cancellation.Token);
        await WaitUntilAsync(() => logs.HasEventId(WorkerNodeCleanupFailedEventId), cancellation.Token);
        var claimsAfterFailure = store.ClaimAttempts;
        await WaitUntilAsync(() => store.ClaimAttempts > claimsAfterFailure + 5, cancellation.Token);
        await worker.StopAsync(cancellation.Token);

        var failure = logs.SingleByEventId(WorkerNodeCleanupFailedEventId);
        failure.Level.ShouldBe(LogLevel.Warning);
        failure.Exception.ShouldBeOfType<InvalidOperationException>();
        logs.HasEventId(WorkerLoopFailedEventId).ShouldBeFalse();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task StartupValidation_DeadNodeRetentionNotLongerThanDeadThreshold_Fails(int minutes)
    {
        // The default dead node threshold is four times the 30 second lease: 2 minutes.
        var validator = new ShedduellerWorkerStartupValidator(Options.Create(new ShedduellerOptions
        {
            DeadNodeRetention = TimeSpan.FromMinutes(minutes),
        }));

        var exception = await Should.ThrowAsync<InvalidOperationException>(() => validator.ValidateAsync(CancellationToken.None).AsTask());

        exception.Message.ShouldBe("ShedduellerOptions.DeadNodeRetention must be null or greater than the dead node threshold.");
    }

    [Fact]
    public async Task StartupValidation_DeadNodeRetentionDefaultOrNull_Passes()
    {
        await new ShedduellerWorkerStartupValidator(Options.Create(new ShedduellerOptions())).ValidateAsync(CancellationToken.None);
        await new ShedduellerWorkerStartupValidator(Options.Create(new ShedduellerOptions { DeadNodeRetention = null }))
          .ValidateAsync(CancellationToken.None);
    }

    // Moves the clock on and waits until the worker has done its periodic store work at the new time.
    private static async Task AdvanceAndWaitForHousekeepingAsync(
        HousekeepingJobStore store,
        FakeTimeProvider timeProvider,
        TimeSpan elapsed,
        CancellationToken cancellationToken)
    {
        var runs = store.HousekeepingRuns;
        timeProvider.Advance(elapsed);
        await WaitUntilAsync(() => store.HousekeepingRuns > runs, cancellationToken);
    }

    private static ServiceProvider CreateProvider(
        HousekeepingJobStore store,
        TestLoggerProvider logs,
        TimeProvider timeProvider,
        bool registerNodeRetention,
        Action<ShedduellerOptions>? configureOptions = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder
          .SetMinimumLevel(LogLevel.Trace)
          .AddProvider(logs));
        services.AddSingleton(timeProvider);
        services.AddSingleton<IJobStore>(store);
        if (registerNodeRetention)
        {
            services.AddSingleton<IWorkerNodeRetentionStore>(store);
        }

        services.AddShedduellerWorker(builder => builder.ConfigureOptions(options =>
        {
            options.NodeId = "worker-node-retention";
            options.IdlePollingInterval = IdlePollingInterval;
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

    // Never has a job to claim; counts the periodic store work and records each dead node cleanup.
    private sealed class HousekeepingJobStore : StubJobStore, IWorkerNodeRetentionStore
    {
        private readonly ConcurrentQueue<TimeSpan> _cleanupRetentions = new();
        private int _claimAttempts;
        private int _housekeepingRuns;

        public Exception? CleanupFailure { get; init; }

        public IReadOnlyList<TimeSpan> CleanupRetentions
          => [.. this._cleanupRetentions];

        public int ClaimAttempts
          => Volatile.Read(ref this._claimAttempts);

        public int HousekeepingRuns
          => Volatile.Read(ref this._housekeepingRuns);

        public ValueTask<int> CleanupWorkerNodesAsync(TimeSpan retention, CancellationToken cancellationToken = default)
        {
            this._cleanupRetentions.Enqueue(retention);
            return this.CleanupFailure is { } failure ? ValueTask.FromException<int>(failure) : ValueTask.FromResult(1);
        }

        public override ValueTask<ClaimJobResult> TryClaimNextAsync(
            ClaimJobRequest request,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref this._claimAttempts);
            return ValueTask.FromResult<ClaimJobResult>(new ClaimJobResult.NoJobAvailable());
        }

        public override ValueTask RecordWorkerNodeHeartbeatAsync(
            WorkerNodeHeartbeatRequest request,
            CancellationToken cancellationToken = default)
          => ValueTask.CompletedTask;

        public override ValueTask<int> RecoverExpiredLeasesAsync(
            RecoverExpiredLeasesRequest request,
            CancellationToken cancellationToken = default)
          => ValueTask.FromResult(0);

        public override ValueTask<int> MaterializeDueRecurringSchedulesAsync(
            MaterializeDueRecurringSchedulesRequest request,
            CancellationToken cancellationToken = default)
        {
            // The last step of the periodic work before the node cleanup, which runs in the same pass.
            Interlocked.Increment(ref this._housekeepingRuns);
            return ValueTask.FromResult(0);
        }
    }
}
