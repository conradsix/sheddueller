namespace Sheddueller.Postgres.Internal.Operations;

using System.Globalization;
using System.Text.Json;

using Npgsql;

using NpgsqlTypes;

using Sheddueller.Storage;

internal static class PostgresJobEvents
{
    public static async ValueTask<JobEvent> AppendAsync(
        PostgresOperationContext context,
        AppendJobEventRequest request,
        CancellationToken cancellationToken)
    {
        await using var connection = await context.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var jobEvent = await AppendAndNotifyInTransactionAsync(context, connection, transaction, request, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return jobEvent;
    }

    public static async ValueTask<JobEvent> AppendAndNotifyInTransactionAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AppendJobEventRequest request,
        CancellationToken cancellationToken)
    {
        var jobEvent = await AppendInTransactionAsync(context, connection, transaction, request, cancellationToken).ConfigureAwait(false);
        await NotifyAsync(context, connection, transaction, jobEvent, cancellationToken).ConfigureAwait(false);

        return jobEvent;
    }

    /// <summary>
    /// Reads the identified events with one query. The result is aligned with <paramref name="eventKeys"/>,
    /// holding <see langword="null"/> for an event that no longer exists.
    /// </summary>
    public static async ValueTask<IReadOnlyList<JobEvent?>> ReadEventsAsync(
        PostgresOperationContext context,
        IReadOnlyList<(Guid JobId, long EventSequence)> eventKeys,
        CancellationToken cancellationToken)
    {
        var jobEvents = new JobEvent?[eventKeys.Count];
        if (eventKeys.Count == 0)
        {
            return jobEvents;
        }

        var jobIds = new Guid[eventKeys.Count];
        var eventSequences = new long[eventKeys.Count];
        for (var i = 0; i < eventKeys.Count; i++)
        {
            (jobIds[i], eventSequences[i]) = eventKeys[i];
        }

        await using var connection = await context.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
          $"""
          select
              job_event.event_id,
              job_event.job_id,
              job_event.event_sequence,
              job_event.kind,
              job_event.occurred_at_utc,
              job_event.attempt_number,
              job_event.log_level,
              job_event.message,
              job_event.progress_percent,
              job_event.fields,
              requested.ordinal
          from unnest(
              @job_ids::uuid[],
              @event_sequences::bigint[])
            with ordinality as requested(job_id, event_sequence, ordinal)
          join {context.Names.JobEvents} job_event
            on job_event.job_id = requested.job_id
           and job_event.event_sequence = requested.event_sequence;
          """;
        command.Parameters.AddWithValue("job_ids", jobIds);
        command.Parameters.AddWithValue("event_sequences", eventSequences);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            // with ordinality numbers the requested keys from 1.
            jobEvents[checked((int)reader.GetInt64(10)) - 1] = ReadEvent(reader);
        }

        return jobEvents;
    }

    public static JobEvent ReadEvent(NpgsqlDataReader reader)
      => new(
        reader.GetGuid(0),
        reader.GetGuid(1),
        reader.GetInt64(2),
        PostgresConversion.ToJobEventKind(reader.GetValue(3)),
        PostgresConversion.ToDateTimeOffset(reader.GetValue(4)),
        reader.GetInt32(5),
        PostgresConversion.ToJobLogLevel(reader.GetValue(6)),
        reader.IsDBNull(7) ? null : reader.GetString(7),
        reader.IsDBNull(8) ? null : reader.GetDouble(8),
        reader.IsDBNull(9) ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(9)));

    private static async ValueTask<JobEvent> AppendInTransactionAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AppendJobEventRequest request,
        CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        var eventSequence = await IncrementEventSequenceAsync(context, connection, transaction, request.JobId, cancellationToken).ConfigureAwait(false);
        var eventId = Guid.NewGuid();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
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
          values (
              @job_id,
              @event_sequence,
              @event_id,
              @kind,
              transaction_timestamp(),
              @attempt_number,
              @log_level,
              @message,
              @progress_percent,
              @fields)
          returning occurred_at_utc;
          """;
        command.Parameters.AddWithValue("job_id", request.JobId);
        command.Parameters.AddWithValue("event_sequence", eventSequence);
        command.Parameters.AddWithValue("event_id", eventId);
        command.Parameters.AddWithValue("kind", PostgresConversion.ToText(request.Kind));
        command.Parameters.AddWithValue("attempt_number", request.AttemptNumber);
        command.Parameters.AddWithValue("log_level", PostgresOperationContext.ToDbValue(request.LogLevel is null ? null : PostgresConversion.ToText(request.LogLevel.Value)));
        command.Parameters.AddWithValue("message", PostgresOperationContext.ToDbValue(request.Message));
        command.Parameters.AddWithValue("progress_percent", PostgresOperationContext.ToDbValue(request.ProgressPercent));

        if (request.Fields is null)
        {
            command.Parameters.AddWithValue("fields", DBNull.Value);
        }
        else
        {
            command.Parameters.Add("fields", NpgsqlDbType.Jsonb).Value = JsonSerializer.Serialize(request.Fields);
        }

        var occurredAtUtc = PostgresConversion.ToDateTimeOffset(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
          ?? throw new InvalidOperationException("PostgreSQL did not return a job event timestamp."));

        var jobEvent = new JobEvent(
          eventId,
          request.JobId,
          eventSequence,
          request.Kind,
          occurredAtUtc,
          request.AttemptNumber,
          request.LogLevel,
          request.Message,
          request.ProgressPercent,
          request.Fields);
        await PostgresMetricsRollups.RecordJobEventAsync(context, connection, transaction, jobEvent, cancellationToken)
          .ConfigureAwait(false);

        return jobEvent;
    }

    private static async ValueTask<long> IncrementEventSequenceAsync(
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
          update {context.Names.Jobs}
          set job_event_sequence = job_event_sequence + 1
          where job_id = @job_id
          returning job_event_sequence;
          """;
        command.Parameters.AddWithValue("job_id", jobId);

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
          ?? throw new InvalidOperationException($"Job '{jobId}' does not exist.");
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private static async ValueTask NotifyAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        JobEvent jobEvent,
        CancellationToken cancellationToken)
      => await PostgresOperationContext.ExecuteCountAsync(
        connection,
        transaction,
        "select pg_notify(@channel, @payload);",
        command =>
        {
            command.Parameters.AddWithValue("channel", PostgresNames.JobEventChannel);
            command.Parameters.AddWithValue("payload", $"{context.Options.SchemaName}|{jobEvent.JobId:N}|{jobEvent.EventSequence}");
        },
        cancellationToken)
      .ConfigureAwait(false);

    private static void ValidateRequest(AppendJobEventRequest request)
    {
        if (!Enum.IsDefined(request.Kind))
        {
            throw new ArgumentOutOfRangeException(nameof(request), request.Kind, "Job event kind is not supported.");
        }

        if (request.AttemptNumber < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), request.AttemptNumber, "Job event attempt number cannot be negative.");
        }

        if (request.ProgressPercent is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(request), request.ProgressPercent, "Progress percent must be between 0 and 100.");
        }

        if (request.LogLevel is not null && !Enum.IsDefined(request.LogLevel.Value))
        {
            throw new ArgumentOutOfRangeException(nameof(request), request.LogLevel, "Job log level is not supported.");
        }
    }
}
