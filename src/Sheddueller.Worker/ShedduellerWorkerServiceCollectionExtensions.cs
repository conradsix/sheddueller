#pragma warning disable IDE0130

namespace Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Sheddueller;
using Sheddueller.Runtime;
using Sheddueller.Storage;
using Sheddueller.Worker.Internal;

/// <summary>
/// Registration extensions for Sheddueller worker execution.
/// </summary>
public static class ShedduellerWorkerServiceCollectionExtensions
{
    /// <summary>
    /// Adds Sheddueller client services and starts a worker execution loop.
    /// </summary>
    /// <param name="services">The service collection to register with.</param>
    /// <param name="configure">Optional Sheddueller configuration, including storage provider registration.</param>
    /// <returns>The same service collection for chained registration.</returns>
    public static IServiceCollection AddShedduellerWorker(
        this IServiceCollection services,
        Action<ShedduellerBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSheddueller(configure);
        services.TryAddSingleton<IShedduellerNodeIdProvider, ShedduellerNodeIdProvider>();
        services.TryAddSingleton<ShedduellerJobLogEventQueue>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ILoggerProvider, ShedduellerJobLoggerProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, ShedduellerJobLogEventDispatcher>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IShedduellerStartupValidator, ShedduellerWorkerStartupValidator>());
        TryAddStartupValidationHostedService(services);
        TryAddJobRetentionHostedService(services);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, ShedduellerWorker>());

        return services;
    }

    private static void TryAddJobRetentionHostedService(IServiceCollection services)
    {
        if (services.Any(descriptor => descriptor.ServiceType == typeof(IJobRetentionStore)))
        {
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, ShedduellerJobRetentionService>());
        }
    }

    private static void TryAddStartupValidationHostedService(IServiceCollection services)
      => StartupValidationRegistration.TryAdd(
          services,
          ServiceDescriptor.Singleton<IHostedService, ShedduellerStartupValidationHostedService>());
}
