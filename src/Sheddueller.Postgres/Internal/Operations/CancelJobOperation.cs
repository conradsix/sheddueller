namespace Sheddueller.Postgres.Internal.Operations;

using Npgsql;

using Sheddueller.Storage;

internal static class CancelJobOperation
{
    public static async ValueTask<JobCancellationResult> ExecuteAsync(
        PostgresOperationContext context,
        CancelJobRequest request,
        CancellationToken cancellationToken)
    {
        await using var connection = await context.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var row = await ReadForUpdateAsync(context, connection, transaction, request.JobId, cancellationToken).ConfigureAwait(false);

        if (row is null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return JobCancellationResult.NotFound;
        }

        var result = row.State switch
        {
            JobState.Queued => await CancelQueuedJobAsync(context, connection, transaction, row, request, cancellationToken).ConfigureAwait(false),
            JobState.Claimed => await RequestClaimedJobCancellationAsync(context, connection, transaction, row, request, cancellationToken).ConfigureAwait(false),
            JobState.Completed or JobState.Failed or JobState.Canceled => JobCancellationResult.AlreadyFinished,
            _ => throw new InvalidOperationException($"Unsupported job state '{row.State}'."),
        };

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    private static async ValueTask<JobCancellationResult> CancelQueuedJobAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancelJobRow row,
        CancelJobRequest request,
        CancellationToken cancellationToken)
    {
        await PostgresOperationContext.ExecuteCountAsync(
          connection,
          transaction,
          $"""
          update {context.Names.Jobs}
          set state = 'Canceled',
              canceled_at_utc = @canceled_at_utc
          where job_id = @job_id;
          """,
          command =>
          {
              command.Parameters.AddWithValue("job_id", row.JobId);
              command.Parameters.AddWithValue("canceled_at_utc", request.CanceledAtUtc);
          },
          cancellationToken)
          .ConfigureAwait(false);

        await PostgresJobEvents.AppendAndNotifyInTransactionAsync(
          context,
          connection,
          transaction,
          new AppendJobEventRequest(row.JobId, JobEventKind.Lifecycle, row.AttemptCount, Message: PostgresJobEventMessages.Canceled),
          cancellationToken)
          .ConfigureAwait(false);

        return JobCancellationResult.Canceled;
    }

    private static async ValueTask<JobCancellationResult> RequestClaimedJobCancellationAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancelJobRow row,
        CancelJobRequest request,
        CancellationToken cancellationToken)
    {
        if (row.CancellationRequestedAtUtc is not null)
        {
            return JobCancellationResult.CancellationRequested;
        }

        await PostgresOperationContext.ExecuteCountAsync(
          connection,
          transaction,
          $"""
          update {context.Names.Jobs}
          set cancellation_requested_at_utc = @cancellation_requested_at_utc
          where job_id = @job_id;
          """,
          command =>
          {
              command.Parameters.AddWithValue("job_id", row.JobId);
              command.Parameters.AddWithValue("cancellation_requested_at_utc", request.CanceledAtUtc);
          },
          cancellationToken)
          .ConfigureAwait(false);

        await PostgresJobEvents.AppendAndNotifyInTransactionAsync(
          context,
          connection,
          transaction,
          new AppendJobEventRequest(row.JobId, JobEventKind.CancelRequested, row.AttemptCount, Message: "Cancellation requested"),
          cancellationToken)
          .ConfigureAwait(false);

        return JobCancellationResult.CancellationRequested;
    }

    private static async ValueTask<CancelJobRow?> ReadForUpdateAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid jobId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
          $"""
          select job_id,
                 state,
                 attempt_count,
                 cancellation_requested_at_utc
          from {context.Names.Jobs}
          where job_id = @job_id
          for update;
          """;
        command.Parameters.AddWithValue("job_id", jobId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new CancelJobRow(
          reader.GetGuid(0),
          PostgresConversion.ToJobState(reader.GetValue(1)),
          reader.GetInt32(2),
          reader.IsDBNull(3) ? null : PostgresConversion.ToDateTimeOffset(reader.GetValue(3)));
    }

    private sealed record CancelJobRow(
        Guid JobId,
        JobState State,
        int AttemptCount,
        DateTimeOffset? CancellationRequestedAtUtc);
}
