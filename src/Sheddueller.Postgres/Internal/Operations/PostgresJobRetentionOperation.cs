namespace Sheddueller.Postgres.Internal.Operations;

using System.Globalization;

using Npgsql;

using NpgsqlTypes;

using Sheddueller.Storage;

internal static class PostgresJobRetentionOperation
{
    /// <summary>
    /// The number of job events one cleanup transaction deletes.
    /// </summary>
    public const int JobEventCleanupBatchSize = 1000;

    public static async ValueTask<JobRetentionCleanupResult> ExecuteAsync(
        PostgresOperationContext context,
        JobRetentionCleanupRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        await using var connection = await context.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var lockAcquired = await TryAcquireCleanupLockAsync(context, connection, transaction, cancellationToken).ConfigureAwait(false);
        if (!lockAcquired)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new JobRetentionCleanupResult(0);
        }

        var deletedCount = await DeleteTerminalJobsAsync(context, connection, transaction, request, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new JobRetentionCleanupResult(deletedCount);
    }

    /// <summary>
    /// Deletes the events of jobs that reached their terminal state more than <paramref name="retention" /> ago, one
    /// batch per transaction, and returns how many it deleted. Stops early, keeping what it has deleted, when another
    /// cleanup holds the retention lock.
    /// </summary>
    public static async ValueTask<int> CleanupEventsAsync(
        PostgresOperationContext context,
        TimeSpan retention,
        int batchSize,
        CancellationToken cancellationToken)
    {
        if (retention <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(retention), retention, "Job event retention must be positive.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);

        await using var connection = await context.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var totalDeleted = 0;
        while (true)
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            if (!await TryAcquireCleanupLockAsync(context, connection, transaction, cancellationToken).ConfigureAwait(false))
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return totalDeleted;
            }

            var deleted = await DeleteEventBatchAsync(context, connection, transaction, retention, batchSize, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            totalDeleted += deleted;
            if (deleted < batchSize)
            {
                return totalDeleted;
            }
        }
    }

    private static ValueTask<bool> TryAcquireCleanupLockAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
      => PostgresAdvisoryLocks.TryAcquireTransactionLockAsync(
        connection,
        transaction,
        PostgresAdvisoryLocks.RetentionLockKey,
        context.Options.SchemaName,
        cancellationToken);

    private static async ValueTask<int> DeleteTerminalJobsAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        JobRetentionCleanupRequest request,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
          $"""
          with candidates as (
              select job_id
              from {context.Names.Jobs} job
              where
                  {PostgresSqlFragments.TerminalBefore("job", "@completed_before_utc", "@failed_before_utc", "@canceled_before_utc")}
                  and not exists (
                      select 1
                      from {context.Names.JobDependencies} dependency
                      join {context.Names.Jobs} dependent on dependent.job_id = dependency.job_id
                      where dependency.prerequisite_job_id = job.job_id
                        and dependent.state not in {PostgresSqlFragments.TerminalStates}
                  )
              order by {PostgresSqlFragments.TerminalAt("job")} asc, enqueue_sequence asc
              limit @batch_size
              for update skip locked
          ),
          deleted_jobs as (
              delete from {context.Names.Jobs} job
              using candidates
              where job.job_id = candidates.job_id
              returning 1
          )
          select count(*) from deleted_jobs;
          """;
        AddNullableTimestampParameter(command, "completed_before_utc", request.CompletedBeforeUtc);
        AddNullableTimestampParameter(command, "failed_before_utc", request.FailedBeforeUtc);
        AddNullableTimestampParameter(command, "canceled_before_utc", request.CanceledBeforeUtc);
        command.Parameters.Add("batch_size", NpgsqlDbType.Integer).Value = request.BatchSize;

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
          ?? throw new InvalidOperationException("PostgreSQL did not return a terminal job cleanup count.");
        return Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    private static async ValueTask<int> DeleteEventBatchAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        TimeSpan retention,
        int batchSize,
        CancellationToken cancellationToken)
    {
        const string CutoffSql = "(transaction_timestamp() - @retention)";

        return await PostgresOperationContext.ExecuteCountAsync(
          connection,
          transaction,
          $"""
          with candidates as (
              select event.job_id, event.event_sequence
              from {context.Names.Jobs} job
              join {context.Names.JobEvents} event on event.job_id = job.job_id
              where {PostgresSqlFragments.TerminalBefore("job", CutoffSql, CutoffSql, CutoffSql)}
              limit @batch_size
          )
          delete from {context.Names.JobEvents} event
          using candidates
          where event.job_id = candidates.job_id
            and event.event_sequence = candidates.event_sequence;
          """,
          command =>
          {
              command.Parameters.AddWithValue("retention", retention);
              command.Parameters.Add("batch_size", NpgsqlDbType.Integer).Value = batchSize;
          },
          cancellationToken)
          .ConfigureAwait(false);
    }

    private static void AddNullableTimestampParameter(
        NpgsqlCommand command,
        string name,
        DateTimeOffset? value)
      => command.Parameters.Add(name, NpgsqlDbType.TimestampTz).Value = value is { } timestamp ? timestamp : DBNull.Value;
}
