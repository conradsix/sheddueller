namespace Sheddueller.Postgres.Internal.Operations;

using NpgsqlTypes;

internal static class PostgresConcurrencyGroupRetentionOperation
{
    public static async ValueTask<int> ExecuteAsync(PostgresOperationContext context, TimeSpan idleRetention, int batchSize, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(idleRetention, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        await using var connection = await context.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        if (!await PostgresAdvisoryLocks.TryAcquireTransactionLockAsync(connection, transaction, PostgresAdvisoryLocks.RetentionLockKey, context.Options.SchemaName, ct).ConfigureAwait(false))
        {
            return 0;
        }

        var deleted = await PostgresOperationContext.ExecuteCountAsync(connection, transaction,
          $"""
          with candidates as (
              select cg.group_key
              from {context.Names.ConcurrencyGroups} cg
              where cg.in_use_count = 0
                and cg.updated_at_utc < transaction_timestamp() - @idle_retention
                and cg.default_limit is null and cg.configured_limit is null
                and not cg.rate_limit_override_enabled
                and cg.default_rate_permit_count is null
                and cg.configured_rate_permit_count is null
                and (cg.rate_theoretical_arrival_at_utc is null or cg.rate_theoretical_arrival_at_utc <= transaction_timestamp())
                and not exists (select 1 from {context.Names.JobConcurrencyGroups} jg where jg.group_key = cg.group_key)
                and not exists (select 1 from {context.Names.ScheduleConcurrencyGroups} sg where sg.group_key = cg.group_key)
              order by cg.group_key
              limit @batch_size
              for update of cg skip locked
          )
          delete from {context.Names.ConcurrencyGroups} cg using candidates c where cg.group_key = c.group_key;
          """,
          command =>
          {
              command.Parameters.AddWithValue("idle_retention", idleRetention);
              command.Parameters.Add("batch_size", NpgsqlDbType.Integer).Value = batchSize;
          }, ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return deleted;
    }
}
