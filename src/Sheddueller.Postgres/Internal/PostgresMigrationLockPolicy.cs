namespace Sheddueller.Postgres.Internal;

/// <summary>
/// How long a migration waits for a table lock, and how it retries when it can't get one.
/// </summary>
/// <remarks>
/// While a schema change waits for its table lock, every new query on that table queues behind it, so a single
/// long-running transaction would stall enqueueing, claiming and the dashboard for as long as the migration waits.
/// A short lock timeout caps that stall; the migration then backs off, lets the queued work through and tries again.
/// </remarks>
/// <param name="LockTimeout">The longest any migration statement waits for a lock.</param>
/// <param name="MaxAttempts">How many times the migration runs before giving up.</param>
/// <param name="RetryDelay">The pause after the first failed attempt; each later pause grows by the same amount.</param>
/// <param name="MaxRetryDelay">The longest pause between attempts.</param>
internal sealed record PostgresMigrationLockPolicy(
    TimeSpan LockTimeout,
    int MaxAttempts,
    TimeSpan RetryDelay,
    TimeSpan MaxRetryDelay)
{
    /// <summary>
    /// Up to 3 s of queueing per attempt, retrying for about two minutes in all.
    /// </summary>
    public static PostgresMigrationLockPolicy Default { get; } = new(
      TimeSpan.FromSeconds(3),
      MaxAttempts: 10,
      TimeSpan.FromSeconds(2),
      TimeSpan.FromSeconds(15));

    public TimeSpan RetryDelayAfter(int attempt)
      => TimeSpan.FromTicks(Math.Min(this.RetryDelay.Ticks * attempt, this.MaxRetryDelay.Ticks));
}
