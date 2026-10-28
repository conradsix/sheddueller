namespace Sheddueller.Postgres.Internal.Operations;

using System.Globalization;

using Sheddueller.Storage;

internal static class PostgresWorkerOperations
{
    public static async ValueTask<DateTimeOffset?> GetCancellationRequestedAtAsync(
        PostgresOperationContext context,
        JobCancellationStatusRequest request,
        CancellationToken cancellationToken)
    {
        await using var connection = await context.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
          $"""
          select cancellation_requested_at_utc
          from {context.Names.Jobs}
          where job_id = @job_id
            and state = 'Claimed'
            and claimed_by_node_id = @node_id
            and lease_token = @lease_token
            and lease_expires_at_utc > transaction_timestamp();
          """;
        command.Parameters.AddWithValue("job_id", request.JobId);
        command.Parameters.AddWithValue("node_id", request.NodeId);
        command.Parameters.AddWithValue("lease_token", request.LeaseToken);

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is null or DBNull ? null : PostgresConversion.ToDateTimeOffset(result);
    }

    public static async ValueTask<bool> MarkCancellationObservedAsync(
        PostgresOperationContext context,
        ObserveJobCancellationRequest request,
        CancellationToken cancellationToken)
    {
        await using var connection = await context.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var job = await PostgresClaimedJobs.TryReadCurrentClaimForFailureAsync(
          context,
          connection,
          transaction,
          request.JobId,
          request.NodeId,
          request.LeaseToken,
          cancellationToken)
          .ConfigureAwait(false);
        if (job is null || !await HasCancellationRequestAsync(context, connection, transaction, request.JobId, cancellationToken).ConfigureAwait(false))
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        // The claim read locked the job row for this transaction, so the update below targets that row directly and
        // always applies: the group decrement can never be committed without the matching state change.
        await PostgresJobGroups.DecrementGroupsAsync(context, connection, transaction, job.GroupKeys, cancellationToken).ConfigureAwait(false);
        await PostgresOperationContext.ExecuteCountAsync(
          connection,
          transaction,
          $"""
          update {context.Names.Jobs}
          set state = 'Canceled',
              canceled_at_utc = @observed_at_utc,
              cancellation_observed_at_utc = @observed_at_utc,
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
              command.Parameters.AddWithValue("observed_at_utc", request.ObservedAtUtc);
          },
          cancellationToken)
          .ConfigureAwait(false);
        await PostgresJobEvents.AppendAndNotifyInTransactionAsync(
          context,
          connection,
          transaction,
          new AppendJobEventRequest(job.JobId, JobEventKind.CancelObserved, job.AttemptCount, Message: "Cancellation observed"),
          cancellationToken)
          .ConfigureAwait(false);
        await PostgresJobEvents.AppendAndNotifyInTransactionAsync(
          context,
          connection,
          transaction,
          new AppendJobEventRequest(job.JobId, JobEventKind.Lifecycle, job.AttemptCount, Message: "Canceled"),
          cancellationToken)
          .ConfigureAwait(false);
        await context.NotifyAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return true;
    }

    public static async ValueTask RecordWorkerNodeHeartbeatAsync(
        PostgresOperationContext context,
        WorkerNodeHeartbeatRequest request,
        CancellationToken cancellationToken)
    {
        if (request.MaxConcurrentExecutionsPerNode <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), request.MaxConcurrentExecutionsPerNode, "Node max concurrency must be positive.");
        }

        if (request.CurrentExecutionCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), request.CurrentExecutionCount, "Node current execution count cannot be negative.");
        }

        await using var connection = await context.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await PostgresOperationContext.ExecuteCountAsync(
          connection,
          $"""
          insert into {context.Names.WorkerNodes} (
              node_id,
              first_seen_at_utc,
              last_heartbeat_at_utc,
              max_concurrent_executions_per_node,
              current_execution_count)
          values (
              @node_id,
              transaction_timestamp(),
              transaction_timestamp(),
              @max_concurrent_executions_per_node,
              @current_execution_count)
          on conflict (node_id) do update
          set last_heartbeat_at_utc = excluded.last_heartbeat_at_utc,
              max_concurrent_executions_per_node = excluded.max_concurrent_executions_per_node,
              current_execution_count = excluded.current_execution_count;
          """,
          command =>
          {
              command.Parameters.AddWithValue("node_id", request.NodeId);
              command.Parameters.AddWithValue("max_concurrent_executions_per_node", request.MaxConcurrentExecutionsPerNode);
              command.Parameters.AddWithValue("current_execution_count", request.CurrentExecutionCount);
          },
          cancellationToken)
          .ConfigureAwait(false);
    }

    private static async ValueTask<bool> HasCancellationRequestAsync(
        PostgresOperationContext context,
        Npgsql.NpgsqlConnection connection,
        Npgsql.NpgsqlTransaction transaction,
        Guid jobId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
          $"""
          select cancellation_requested_at_utc is not null
          from {context.Names.Jobs}
          where job_id = @job_id;
          """;
        command.Parameters.AddWithValue("job_id", jobId);

        return Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }
}
