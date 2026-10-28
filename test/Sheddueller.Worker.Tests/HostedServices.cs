namespace Sheddueller.Worker.Tests;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

/// <summary>
/// Starts and stops a provider's hosted services in host order, so the job event dispatcher stops after the worker and
/// flushes the events its jobs queued.
/// </summary>
internal static class HostedServices
{
    public static async Task<IReadOnlyList<IHostedService>> StartAsync(
        IServiceProvider provider,
        CancellationToken cancellationToken)
    {
        var hostedServices = provider.GetServices<IHostedService>().ToArray();
        foreach (var hostedService in hostedServices)
        {
            await hostedService.StartAsync(cancellationToken);
        }

        return hostedServices;
    }

    public static async Task StopAsync(
        IEnumerable<IHostedService> hostedServices,
        CancellationToken cancellationToken)
    {
        foreach (var hostedService in hostedServices.Reverse())
        {
            await hostedService.StopAsync(cancellationToken);
        }
    }
}
