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
        services.AddSingleton<UnusedJobStore>();
        services.AddSingleton<IJobStore>(serviceProvider => serviceProvider.GetRequiredService<UnusedJobStore>());

        services.AddShedduellerWorker();

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IJobEnqueuer>().ShouldNotBeNull();
        provider.GetRequiredService<IJobStore>().ShouldBeSameAs(provider.GetRequiredService<UnusedJobStore>());
        provider.GetRequiredService<IShedduellerNodeIdProvider>().ShouldNotBeNull();
        provider.GetServices<IHostedService>().Count().ShouldBe(3);
        provider.GetServices<IHostedService>().ShouldContain(service => service.GetType() == typeof(ShedduellerJobLogEventDispatcher));
        provider.GetServices<IHostedService>().ShouldContain(service => service.GetType() == typeof(ShedduellerWorker));
    }

    [Fact]
    public void AddShedduellerWorker_RetentionStore_RegistersRetentionHostedService()
    {
        var services = new ServiceCollection();
        services.AddSingleton<UnusedJobStore>();
        services.AddSingleton<IJobStore>(serviceProvider => serviceProvider.GetRequiredService<UnusedJobStore>());
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
        services.AddSingleton<UnusedJobStore>();
        services.AddSingleton<IJobStore>(serviceProvider => serviceProvider.GetRequiredService<UnusedJobStore>());
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
        services.AddSingleton<UnusedJobStore>();
        services.AddSingleton<IJobStore>(serviceProvider => serviceProvider.GetRequiredService<UnusedJobStore>());
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
        services.AddSingleton<UnusedJobStore>();
        services.AddSingleton<IJobStore>(serviceProvider => serviceProvider.GetRequiredService<UnusedJobStore>());
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

    // Registration only resolves the store; nothing here calls it.
    private sealed class UnusedJobStore : StubJobStore;
}
