namespace Sheddueller.Postgres.Internal;

using Npgsql;

/// <summary>
/// The advisory locks Sheddueller takes, and the keys they use.
/// </summary>
/// <remarks>
/// Every node working against a schema must agree on these values, including nodes running older or newer
/// Sheddueller versions during a rolling upgrade, so they must never change. The keyed locks take two
/// <see langword="int" /> keys, a lock key and <c>hashtext(schema_name)</c>, so each schema gets its own lock. The
/// idempotency locks take one <see langword="bigint" /> key hashed from the schema and idempotency key; PostgreSQL
/// keeps one-key and two-key advisory locks apart, so they never conflict with the keyed locks.
/// </remarks>
internal static class PostgresAdvisoryLocks
{
    /// <summary>
    /// Held by a migration for its whole transaction, so migrations of one schema run one at a time.
    /// </summary>
    public const int MigrationLockKey = 7870833;

    /// <summary>
    /// Held by each job retention and job event cleanup transaction. Both delete job events, the first through the
    /// cascade from jobs, so they share it: one cleanup at a time per schema, and a cleanup that finds it held skips
    /// its turn.
    /// </summary>
    public const int RetentionLockKey = 7870834;

    /// <summary>
    /// Held by a metrics rollup cleanup transaction, so one node at a time prunes a schema's rollups.
    /// </summary>
    public const int MetricsCleanupLockKey = 7870835;

    /// <summary>
    /// The seed of <c>hashtextextended(schema_name || '|' || idempotency_key, seed)</c>, the one-key lock that
    /// serializes enqueues and retries of the same idempotency key. It shares its value with
    /// <see cref="RetentionLockKey" />, which is harmless: one is a hash seed in the one-key lock space, the other a
    /// key in the two-key space.
    /// </summary>
    public const long IdempotencyKeyHashSeed = 7870834;

    /// <summary>
    /// Takes the schema's lock for <paramref name="lockKey" /> until <paramref name="transaction" /> ends, without
    /// waiting. Returns whether it was taken; false means another transaction holds it.
    /// </summary>
    public static async ValueTask<bool> TryAcquireTransactionLockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int lockKey,
        string schemaName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "select pg_try_advisory_xact_lock(@lock_key, hashtext(@schema_name));";
        command.Parameters.AddWithValue("lock_key", lockKey);
        command.Parameters.AddWithValue("schema_name", schemaName);

        return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
          ?? throw new InvalidOperationException("PostgreSQL did not return an advisory lock result."));
    }
}
