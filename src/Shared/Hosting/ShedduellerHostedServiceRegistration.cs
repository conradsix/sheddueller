namespace Sheddueller.Hosting;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

using Sheddueller.Runtime;
using Sheddueller.Storage;

/// <summary>
/// Registers the hosted services the worker and dashboard packages share. Each package compiles its own copy of these
/// types, so the guards use markers from the core package: a host that adds both packages runs each service once.
/// </summary>
internal static class ShedduellerHostedServiceRegistration
{
    /// <summary>
    /// Adds the startup validation hosted service ahead of every hosted service registered so far, unless a Sheddueller
    /// package has already added it.
    /// </summary>
    public static void TryAddStartupValidationHostedService(IServiceCollection services)
    {
        if (services.Any(descriptor => descriptor.ServiceType == typeof(ShedduellerHostedStartupValidationMarker)))
        {
            return;
        }

        services.AddSingleton<ShedduellerHostedStartupValidationMarker>();
        var descriptor = ServiceDescriptor.Singleton<IHostedService, ShedduellerStartupValidationHostedService>();
        for (var index = 0; index < services.Count; index++)
        {
            if (services[index].ServiceType == typeof(IHostedService))
            {
                services.Insert(index, descriptor);
                return;
            }
        }

        services.Add(descriptor);
    }

    /// <summary>
    /// Adds one unused concurrency-group cleanup loop per host.
    /// </summary>
    public static void TryAddConcurrencyGroupRetentionHostedService(IServiceCollection services)
    {
        if (!services.Any(descriptor => descriptor.ServiceType == typeof(IConcurrencyGroupRetentionStore))
            || services.Any(descriptor => descriptor.ServiceType == typeof(ShedduellerConcurrencyGroupRetentionHostedServiceMarker)))
        {
            return;
        }

        services.AddSingleton<ShedduellerConcurrencyGroupRetentionHostedServiceMarker>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, ConcurrencyGroupRetentionHostedService>());
    }

    public static void TryAddJobRetentionHostedService<TService>(IServiceCollection services)
      where TService : JobRetentionHostedService
    {
        if (services.Any(descriptor => descriptor.ServiceType == typeof(ShedduellerJobRetentionHostedServiceMarker)))
        {
            return;
        }

        services.AddSingleton<ShedduellerJobRetentionHostedServiceMarker>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, TService>());
    }
}
