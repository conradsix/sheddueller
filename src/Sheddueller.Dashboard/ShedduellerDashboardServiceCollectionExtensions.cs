#pragma warning disable IDE0130

namespace Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

using Sheddueller.Dashboard;
using Sheddueller.Dashboard.Internal;
using Sheddueller.Runtime;
using Sheddueller.Storage;

/// <summary>
/// Registration extensions for the embedded Sheddueller dashboard.
/// </summary>
public static class ShedduellerDashboardServiceCollectionExtensions
{
    /// <summary>
    /// Adds Sheddueller dashboard services without exposing routes.
    /// </summary>
    /// <param name="services">The service collection to register with.</param>
    /// <param name="configure">Optional dashboard configuration.</param>
    /// <returns>The same service collection for chained registration.</returns>
    public static IServiceCollection AddShedduellerDashboard(
        this IServiceCollection services,
        Action<ShedduellerDashboardOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<ShedduellerDashboardOptions>()
          .Configure(options => configure?.Invoke(options))
          .Validate(options => options.EventRetention > TimeSpan.Zero, "ShedduellerDashboardOptions.EventRetention must be positive.")
          .Validate(DashboardTagOrder.IsValid, "ShedduellerDashboardOptions.TagDisplayOrder cannot contain null, empty, or duplicate tag names.")
          .Validate(DashboardJobViews.IsValid, "ShedduellerDashboardOptions job views are invalid. Names and columns must be unique, the default must exist, and every layout must include JobId.")
          .ValidateOnStart();

        services.AddRazorComponents()
          .AddInteractiveServerComponents();
        services.AddSignalR();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<DashboardLiveUpdateStream>();
        services.TryAddSingleton<DashboardThroughputStore>();
        services.TryAddSingleton<IDashboardThroughputReader>(serviceProvider => serviceProvider.GetRequiredService<DashboardThroughputStore>());
        services.TryAddSingleton<DashboardMetricsSnapshotCache>();
        services.TryAddSingleton<IDashboardMetricsReader>(serviceProvider => serviceProvider.GetRequiredService<DashboardMetricsSnapshotCache>());
        services.Replace(ServiceDescriptor.Singleton<IJobEventNotifier, SignalRJobEventNotifier>());
        TryAddStartupValidationHostedService(services);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, DashboardThroughputHostedService>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, DashboardJobEventListenerService>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, JobEventRetentionService>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, DashboardJobRetentionService>());

        return services;
    }

    private static void TryAddStartupValidationHostedService(IServiceCollection services)
      => StartupValidationRegistration.TryAdd(
          services,
          ServiceDescriptor.Singleton<IHostedService, ShedduellerStartupValidationHostedService>());
}
