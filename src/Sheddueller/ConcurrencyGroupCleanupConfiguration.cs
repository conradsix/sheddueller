namespace Sheddueller;

/// <summary>
/// Cluster-wide cleanup configuration for unused concurrency groups.
/// </summary>
public sealed record ConcurrencyGroupCleanupConfiguration(bool Enabled, TimeSpan IdleRetention, TimeSpan CleanupInterval, int BatchSize)
{
    public static ConcurrencyGroupCleanupConfiguration Default { get; } = FromOptions(new ConcurrencyGroupCleanupOptions());

    public static ConcurrencyGroupCleanupConfiguration FromOptions(ConcurrencyGroupCleanupOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new(options.Enabled, options.IdleRetention, options.CleanupInterval, options.BatchSize);
    }

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(this.IdleRetention, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(this.CleanupInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(this.BatchSize);
    }
}
