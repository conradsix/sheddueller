namespace Sheddueller.Tests;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Sheddueller.Runtime;
using Sheddueller.Serialization;
using Sheddueller.Storage;

using Shouldly;

public sealed class RegistrationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void HostedValidation_WorkerAndDashboard_IsRegisteredOnceBeforeExistingServices(bool workerFirst)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IHostedService, ExistingHostedService>();
        services.AddSingleton<IJobStore, RecordingJobStore>();
        services.AddSingleton<IJobRetentionStore, RecordingRetentionStore>();
        if (workerFirst)
        {
            services.AddShedduellerWorker();
            services.AddShedduellerDashboard();
        }
        else
        {
            services.AddShedduellerDashboard();
            services.AddShedduellerWorker();
        }

        services.AddShedduellerWorker();
        services.AddShedduellerDashboard();
        var hosted = services.Where(descriptor => descriptor.ServiceType == typeof(IHostedService)).ToArray();

        hosted[0].ImplementationType!.Name.ShouldBe("ShedduellerStartupValidationHostedService");
        hosted.Count(descriptor => descriptor.ImplementationType!.Name == "ShedduellerStartupValidationHostedService").ShouldBe(1);
        hosted.Count(descriptor => descriptor.ImplementationType!.Name.EndsWith("JobRetentionService", StringComparison.Ordinal)).ShouldBe(2);
        services.Count(descriptor => descriptor.ServiceType == typeof(ShedduellerHostedStartupValidationMarker)).ShouldBe(1);
    }

    [Fact]
    public void AddSheddueller_CustomProvider_RegistersCoreServices()
    {
        var serializer = new PassThroughSerializer();
        var services = new ServiceCollection();
        services.AddSingleton<RecordingJobStore>();
        services.AddSingleton<IJobStore>(serviceProvider => serviceProvider.GetRequiredService<RecordingJobStore>());

        services.AddSheddueller(builder => builder
          .UseJobPayloadSerializer(serializer)
          .ConfigureOptions(options => options.NodeId = "node-a"));

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IJobEnqueuer>().ShouldNotBeNull();
        provider.GetRequiredService<IConcurrencyGroupManager>().ShouldNotBeNull();
        provider.GetRequiredService<IJobStore>().ShouldBeSameAs(provider.GetRequiredService<RecordingJobStore>());
        provider.GetRequiredService<IJobPayloadSerializer>().ShouldBeSameAs(serializer);
        provider.GetRequiredService<ILoggerFactory>().ShouldNotBeNull();
        provider.GetServices<IHostedService>().ShouldBeEmpty();
        provider.GetServices<IShedduellerStartupValidator>().Count().ShouldBe(1);
    }

    [Fact]
    public void AddSheddueller_MissingJobStoreProvider_DoesNotRegisterHostedValidation()
    {
        var services = new ServiceCollection();
        services.AddSheddueller();
        using var provider = services.BuildServiceProvider();

        provider.GetServices<IHostedService>().ShouldBeEmpty();
    }

    [Fact]
    public void AddSheddueller_WorkerOnlyOptions_AreNotHostedValidated()
    {
        var services = new ServiceCollection();
        services.AddSingleton<RecordingJobStore>();
        services.AddSingleton<IJobStore>(serviceProvider => serviceProvider.GetRequiredService<RecordingJobStore>());
        services.AddSheddueller(builder => builder.ConfigureOptions(options => options.MaxConcurrentExecutionsPerNode = 0));
        using var provider = services.BuildServiceProvider();

        provider.GetServices<IHostedService>().ShouldBeEmpty();
    }

    [Fact]
    public void AddSheddueller_ServiceProviderAwareOptions_CanReadRegisteredServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new NodeConfiguration("node-from-di"));
        services.AddSheddueller(builder => builder.ConfigureOptions((serviceProvider, options) =>
        {
            options.NodeId = serviceProvider.GetRequiredService<NodeConfiguration>().NodeId;
        }));
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IOptions<ShedduellerOptions>>().Value.NodeId.ShouldBe("node-from-di");
    }

    private sealed class PassThroughSerializer : IJobPayloadSerializer
    {
        public ValueTask<SerializedJobPayload> SerializeAsync(
          IReadOnlyList<object?> arguments,
          IReadOnlyList<Type> parameterTypes,
          CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(new SerializedJobPayload("test/pass-through", []));
        }

        public ValueTask<IReadOnlyList<object?>> DeserializeAsync(
          SerializedJobPayload payload,
          IReadOnlyList<Type> parameterTypes,
          CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult<IReadOnlyList<object?>>(Array.Empty<object?>());
        }
    }

    private sealed record NodeConfiguration(string NodeId);

    private sealed class ExistingHostedService : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RecordingRetentionStore : IJobRetentionStore
    {
        public ValueTask<JobRetentionCleanupResult> CleanupTerminalJobsAsync(JobRetentionCleanupRequest request, CancellationToken cancellationToken = default)
          => ValueTask.FromResult(new JobRetentionCleanupResult(0));
    }
}
