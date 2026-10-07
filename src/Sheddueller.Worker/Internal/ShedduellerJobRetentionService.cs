namespace Sheddueller.Worker.Internal;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Sheddueller.Runtime;

internal sealed class ShedduellerJobRetentionService(
    IServiceProvider serviceProvider,
    TimeProvider timeProvider,
    IOptions<ShedduellerOptions> options,
    ILogger<ShedduellerJobRetentionService> logger) : BackgroundService
{
    private readonly JobRetentionCoordinator _coordinator = new(
      serviceProvider,
      timeProvider,
      options,
      logger.WorkerJobRetentionCleanupFailed,
      logger.WorkerJobRetentionStoreMissing,
      logger.WorkerJobRetentionCleaned);

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
      => this._coordinator.RunAsync(stoppingToken);
}
