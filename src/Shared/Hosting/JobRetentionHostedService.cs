namespace Sheddueller.Hosting;

using System.Diagnostics.CodeAnalysis;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

using Sheddueller.Storage;

/// <summary>
/// Deletes terminal jobs older than the configured retention, in batches, every cleanup interval. Persisted cleanup
/// settings are re-read every minute. Each package derives from this to log under its own category and event IDs.
/// </summary>
internal abstract class JobRetentionHostedService(
    IServiceProvider serviceProvider,
    TimeProvider timeProvider,
    IOptions<ShedduellerOptions> options) : BackgroundService
{
    private static readonly TimeSpan PersistedSettingsPollingInterval = TimeSpan.FromMinutes(1);

    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly IOptions<ShedduellerOptions> _options = options;

    // Resolved on first use rather than every pass. PublicationOnly does not cache a failed resolution, so it is retried
    // on the next pass like any other cleanup failure.
    private readonly Lazy<IShedduellerCleanupConfigurationStore?> _settingsStore = new(
        serviceProvider.GetService<IShedduellerCleanupConfigurationStore>,
        LazyThreadSafetyMode.PublicationOnly);

    private readonly Lazy<IJobRetentionStore?> _retentionStore = new(
        serviceProvider.GetService<IJobRetentionStore>,
        LazyThreadSafetyMode.PublicationOnly);

    private DateTimeOffset? _lastCleanupAtUtc;

    protected abstract void LogRetentionStoreMissing();

    protected abstract void LogCleaned(int deletedCount);

    protected abstract void LogCleanupFailed(Exception exception);

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Retention cleanup failures are diagnostic and should not stop the host.")]
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var delay = PersistedSettingsPollingInterval;
                try
                {
                    delay = await this.CleanupIfDueAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    this.LogCleanupFailed(exception);
                }

                await Task.Delay(delay, this._timeProvider, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async ValueTask<TimeSpan> CleanupIfDueAsync(CancellationToken cancellationToken)
    {
        var settingsStore = this._settingsStore.Value;
        var retention = settingsStore is null
          ? JobRetentionCleanupConfiguration.FromOptions(this._options.Value.JobRetention)
          : await settingsStore
            .GetJobRetentionCleanupConfigurationAsync(JobRetentionCleanupConfiguration.FromOptions(this._options.Value.JobRetention), cancellationToken)
            .ConfigureAwait(false);
        var usesPersistedSettings = settingsStore is not null;
        if (!retention.Enabled)
        {
            return GetDelay(retention.CleanupInterval, usesPersistedSettings);
        }

        var now = this._timeProvider.GetUtcNow();
        if (usesPersistedSettings
            && this._lastCleanupAtUtc is { } lastCleanupAtUtc
            && now - lastCleanupAtUtc < retention.CleanupInterval)
        {
            return GetDelay(retention.CleanupInterval - (now - lastCleanupAtUtc), usesPersistedSettings);
        }

        var store = this._retentionStore.Value;
        if (store is null)
        {
            this.LogRetentionStoreMissing();
            return GetDelay(retention.CleanupInterval, usesPersistedSettings);
        }

        if (retention.CompletedRetention is null
            && retention.FailedRetention is null
            && retention.CanceledRetention is null)
        {
            this._lastCleanupAtUtc = now;
            return GetDelay(retention.CleanupInterval, usesPersistedSettings);
        }

        var request = new JobRetentionCleanupRequest(
          retention.CompletedRetention is { } completedRetention ? now.Subtract(completedRetention) : null,
          retention.FailedRetention is { } failedRetention ? now.Subtract(failedRetention) : null,
          retention.CanceledRetention is { } canceledRetention ? now.Subtract(canceledRetention) : null,
          retention.BatchSize);

        var totalDeleted = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var result = await store.CleanupTerminalJobsAsync(request, cancellationToken).ConfigureAwait(false);
            totalDeleted += result.DeletedCount;
            if (result.DeletedCount < retention.BatchSize)
            {
                break;
            }
        }

        if (totalDeleted > 0)
        {
            this.LogCleaned(totalDeleted);
        }

        this._lastCleanupAtUtc = now;

        return GetDelay(retention.CleanupInterval, usesPersistedSettings);
    }

    private static TimeSpan GetDelay(
        TimeSpan requestedDelay,
        bool usesPersistedSettings)
      => usesPersistedSettings && requestedDelay > PersistedSettingsPollingInterval
        ? PersistedSettingsPollingInterval
        : requestedDelay;
}
