namespace Sheddueller.Worker.Internal;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Sheddueller.Hosting;

/// <summary>
/// The worker's job retention loop, logging under the worker's category and event IDs.
/// </summary>
internal sealed class ShedduellerJobRetentionService(
    IServiceProvider serviceProvider,
    TimeProvider timeProvider,
    IOptions<ShedduellerOptions> options,
    ILogger<ShedduellerJobRetentionService> logger) : JobRetentionHostedService(serviceProvider, timeProvider, options)
{
    private readonly ILogger<ShedduellerJobRetentionService> _logger = logger;

    protected override void LogRetentionStoreMissing()
      => this._logger.WorkerJobRetentionStoreMissing();

    protected override void LogCleaned(int deletedCount)
      => this._logger.WorkerJobRetentionCleaned(deletedCount);

    protected override void LogCleanupFailed(Exception exception)
      => this._logger.WorkerJobRetentionCleanupFailed(exception);
}
