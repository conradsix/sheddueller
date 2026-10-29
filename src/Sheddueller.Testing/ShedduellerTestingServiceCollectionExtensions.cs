#pragma warning disable IDE0130

namespace Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.DependencyInjection.Extensions;

using Sheddueller;
using Sheddueller.Serialization;
using Sheddueller.Testing;

/// <summary>
/// Registration extensions for Sheddueller testing services.
/// </summary>
public static class ShedduellerTestingServiceCollectionExtensions
{
    /// <summary>
    /// Adds Sheddueller testing services to the service collection.
    /// </summary>
    /// <param name="services">The service collection to register with.</param>
    /// <returns>The same service collection for chained registration.</returns>
    public static IServiceCollection AddShedduellerTesting(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IJobPayloadSerializer, SystemTextJsonJobPayloadSerializer>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<FakeConcurrencyGroupConfigurationReader>();
        services.Replace(ServiceDescriptor.Singleton<IConcurrencyGroupConfigurationReader>(serviceProvider => serviceProvider.GetRequiredService<FakeConcurrencyGroupConfigurationReader>()));
        services.TryAddSingleton<CapturingJobEnqueuer>();
        services.TryAddSingleton<CapturingRecurringScheduleManager>();
        services.Replace(ServiceDescriptor.Singleton<IJobEnqueuer>(serviceProvider => serviceProvider.GetRequiredService<CapturingJobEnqueuer>()));
        services.Replace(ServiceDescriptor.Singleton<IRecurringScheduleManager>(serviceProvider => serviceProvider.GetRequiredService<CapturingRecurringScheduleManager>()));

        return services;
    }
}
