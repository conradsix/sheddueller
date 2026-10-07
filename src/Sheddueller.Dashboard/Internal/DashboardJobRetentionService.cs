namespace Sheddueller.Dashboard.Internal;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Sheddueller.Runtime;

internal sealed class DashboardJobRetentionService(
    IServiceProvider serviceProvider,
    TimeProvider timeProvider,
    IOptions<ShedduellerOptions> options,
    ILogger<DashboardJobRetentionService> logger) : BackgroundService
{
    private readonly JobRetentionCoordinator _coordinator = new(
      serviceProvider,
      timeProvider,
      options,
      logger.DashboardJobRetentionCleanupFailed,
      logger.DashboardJobRetentionStoreMissing,
      logger.DashboardJobRetentionCleaned);

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
      => this._coordinator.RunAsync(stoppingToken);
}
