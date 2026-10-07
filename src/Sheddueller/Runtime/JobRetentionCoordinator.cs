namespace Sheddueller.Runtime;

using System.Diagnostics.CodeAnalysis;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Sheddueller.Storage;

internal sealed class JobRetentionCoordinator(
    IServiceProvider serviceProvider,
    TimeProvider timeProvider,
    IOptions<ShedduellerOptions> options,
    Action<Exception> cleanupFailed,
    Action storeMissing,
    Action<int> cleaned)
{
    private static readonly TimeSpan PersistedSettingsPollingInterval = TimeSpan.FromMinutes(1);
    private DateTimeOffset? _lastCleanupAtUtc;

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Retention cleanup failures are diagnostic and should not stop the host.")]
    public async Task RunAsync(CancellationToken stoppingToken)
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
                    cleanupFailed(exception);
                }

                await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    internal async ValueTask<TimeSpan> CleanupIfDueAsync(CancellationToken cancellationToken)
    {
        var settingsStore = serviceProvider.GetService<IShedduellerCleanupConfigurationStore>();
        var retention = settingsStore is null
          ? JobRetentionCleanupConfiguration.FromOptions(options.Value.JobRetention)
          : await settingsStore
            .GetJobRetentionCleanupConfigurationAsync(JobRetentionCleanupConfiguration.FromOptions(options.Value.JobRetention), cancellationToken)
            .ConfigureAwait(false);
        var usesPersistedSettings = settingsStore is not null;
        if (!retention.Enabled)
        {
            return GetDelay(retention.CleanupInterval, usesPersistedSettings);
        }

        var now = timeProvider.GetUtcNow();
        if (usesPersistedSettings
            && this._lastCleanupAtUtc is { } lastCleanupAtUtc
            && now - lastCleanupAtUtc < retention.CleanupInterval)
        {
            return GetDelay(retention.CleanupInterval - (now - lastCleanupAtUtc), usesPersistedSettings);
        }

        var store = serviceProvider.GetService<IJobRetentionStore>();
        if (store is null)
        {
            storeMissing();
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
            cleaned(totalDeleted);
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
