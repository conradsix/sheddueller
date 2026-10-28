namespace Sheddueller.Storage;

/// <summary>
/// Removes worker nodes that have stopped sending heartbeats.
/// </summary>
/// <remarks>
/// Optional: a store that doesn't implement it keeps every node it has seen. Removing a node only removes its
/// heartbeat record; a node that is still running registers again on its next heartbeat.
/// </remarks>
public interface IWorkerNodeRetentionStore
{
    /// <summary>
    /// Removes worker nodes whose last heartbeat is older than <paramref name="retention" />, measured on the store's
    /// clock (the clock that stamps heartbeats).
    /// </summary>
    /// <param name="retention">How long after its last heartbeat a node is kept.</param>
    /// <param name="cancellationToken">A token for canceling the cleanup.</param>
    /// <returns>The number of nodes removed.</returns>
    ValueTask<int> CleanupWorkerNodesAsync(
        TimeSpan retention,
        CancellationToken cancellationToken = default);
}
