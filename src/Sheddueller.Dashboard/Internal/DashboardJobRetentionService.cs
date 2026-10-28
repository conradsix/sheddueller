namespace Sheddueller.Dashboard.Internal;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Sheddueller.Hosting;

/// <summary>
/// The dashboard's job retention loop, logging under the dashboard's category and event IDs.
/// </summary>
internal sealed class DashboardJobRetentionService(
    IServiceProvider serviceProvider,
    TimeProvider timeProvider,
    IOptions<ShedduellerOptions> options,
    ILogger<DashboardJobRetentionService> logger) : JobRetentionHostedService(serviceProvider, timeProvider, options)
{
    private readonly ILogger<DashboardJobRetentionService> _logger = logger;

    protected override void LogRetentionStoreMissing()
      => this._logger.DashboardJobRetentionStoreMissing();

    protected override void LogCleaned(int deletedCount)
      => this._logger.DashboardJobRetentionCleaned(deletedCount);

    protected override void LogCleanupFailed(Exception exception)
      => this._logger.DashboardJobRetentionCleanupFailed(exception);
}
