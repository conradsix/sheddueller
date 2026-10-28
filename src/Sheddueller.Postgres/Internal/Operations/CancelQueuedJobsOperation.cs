namespace Sheddueller.Postgres.Internal.Operations;

using Npgsql;

using NpgsqlTypes;

using Sheddueller.Storage;

internal static class CancelQueuedJobsOperation
{
    public static async ValueTask<int> ExecuteAsync(
        PostgresOperationContext context,
        CancelQueuedJobsRequest request,
        CancellationToken cancellationToken)
    {
        await using var connection = await context.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var canceledJobs = await CancelQueuedJobsAsync(context, connection, transaction, request, cancellationToken).ConfigureAwait(false);

        if (canceledJobs.Count > 0)
        {
            await InsertLifecycleEventsAsync(context, connection, transaction, canceledJobs, cancellationToken).ConfigureAwait(false);
            await PostgresMetricsRollups.RecordCanceledJobsAsync(
              context,
              connection,
              transaction,
              [.. canceledJobs.Select(static job => job.JobId)],
              cancellationToken)
              .ConfigureAwait(false);
            await NotifyLifecycleEventsAsync(context, connection, transaction, canceledJobs, cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return canceledJobs.Count;
    }

    private static async ValueTask<IReadOnlyList<CanceledQueuedJob>> CancelQueuedJobsAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancelQueuedJobsRequest request,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
          $"""
          update {context.Names.Jobs}
          set state = 'Canceled',
              canceled_at_utc = @canceled_at_utc,
              job_event_sequence = job_event_sequence + 1
          where state = 'Queued'
          returning job_id, job_event_sequence, attempt_count;
          """;
        command.Parameters.AddWithValue("canceled_at_utc", request.CanceledAtUtc);

        var canceledJobs = new List<CanceledQueuedJob>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            canceledJobs.Add(new CanceledQueuedJob(
              reader.GetGuid(0),
              reader.GetInt64(1),
              Guid.NewGuid(),
              reader.GetInt32(2)));
        }

        return canceledJobs;
    }

    private static async ValueTask InsertLifecycleEventsAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<CanceledQueuedJob> canceledJobs,
        CancellationToken cancellationToken)
      => await PostgresOperationContext.ExecuteCountAsync(
          connection,
          transaction,
          $"""
          insert into {context.Names.JobEvents} (
              job_id,
              event_sequence,
              event_id,
              kind,
              occurred_at_utc,
              attempt_number,
              log_level,
              message,
              progress_percent,
              fields)
          select
              staged.job_id,
              staged.event_sequence,
              staged.event_id,
              'Lifecycle',
              transaction_timestamp(),
              staged.attempt_number,
              null,
              @message,
              null,
              null
          from unnest(
              @job_ids::uuid[],
              @event_sequences::bigint[],
              @event_ids::uuid[],
              @attempt_numbers::integer[])
            as staged(job_id, event_sequence, event_id, attempt_number);
          """,
          command =>
          {
              AddCanceledJobIdentityParameters(command, canceledJobs);
              command.Parameters.Add("event_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid).Value =
                canceledJobs.Select(static job => job.EventId).ToArray();
              command.Parameters.Add("attempt_numbers", NpgsqlDbType.Array | NpgsqlDbType.Integer).Value =
                canceledJobs.Select(static job => job.AttemptNumber).ToArray();
              command.Parameters.AddWithValue("message", PostgresJobEventMessages.Canceled);
          },
          cancellationToken)
          .ConfigureAwait(false);

    private static async ValueTask NotifyLifecycleEventsAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<CanceledQueuedJob> canceledJobs,
        CancellationToken cancellationToken)
      => await PostgresOperationContext.ExecuteCountAsync(
          connection,
          transaction,
          """
          select pg_notify(
              @job_event_channel,
              @schema_name || '|' || replace(staged.job_id::text, '-', '') || '|' || staged.event_sequence::text)
          from unnest(
              @job_ids::uuid[],
              @event_sequences::bigint[])
            as staged(job_id, event_sequence);
          """,
          command =>
          {
              command.Parameters.AddWithValue("job_event_channel", PostgresNames.JobEventChannel);
              command.Parameters.AddWithValue("schema_name", context.Options.SchemaName);
              AddCanceledJobIdentityParameters(command, canceledJobs);
          },
          cancellationToken)
          .ConfigureAwait(false);

    private static void AddCanceledJobIdentityParameters(
        NpgsqlCommand command,
        IReadOnlyList<CanceledQueuedJob> canceledJobs)
    {
        command.Parameters.Add("job_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid).Value =
          canceledJobs.Select(static job => job.JobId).ToArray();
        command.Parameters.Add("event_sequences", NpgsqlDbType.Array | NpgsqlDbType.Bigint).Value =
          canceledJobs.Select(static job => job.EventSequence).ToArray();
    }

    private sealed record CanceledQueuedJob(
        Guid JobId,
        long EventSequence,
        Guid EventId,
        int AttemptNumber);
}
