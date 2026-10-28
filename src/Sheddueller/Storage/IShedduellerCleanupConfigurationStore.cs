namespace Sheddueller.Storage;

/// <summary>
/// Stores cluster-wide cleanup configuration.
/// </summary>
public interface IShedduellerCleanupConfigurationStore
{
    /// <summary>
    /// Gets the full cleanup configuration, using the supplied defaults for any setting that has not been persisted.
    /// </summary>
    ValueTask<ShedduellerCleanupConfiguration> GetCleanupConfigurationAsync(
        ShedduellerCleanupConfiguration defaultConfiguration,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets terminal job cleanup configuration, or the supplied default when it has not been persisted.
    /// </summary>
    ValueTask<JobRetentionCleanupConfiguration> GetJobRetentionCleanupConfigurationAsync(
        JobRetentionCleanupConfiguration defaultConfiguration,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets durable job-event cleanup configuration, or the supplied default when it has not been persisted.
    /// </summary>
    ValueTask<JobEventCleanupConfiguration> GetJobEventCleanupConfigurationAsync(
        JobEventCleanupConfiguration defaultConfiguration,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets metrics cleanup configuration, or the supplied default when it has not been persisted.
    /// </summary>
    ValueTask<MetricsCleanupConfiguration> GetMetricsCleanupConfigurationAsync(
        MetricsCleanupConfiguration defaultConfiguration,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists the full cleanup configuration.
    /// </summary>
    ValueTask SetCleanupConfigurationAsync(
        ShedduellerCleanupConfiguration configuration,
        CancellationToken cancellationToken = default);
}
