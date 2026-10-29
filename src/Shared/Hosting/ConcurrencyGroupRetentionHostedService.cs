namespace Sheddueller.Hosting;

using System.Diagnostics.CodeAnalysis;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Sheddueller.Storage;

internal sealed partial class ConcurrencyGroupRetentionHostedService(
    IServiceProvider services,
    TimeProvider clock,
    IOptions<ShedduellerOptions> options,
    ILogger<ConcurrencyGroupRetentionHostedService> logger) : BackgroundService
{
    private DateTimeOffset? _lastCleanupAt;

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Housekeeping failures must not interrupt workers.")]
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var store = services.GetService<IConcurrencyGroupRetentionStore>();
        if (store is null)
        {
            return;
        }

        var settings = services.GetService<IShedduellerCleanupConfigurationStore>();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var defaults = ConcurrencyGroupCleanupConfiguration.FromOptions(options.Value.ConcurrencyGroupCleanup);
                    var config = settings is null ? defaults : await settings.GetConcurrencyGroupCleanupConfigurationAsync(defaults, stoppingToken).ConfigureAwait(false);
                    config.Validate();
                    var now = clock.GetUtcNow();
                    if (config.Enabled && (this._lastCleanupAt is null || now - this._lastCleanupAt >= config.CleanupInterval))
                    {
                        var total = 0;
                        int deleted;
                        do
                        {
                            deleted = await store.CleanupConcurrencyGroupsAsync(config.IdleRetention, config.BatchSize, stoppingToken).ConfigureAwait(false);
                            total += deleted;
                        }
                        while (deleted == config.BatchSize && !stoppingToken.IsCancellationRequested);

                        this._lastCleanupAt = now;
                        if (total > 0)
                        {
                            LogCleaned(logger, total);
                        }
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    LogFailed(logger, exception);
                }

                await Task.Delay(TimeSpan.FromMinutes(1), clock, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    [LoggerMessage(80, LogLevel.Information, "Removed {DeletedCount} unused concurrency groups.")]
    private static partial void LogCleaned(ILogger logger, int deletedCount);

    [LoggerMessage(81, LogLevel.Warning, "Concurrency group cleanup failed.")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
