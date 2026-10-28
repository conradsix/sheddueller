namespace Sheddueller.Worker.Tests;

using System.Globalization;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Sheddueller.Storage;
using Sheddueller.Worker.Internal;

using Shouldly;

public sealed class JobRetentionServiceTests
{
    [Fact]
    public async Task Cleanup_PersistedSettingsStore_UsesPersistedJobRetention()
    {
        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var now = DateTimeOffset.Parse("2026-04-20T12:00:00Z", CultureInfo.InvariantCulture);
        var retentionStore = new RecordingRetentionStore();
        var settingsStore = new RecordingCleanupConfigurationStore(new JobRetentionCleanupConfiguration(
          Enabled: true,
          CompletedRetention: TimeSpan.FromDays(3),
          FailedRetention: null,
          CanceledRetention: null,
          CleanupInterval: TimeSpan.FromHours(1),
          BatchSize: 12));
        var services = new ServiceCollection();
        services.AddSingleton<IJobRetentionStore>(retentionStore);
        services.AddSingleton<IShedduellerCleanupConfigurationStore>(settingsStore);
        await using var serviceProvider = services.BuildServiceProvider();
        var options = new ShedduellerOptions();
        options.JobRetention.CompletedRetention = TimeSpan.FromDays(1);
        using var service = new ShedduellerJobRetentionService(
          serviceProvider,
          new FixedTimeProvider(now),
          Options.Create(options),
          NullLogger<ShedduellerJobRetentionService>.Instance);

        await service.StartAsync(cancellationTokenSource.Token);
        await retentionStore.CleanupCalled.Task.WaitAsync(cancellationTokenSource.Token);
        await service.StopAsync(cancellationTokenSource.Token);

        var request = retentionStore.Request.ShouldNotBeNull();
        request.CompletedBeforeUtc.ShouldBe(now.AddDays(-3));
        request.FailedBeforeUtc.ShouldBeNull();
        request.CanceledBeforeUtc.ShouldBeNull();
        request.BatchSize.ShouldBe(12);
    }

    private sealed class RecordingCleanupConfigurationStore(JobRetentionCleanupConfiguration configuration) : IShedduellerCleanupConfigurationStore
    {
        public ValueTask<ShedduellerCleanupConfiguration> GetCleanupConfigurationAsync(
            ShedduellerCleanupConfiguration defaultConfiguration,
            CancellationToken cancellationToken = default)
          => ValueTask.FromResult(defaultConfiguration with { JobRetention = configuration });

        public ValueTask<JobRetentionCleanupConfiguration> GetJobRetentionCleanupConfigurationAsync(
            JobRetentionCleanupConfiguration defaultConfiguration,
            CancellationToken cancellationToken = default)
          => ValueTask.FromResult(configuration);

        public ValueTask<JobEventCleanupConfiguration> GetJobEventCleanupConfigurationAsync(
            JobEventCleanupConfiguration defaultConfiguration,
            CancellationToken cancellationToken = default)
          => ValueTask.FromResult(defaultConfiguration);

        public ValueTask<MetricsCleanupConfiguration> GetMetricsCleanupConfigurationAsync(
            MetricsCleanupConfiguration defaultConfiguration,
            CancellationToken cancellationToken = default)
          => ValueTask.FromResult(defaultConfiguration);

        public ValueTask SetCleanupConfigurationAsync(
            ShedduellerCleanupConfiguration configuration,
            CancellationToken cancellationToken = default)
          => ValueTask.CompletedTask;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
          => now;
    }
}
