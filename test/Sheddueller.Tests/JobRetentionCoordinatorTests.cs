namespace Sheddueller.Tests;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

using Sheddueller.Runtime;
using Sheddueller.Storage;

using Shouldly;

public sealed class JobRetentionCoordinatorTests
{
    [Fact]
    public async Task Cleanup_FullBatches_UsesOneCutoffAndReportsTotalDeleted()
    {
        var time = new FakeTimeProvider();
        var store = new RetentionStore(2, 2, 1);
        var services = new ServiceCollection();
        services.AddSingleton<IJobRetentionStore>(store);
        using var provider = services.BuildServiceProvider();
        var options = new ShedduellerOptions();
        options.JobRetention.BatchSize = 2;
        var deleted = 0;
        var coordinator = Create(provider, time, options, count => deleted = count);

        var delay = await coordinator.CleanupIfDueAsync(CancellationToken.None);

        delay.ShouldBe(options.JobRetention.CleanupInterval);
        deleted.ShouldBe(5);
        store.Requests.Count.ShouldBe(3);
        store.Requests.Distinct().Count().ShouldBe(1);
        store.Requests[0].CompletedBeforeUtc.ShouldBe(time.GetUtcNow() - options.JobRetention.CompletedRetention);
    }

    [Fact]
    public async Task Cleanup_PersistedInterval_PollsWithoutCleaningAgainUntilDue()
    {
        var time = new FakeTimeProvider();
        var store = new RetentionStore(0, 0);
        var settings = new ConfigurationStore(new JobRetentionCleanupConfiguration(true, TimeSpan.FromDays(3), null, null, TimeSpan.FromHours(1), 12));
        var services = new ServiceCollection();
        services.AddSingleton<IJobRetentionStore>(store);
        services.AddSingleton<IShedduellerCleanupConfigurationStore>(settings);
        using var provider = services.BuildServiceProvider();
        var coordinator = Create(provider, time, new ShedduellerOptions());

        (await coordinator.CleanupIfDueAsync(CancellationToken.None)).ShouldBe(TimeSpan.FromMinutes(1));
        time.Advance(TimeSpan.FromMinutes(30));
        (await coordinator.CleanupIfDueAsync(CancellationToken.None)).ShouldBe(TimeSpan.FromMinutes(1));
        store.Requests.Count.ShouldBe(1);
        store.Requests[0].CompletedBeforeUtc.ShouldBe(time.GetUtcNow().AddMinutes(-30).AddDays(-3));
        time.Advance(TimeSpan.FromMinutes(30));
        await coordinator.CleanupIfDueAsync(CancellationToken.None);
        store.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Cleanup_DisabledOrIndefiniteRetention_DoesNotDeleteJobs()
    {
        var services = new ServiceCollection();
        var store = new RetentionStore();
        services.AddSingleton<IJobRetentionStore>(store);
        using var provider = services.BuildServiceProvider();
        var options = new ShedduellerOptions();
        options.JobRetention.Enabled = false;
        var coordinator = Create(provider, new FakeTimeProvider(), options);

        await coordinator.CleanupIfDueAsync(CancellationToken.None);
        options.JobRetention.Enabled = true;
        options.JobRetention.CompletedRetention = null;
        options.JobRetention.FailedRetention = null;
        options.JobRetention.CanceledRetention = null;
        await coordinator.CleanupIfDueAsync(CancellationToken.None);

        store.Requests.ShouldBeEmpty();
    }

    private static JobRetentionCoordinator Create(IServiceProvider provider, TimeProvider time, ShedduellerOptions options, Action<int>? cleaned = null)
      => new(provider, time, Options.Create(options), exception => throw exception, () => throw new InvalidOperationException("Missing retention store."), cleaned ?? (_ => { }));

    private sealed class RetentionStore(params int[] counts) : IJobRetentionStore
    {
        private int _index;

        public List<JobRetentionCleanupRequest> Requests { get; } = [];

        public ValueTask<JobRetentionCleanupResult> CleanupTerminalJobsAsync(JobRetentionCleanupRequest request, CancellationToken cancellationToken = default)
        {
            this.Requests.Add(request);
            return ValueTask.FromResult(new JobRetentionCleanupResult(counts[this._index++]));
        }
    }

    private sealed class ConfigurationStore(JobRetentionCleanupConfiguration retention) : IShedduellerCleanupConfigurationStore
    {
        public ValueTask<ShedduellerCleanupConfiguration> GetCleanupConfigurationAsync(ShedduellerCleanupConfiguration defaultConfiguration, CancellationToken cancellationToken = default)
          => ValueTask.FromResult(defaultConfiguration with { JobRetention = retention });

        public ValueTask<JobRetentionCleanupConfiguration> GetJobRetentionCleanupConfigurationAsync(JobRetentionCleanupConfiguration defaultConfiguration, CancellationToken cancellationToken = default)
          => ValueTask.FromResult(retention);

        public ValueTask<JobEventCleanupConfiguration> GetJobEventCleanupConfigurationAsync(JobEventCleanupConfiguration defaultConfiguration, CancellationToken cancellationToken = default)
          => ValueTask.FromResult(defaultConfiguration);

        public ValueTask<MetricsCleanupConfiguration> GetMetricsCleanupConfigurationAsync(MetricsCleanupConfiguration defaultConfiguration, CancellationToken cancellationToken = default)
          => ValueTask.FromResult(defaultConfiguration);

        public ValueTask SetCleanupConfigurationAsync(ShedduellerCleanupConfiguration configuration, CancellationToken cancellationToken = default)
          => ValueTask.CompletedTask;
    }
}
