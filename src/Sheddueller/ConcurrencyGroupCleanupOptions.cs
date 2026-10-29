namespace Sheddueller;

/// <summary>
/// Cleanup policy for unconfigured groups with no job or schedule references.
/// </summary>
public sealed class ConcurrencyGroupCleanupOptions
{
    public bool Enabled { get; set; } = true;

    public TimeSpan IdleRetention { get; set; } = TimeSpan.FromDays(1);

    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromHours(1);

    public int BatchSize { get; set; } = 1000;
}
