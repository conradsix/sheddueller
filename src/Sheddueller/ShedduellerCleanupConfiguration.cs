namespace Sheddueller;

/// <summary>
/// Cluster-wide cleanup configuration for Sheddueller stores.
/// </summary>
public sealed record ShedduellerCleanupConfiguration(
    JobRetentionCleanupConfiguration JobRetention,
    JobEventCleanupConfiguration JobEvents,
    MetricsCleanupConfiguration Metrics)
{
    public ConcurrencyGroupCleanupConfiguration ConcurrencyGroups { get; init; } = ConcurrencyGroupCleanupConfiguration.Default;
}
