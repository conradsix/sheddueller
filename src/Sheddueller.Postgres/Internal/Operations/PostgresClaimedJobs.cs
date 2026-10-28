namespace Sheddueller.Postgres.Internal.Operations;

using Npgsql;

using Sheddueller.Storage;

internal static class PostgresClaimedJobs
{
    public static async ValueTask<IReadOnlyList<string>?> TryLockCurrentClaimGroupsAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid jobId,
        string nodeId,
        Guid leaseToken,
        CancellationToken cancellationToken)
    {
        var job = await TryReadCurrentClaimForFailureAsync(context, connection, transaction, jobId, nodeId, leaseToken, cancellationToken)
          .ConfigureAwait(false);
        return job?.GroupKeys;
    }

    public static async ValueTask<PostgresClaimedJob?> TryReadCurrentClaimForFailureAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid jobId,
        string nodeId,
        Guid leaseToken,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
          $"""
          select
              {PostgresReaders.PostgresClaimedJobColumnsSql(context.Names)}
          from {context.Names.Jobs} job
          where job.job_id = @job_id
            and job.state = 'Claimed'
            and job.claimed_by_node_id = @node_id
            and job.lease_token = @lease_token
            and job.lease_expires_at_utc > transaction_timestamp()
          for update;
          """;
        command.Parameters.AddWithValue("job_id", jobId);
        command.Parameters.AddWithValue("node_id", nodeId);
        command.Parameters.AddWithValue("lease_token", leaseToken);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
          ? PostgresReaders.ReadPostgresClaimedJob(reader)
          : null;
    }

    public static async ValueTask<IReadOnlyList<PostgresClaimedJob>> ReadExpiredClaimsAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
          $"""
          select
              {PostgresReaders.PostgresClaimedJobColumnsSql(context.Names)}
          from {context.Names.Jobs} job
          where job.state = 'Claimed'
            and job.lease_expires_at_utc <= transaction_timestamp()
          order by job.lease_expires_at_utc asc, job.enqueue_sequence asc
          for update skip locked;
          """;

        var jobs = new List<PostgresClaimedJob>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            jobs.Add(PostgresReaders.ReadPostgresClaimedJob(reader));
        }

        return jobs;
    }

    public static async ValueTask<string> ApplyFailedAttemptAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PostgresClaimedJob job,
        DateTimeOffset failedAtUtc,
        JobFailureInfo failure,
        CancellationToken cancellationToken)
    {
        var retriesRemain = job.AttemptCount < job.MaxAttempts;
        if (retriesRemain
            && await TryMarkSupersededByQueuedDuplicateAsync(context, connection, transaction, job, failedAtUtc, failure, cancellationToken)
                .ConfigureAwait(false) is { } supersededMessage)
        {
            return supersededMessage;
        }

        var notBeforeExpression = retriesRemain ? "transaction_timestamp() + @retry_delay" : "null";
        await PostgresOperationContext.ExecuteCountAsync(
          connection,
          transaction,
          $"""
          update {context.Names.Jobs}
          set state = @state,
              failed_at_utc = @failed_at_utc,
              failure_type_name = @failure_type_name,
              failure_message = @failure_message,
              failure_stack_trace = @failure_stack_trace,
              not_before_utc = {notBeforeExpression},
              claimed_by_node_id = null,
              claimed_at_utc = null,
              lease_token = null,
              lease_expires_at_utc = null,
              last_heartbeat_at_utc = null
          where job_id = @job_id;
          """,
          command =>
          {
              command.Parameters.AddWithValue("job_id", job.JobId);
              command.Parameters.AddWithValue("state", retriesRemain ? "Queued" : "Failed");
              command.Parameters.AddWithValue("failed_at_utc", failedAtUtc);
              command.Parameters.AddWithValue("failure_type_name", failure.ExceptionType);
              command.Parameters.AddWithValue("failure_message", failure.Message);
              command.Parameters.AddWithValue("failure_stack_trace", PostgresOperationContext.ToDbValue(failure.StackTrace));

              if (retriesRemain)
              {
                  command.Parameters.AddWithValue("retry_delay", PostgresRetryPolicies.CalculateBackoff(job));
              }
          },
          cancellationToken)
          .ConfigureAwait(false);

        return retriesRemain ? PostgresJobEventMessages.RetryScheduled : PostgresJobEventMessages.Failed;
    }

    public static async ValueTask<string?> TryMarkSupersededByQueuedDuplicateAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PostgresClaimedJob job,
        DateTimeOffset failedAtUtc,
        JobFailureInfo failure,
        CancellationToken cancellationToken)
    {
        if (job.IdempotencyKey is null)
        {
            return null;
        }

        await LockIdempotencyKeyAsync(context, connection, transaction, job.IdempotencyKey, cancellationToken).ConfigureAwait(false);
        var duplicateJobId = await ReadQueuedDuplicateJobIdAsync(context, connection, transaction, job, cancellationToken).ConfigureAwait(false);
        if (duplicateJobId is null)
        {
            return null;
        }

        await PostgresOperationContext.ExecuteCountAsync(
          connection,
          transaction,
          $"""
          update {context.Names.Jobs}
          set state = 'Failed',
              failed_at_utc = @failed_at_utc,
              failure_type_name = @failure_type_name,
              failure_message = @failure_message,
              failure_stack_trace = @failure_stack_trace,
              not_before_utc = null,
              claimed_by_node_id = null,
              claimed_at_utc = null,
              lease_token = null,
              lease_expires_at_utc = null,
              last_heartbeat_at_utc = null
          where job_id = @job_id;
          """,
          command =>
          {
              command.Parameters.AddWithValue("job_id", job.JobId);
              command.Parameters.AddWithValue("failed_at_utc", failedAtUtc);
              command.Parameters.AddWithValue("failure_type_name", failure.ExceptionType);
              command.Parameters.AddWithValue("failure_message", failure.Message);
              command.Parameters.AddWithValue("failure_stack_trace", PostgresOperationContext.ToDbValue(failure.StackTrace));
          },
          cancellationToken)
          .ConfigureAwait(false);

        return PostgresJobEventMessages.SupersededFailure(duplicateJobId.Value);
    }

    private static async ValueTask LockIdempotencyKeyAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string idempotencyKey,
        CancellationToken cancellationToken)
      => await PostgresOperationContext.ExecuteCountAsync(
          connection,
          transaction,
          "select pg_advisory_xact_lock(hashtextextended(@schema_name || '|' || @idempotency_key, @idempotency_key_hash_seed));",
          command =>
          {
              command.Parameters.AddWithValue("schema_name", context.Options.SchemaName);
              command.Parameters.AddWithValue("idempotency_key", idempotencyKey);
              command.Parameters.AddWithValue("idempotency_key_hash_seed", PostgresAdvisoryLocks.IdempotencyKeyHashSeed);
          },
          cancellationToken)
          .ConfigureAwait(false);

    private static async ValueTask<Guid?> ReadQueuedDuplicateJobIdAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PostgresClaimedJob job,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
          $"""
          select job_id
          from {context.Names.Jobs}
          where idempotency_key = @idempotency_key
            and state = 'Queued'
            and job_id <> @job_id
          order by enqueue_sequence asc
          limit 1;
          """;
        command.Parameters.AddWithValue("idempotency_key", job.IdempotencyKey!);
        command.Parameters.AddWithValue("job_id", job.JobId);

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is Guid duplicateJobId
          ? duplicateJobId
          : null;
    }
}
