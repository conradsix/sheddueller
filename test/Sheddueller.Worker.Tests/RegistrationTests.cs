namespace Sheddueller.Worker.Tests;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Sheddueller;
using Sheddueller.Storage;
using Sheddueller.Worker.Internal;

using Shouldly;

public sealed class RegistrationTests
{
    [Fact]
    public void AddShedduellerWorker_RegistersClientAndWorkerServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton<RecordingJobStore>();
        services.AddSingleton<IJobStore>(serviceProvider => serviceProvider.GetRequiredService<RecordingJobStore>());

        services.AddShedduellerWorker();

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IJobEnqueuer>().ShouldNotBeNull();
        provider.GetRequiredService<IJobStore>().ShouldBeSameAs(provider.GetRequiredService<RecordingJobStore>());
        provider.GetRequiredService<IShedduellerNodeIdProvider>().ShouldNotBeNull();
        provider.GetServices<IHostedService>().Count().ShouldBe(3);
        provider.GetServices<IHostedService>().ShouldContain(service => service.GetType() == typeof(ShedduellerJobLogEventDispatcher));
        provider.GetServices<IHostedService>().ShouldContain(service => service.GetType() == typeof(ShedduellerWorker));
    }

    [Fact]
    public void AddShedduellerWorker_RetentionStore_RegistersRetentionHostedService()
    {
        var services = new ServiceCollection();
        services.AddSingleton<RecordingJobStore>();
        services.AddSingleton<IJobStore>(serviceProvider => serviceProvider.GetRequiredService<RecordingJobStore>());
        services.AddSingleton<IJobRetentionStore, RecordingRetentionStore>();

        services.AddShedduellerWorker();

        using var provider = services.BuildServiceProvider();

        provider.GetServices<IHostedService>().ShouldContain(service => service.GetType() == typeof(ShedduellerJobRetentionService));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AddShedduellerWorkerAndDashboard_RetentionStore_RegistersSharedHostedServicesOnce(bool dashboardFirst)
    {
        var services = new ServiceCollection();
        services.AddSingleton<RecordingJobStore>();
        services.AddSingleton<IJobStore>(serviceProvider => serviceProvider.GetRequiredService<RecordingJobStore>());
        services.AddSingleton<IJobRetentionStore, RecordingRetentionStore>();

        if (dashboardFirst)
        {
            services.AddShedduellerDashboard();
            services.AddShedduellerWorker();
        }
        else
        {
            services.AddShedduellerWorker();
            services.AddShedduellerDashboard();
        }

        // Each package compiles its own copy of the shared services, so match the dashboard's by name.
        var hostedServiceTypes = services
          .Where(descriptor => descriptor.ServiceType == typeof(IHostedService))
          .Select(descriptor => descriptor.ImplementationType?.Name)
          .ToArray();
        hostedServiceTypes.Count(name => name is nameof(ShedduellerJobRetentionService) or "DashboardJobRetentionService").ShouldBe(1);
        hostedServiceTypes.Count(name => name == "ShedduellerStartupValidationHostedService").ShouldBe(1);
        hostedServiceTypes.ShouldContain(dashboardFirst ? "DashboardJobRetentionService" : nameof(ShedduellerJobRetentionService));
    }

    [Fact]
    public async Task StartupValidation_InvalidWorkerOption_FailsStart()
    {
        var services = new ServiceCollection();
        services.AddSingleton<RecordingJobStore>();
        services.AddSingleton<IJobStore>(serviceProvider => serviceProvider.GetRequiredService<RecordingJobStore>());
        services.AddShedduellerWorker(builder => builder.ConfigureOptions(options => options.MaxConcurrentExecutionsPerNode = 0));
        using var provider = services.BuildServiceProvider();

        var exception = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            foreach (var hostedService in provider.GetServices<IHostedService>())
            {
                await hostedService.StartAsync(CancellationToken.None);
            }
        });

        exception.Message.ShouldContain("ShedduellerOptions.MaxConcurrentExecutionsPerNode must be positive.");
    }

    [Fact]
    public async Task StartupValidation_InvalidJobRetentionOption_FailsStart()
    {
        var services = new ServiceCollection();
        services.AddSingleton<RecordingJobStore>();
        services.AddSingleton<IJobStore>(serviceProvider => serviceProvider.GetRequiredService<RecordingJobStore>());
        services.AddShedduellerWorker(builder => builder.ConfigureOptions(options => options.JobRetention.BatchSize = 0));
        using var provider = services.BuildServiceProvider();

        var exception = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            foreach (var hostedService in provider.GetServices<IHostedService>())
            {
                await hostedService.StartAsync(CancellationToken.None);
            }
        });

        exception.Message.ShouldContain("ShedduellerOptions.JobRetention.BatchSize must be positive.");
    }

    private sealed class RecordingRetentionStore : IJobRetentionStore
    {
        public ValueTask<JobRetentionCleanupResult> CleanupTerminalJobsAsync(
            JobRetentionCleanupRequest request,
            CancellationToken cancellationToken = default)
          => ValueTask.FromResult(new JobRetentionCleanupResult(0));
    }

    private sealed class RecordingJobStore : IJobStore
    {
        public ValueTask<EnqueueJobResult> EnqueueAsync(
            EnqueueJobRequest request,
            CancellationToken cancellationToken = default)
          => ValueTask.FromResult(new EnqueueJobResult(request.JobId, 1));

        public ValueTask<IReadOnlyList<EnqueueJobResult>> EnqueueManyAsync(
            IReadOnlyList<EnqueueJobRequest> requests,
            CancellationToken cancellationToken = default)
          => ValueTask.FromResult<IReadOnlyList<EnqueueJobResult>>(
              [.. requests.Select((request, index) => new EnqueueJobResult(request.JobId, index + 1))]);

        public ValueTask<ClaimJobResult> TryClaimNextAsync(
            ClaimJobRequest request,
            CancellationToken cancellationToken = default)
          => ValueTask.FromResult<ClaimJobResult>(new ClaimJobResult.NoJobAvailable());

        public ValueTask<bool> MarkCompletedAsync(
            CompleteJobRequest request,
            CancellationToken cancellationToken = default)
          => ValueTask.FromResult(true);

        public ValueTask<bool> MarkFailedAsync(
            FailJobRequest request,
            CancellationToken cancellationToken = default)
          => ValueTask.FromResult(true);

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
          => ValueTask.CompletedTask;

        public ValueTask SetConcurrencyDefaultLimitAsync(
            SetConcurrencyDefaultLimitRequest request,
            CancellationToken cancellationToken = default)
          => ValueTask.CompletedTask;

        public ValueTask ClearConcurrencyLimitOverrideAsync(
            ClearConcurrencyLimitOverrideRequest request,
            CancellationToken cancellationToken = default)
          => ValueTask.CompletedTask;

        public ValueTask<int?> GetConfiguredConcurrencyLimitAsync(
            string groupKey,
            CancellationToken cancellationToken = default)
          => ValueTask.FromResult<int?>(null);

        public ValueTask<RecurringScheduleUpsertResult> CreateOrUpdateRecurringScheduleAsync(
            UpsertRecurringScheduleRequest request,
            CancellationToken cancellationToken = default)
          => ValueTask.FromResult(RecurringScheduleUpsertResult.Created);

        public ValueTask<RecurringScheduleTriggerResult> TriggerRecurringScheduleAsync(
            TriggerRecurringScheduleRequest request,
            CancellationToken cancellationToken = default)
          => ValueTask.FromResult(new RecurringScheduleTriggerResult(RecurringScheduleTriggerStatus.NotFound));

        public ValueTask<bool> DeleteRecurringScheduleAsync(
            string scheduleKey,
            CancellationToken cancellationToken = default)
          => ValueTask.FromResult(true);

        public ValueTask<bool> PauseRecurringScheduleAsync(
            string scheduleKey,
            DateTimeOffset pausedAtUtc,
            CancellationToken cancellationToken = default)
          => ValueTask.FromResult(true);

        public ValueTask<bool> ResumeRecurringScheduleAsync(
            string scheduleKey,
            DateTimeOffset resumedAtUtc,
            CancellationToken cancellationToken = default)
          => ValueTask.FromResult(true);

        public ValueTask<RecurringScheduleInfo?> GetRecurringScheduleAsync(
            string scheduleKey,
            CancellationToken cancellationToken = default)
          => ValueTask.FromResult<RecurringScheduleInfo?>(null);

        public ValueTask<IReadOnlyList<RecurringScheduleInfo>> ListRecurringSchedulesAsync(
            CancellationToken cancellationToken = default)
          => ValueTask.FromResult<IReadOnlyList<RecurringScheduleInfo>>([]);

        public ValueTask<int> MaterializeDueRecurringSchedulesAsync(
            MaterializeDueRecurringSchedulesRequest request,
            CancellationToken cancellationToken = default)
          => ValueTask.FromResult(0);
    }
}
