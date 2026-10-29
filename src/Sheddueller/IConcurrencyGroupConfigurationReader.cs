namespace Sheddueller;

/// <summary>
/// Reads concurrency limits without querying jobs or creating a group.
/// </summary>
public interface IConcurrencyGroupConfigurationReader
{
    /// <summary>
    /// Gets defaults and overrides; an unconfigured group has an effective limit of one.
    /// </summary>
    ValueTask<ConcurrencyGroupConfiguration> GetConfigurationAsync(string groupKey, CancellationToken cancellationToken = default);
}
