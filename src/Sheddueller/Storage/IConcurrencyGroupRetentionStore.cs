namespace Sheddueller.Storage;

/// <summary>
/// Optional provider support for removing unused, unconfigured concurrency groups.
/// </summary>
public interface IConcurrencyGroupRetentionStore
{
    /// <summary>
    /// Removes at most one batch of unreferenced groups older than the idle retention, measured on the store clock.
    /// </summary>
    ValueTask<int> CleanupConcurrencyGroupsAsync(TimeSpan idleRetention, int batchSize, CancellationToken cancellationToken = default);
}
