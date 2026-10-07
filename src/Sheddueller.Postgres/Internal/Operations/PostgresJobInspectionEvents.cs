namespace Sheddueller.Postgres.Internal.Operations;

using System.Runtime.CompilerServices;

using Npgsql;

using Sheddueller.Inspection.Jobs;
using Sheddueller.Storage;

internal static class PostgresJobInspectionEvents
{
    public static async IAsyncEnumerable<JobEvent> ReadEventsAsync(
        PostgresOperationContext context,
        Guid jobId,
        JobEventReadOptions? options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        options ??= new JobEventReadOptions();
        ValidateEventReadOptions(options);
        await using var connection = await context.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var events = await ReadEventPageAsync(context, connection, jobId, options.AfterEventSequence, options.Limit, cancellationToken).ConfigureAwait(false);
        foreach (var jobEvent in events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return jobEvent;
        }
    }

    public static async ValueTask<int> CleanupAsync(
        PostgresOperationContext context,
        TimeSpan retention,
        CancellationToken cancellationToken)
    {
        if (retention <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(retention), retention, "Job event retention must be positive.");
        }

        await using var connection = await context.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await PostgresOperationContext.ExecuteCountAsync(
          connection,
          $"""
          delete from {context.Names.JobEvents} event
          using {context.Names.Jobs} job
          where event.job_id = job.job_id
            and job.state in ('Completed', 'Failed', 'Canceled')
            and coalesce(job.completed_at_utc, job.failed_at_utc, job.canceled_at_utc) < transaction_timestamp() - @retention;
          """,
          command => command.Parameters.AddWithValue("retention", retention),
          cancellationToken)
          .ConfigureAwait(false);
    }

    public static async ValueTask<IReadOnlyList<JobEvent>> ReadEventPageAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        Guid jobId,
        long? afterEventSequence,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        var afterClause = afterEventSequence is null ? string.Empty : "and event_sequence > @after_event_sequence";
        command.CommandText =
          $"""
          select
              event_id,
              job_id,
              event_sequence,
              kind,
              occurred_at_utc,
              attempt_number,
              log_level,
              message,
              progress_percent,
              fields
          from {context.Names.JobEvents}
          where job_id = @job_id
            {afterClause}
          order by event_sequence asc
          limit @limit;
          """;
        command.Parameters.AddWithValue("job_id", jobId);
        if (afterEventSequence is not null)
        {
            command.Parameters.AddWithValue("after_event_sequence", afterEventSequence.Value);
        }

        command.Parameters.AddWithValue("limit", limit);

        var events = new List<JobEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            events.Add(PostgresJobEvents.ReadEvent(reader));
        }

        return events;
    }

    public static async ValueTask<IReadOnlyList<Guid>> ReadRetryCloneJobIdsAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        Guid sourceJobId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
          $"""
          select job_id
          from {context.Names.Jobs}
          where retry_clone_source_job_id = @source_job_id
          order by enqueue_sequence asc;
          """;
        command.Parameters.AddWithValue("source_job_id", sourceJobId);

        var jobIds = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            jobIds.Add(reader.GetGuid(0));
        }

        return jobIds;
    }

    public static async ValueTask<IReadOnlyList<Guid>> ReadPrerequisiteJobIdsAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        Guid jobId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
          $"""
          select prerequisite_job_id
          from {context.Names.JobDependencies}
          where job_id = @job_id
          order by prerequisite_job_id;
          """;
        command.Parameters.AddWithValue("job_id", jobId);

        return await ReadJobIdsAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<IReadOnlyList<Guid>> ReadDependentJobIdsAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        Guid jobId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
          $"""
          select job_id
          from {context.Names.JobDependencies}
          where prerequisite_job_id = @job_id
          order by job_id;
          """;
        command.Parameters.AddWithValue("job_id", jobId);

        return await ReadJobIdsAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<IReadOnlyList<Guid>> ReadJobIdsAsync(
        NpgsqlCommand command,
        CancellationToken cancellationToken)
    {
        var jobIds = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            jobIds.Add(reader.GetGuid(0));
        }

        return jobIds;
    }

    public static void ValidateEventReadOptions(JobEventReadOptions options)
    {
        if (options.Limit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.Limit, "Job event read options limit must be positive.");
        }
    }
}
