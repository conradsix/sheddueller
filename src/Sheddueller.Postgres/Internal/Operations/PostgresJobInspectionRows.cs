namespace Sheddueller.Postgres.Internal.Operations;

using System.Globalization;

using Npgsql;

using Sheddueller.Inspection.Jobs;
using Sheddueller.Storage;

internal static class PostgresJobInspectionRows
{
    public static async ValueTask<IReadOnlyList<JobInspectionSummary>> CreateSummariesAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        IReadOnlyList<PostgresJobInspectionRow> rows,
        CancellationToken cancellationToken)
      => await CreateSummariesAsync(context, connection, rows, nowUtc: null, cancellationToken).ConfigureAwait(false);

    public static async ValueTask<IReadOnlyList<JobInspectionSummary>> CreateSummariesAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        IReadOnlyList<PostgresJobInspectionRow> rows,
        DateTimeOffset? nowUtc,
        CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        var jobIds = rows.Select(static row => row.JobId).Distinct().ToArray();
        var tagsByJobId = await ReadTagsByJobIdAsync(context, connection, jobIds, cancellationToken).ConfigureAwait(false);
        var groupsByJobId = await ReadGroupsByJobIdAsync(context, connection, jobIds, cancellationToken).ConfigureAwait(false);
        var latestProgressByJobId = await ReadLatestProgressByJobIdAsync(context, connection, jobIds, cancellationToken).ConfigureAwait(false);
        var queuePositionsByJobId = await PostgresJobQueuePositions.ReadQueuePositionsAsync(context, connection, rows, nowUtc, cancellationToken).ConfigureAwait(false);

        return CreateSummaries(rows, tagsByJobId, groupsByJobId, latestProgressByJobId, queuePositionsByJobId);
    }

    public static async ValueTask<IReadOnlyDictionary<Guid, JobInspectionSummary>> CreateSummaryMapAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        IReadOnlyList<PostgresJobInspectionRow> rows,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var summaries = await CreateSummariesAsync(context, connection, rows, nowUtc, cancellationToken).ConfigureAwait(false);
        var byJobId = new Dictionary<Guid, JobInspectionSummary>(summaries.Count);
        foreach (var summary in summaries)
        {
            byJobId[summary.JobId] = summary;
        }

        return byJobId;
    }

    public static List<JobInspectionSummary> SelectSummaries(
        IReadOnlyList<PostgresJobInspectionRow> rows,
        IReadOnlyDictionary<Guid, JobInspectionSummary> summaries)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        var selected = new List<JobInspectionSummary>(rows.Count);
        foreach (var row in rows)
        {
            selected.Add(summaries[row.JobId]);
        }

        return selected;
    }

    public static async ValueTask<IReadOnlyDictionary<JobState, int>> ReadStateCountsAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
          $"""
          select state, count(*)
          from {context.Names.Jobs}
          group by state;
          """;

        var counts = Enum.GetValues<JobState>().ToDictionary(state => state, _ => 0);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            counts[PostgresConversion.ToJobState(reader.GetValue(0))] = Convert.ToInt32(reader.GetInt64(1), CultureInfo.InvariantCulture);
        }

        return counts;
    }

    public static async ValueTask<IReadOnlyList<PostgresJobInspectionRow>> ReadSummaryRowsAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        string clause,
        Action<NpgsqlCommand> configure,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
          $"""
          {JobSelectSql(context)}
          {clause};
          """;
        configure(command);

        return await ReadRowsAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<JobInspectionSummary> CreateSummaryAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        PostgresJobInspectionRow row,
        CancellationToken cancellationToken)
      => new(
        row.JobId,
        row.State,
        row.ServiceType,
        row.MethodName,
        row.Priority,
        row.EnqueueSequence,
        row.EnqueuedAtUtc,
        row.NotBeforeUtc,
        row.AttemptCount,
        row.MaxAttempts,
        await PostgresJobTags.ReadJobTagsAsync(context, connection, row.JobId, cancellationToken).ConfigureAwait(false),
        await PostgresJobGroups.ReadJobGroupKeysAsync(context, connection, transaction: null, row.JobId, cancellationToken).ConfigureAwait(false),
        row.SourceScheduleKey,
        await ReadLatestProgressAsync(context, connection, row.JobId, cancellationToken).ConfigureAwait(false),
        await PostgresJobQueuePositions.GetQueuePositionAsync(context, row.JobId, cancellationToken).ConfigureAwait(false),
        row.ClaimedAtUtc,
        row.CompletedAtUtc,
        row.FailedAtUtc,
        row.CanceledAtUtc)
      {
          RetryCloneSourceJobId = row.RetryCloneSourceJobId,
          CancellationRequestedAtUtc = row.CancellationRequestedAtUtc,
          CancellationObservedAtUtc = row.CancellationObservedAtUtc,
          ScheduleOccurrenceKind = row.ScheduleOccurrenceKind,
      };

    public static List<JobInspectionSummary> CreateSummaries(
        IReadOnlyList<PostgresJobInspectionRow> rows,
        IReadOnlyDictionary<Guid, IReadOnlyList<JobTag>> tagsByJobId,
        IReadOnlyDictionary<Guid, IReadOnlyList<string>> groupsByJobId,
        IReadOnlyDictionary<Guid, JobProgressSnapshot> latestProgressByJobId,
        IReadOnlyDictionary<Guid, JobQueuePosition> queuePositionsByJobId)
    {
        var jobs = new List<JobInspectionSummary>(rows.Count);
        foreach (var row in rows)
        {
            jobs.Add(new JobInspectionSummary(
              row.JobId,
              row.State,
              row.ServiceType,
              row.MethodName,
              row.Priority,
              row.EnqueueSequence,
              row.EnqueuedAtUtc,
              row.NotBeforeUtc,
              row.AttemptCount,
              row.MaxAttempts,
              tagsByJobId.TryGetValue(row.JobId, out var tags) ? tags : [],
              groupsByJobId.TryGetValue(row.JobId, out var groups) ? groups : [],
              row.SourceScheduleKey,
              latestProgressByJobId.GetValueOrDefault(row.JobId),
              queuePositionsByJobId[row.JobId],
              row.ClaimedAtUtc,
              row.CompletedAtUtc,
              row.FailedAtUtc,
              row.CanceledAtUtc)
            {
                RetryCloneSourceJobId = row.RetryCloneSourceJobId,
                CancellationRequestedAtUtc = row.CancellationRequestedAtUtc,
                CancellationObservedAtUtc = row.CancellationObservedAtUtc,
                ScheduleOccurrenceKind = row.ScheduleOccurrenceKind,
            });
        }

        return jobs;
    }

    public static async ValueTask<IReadOnlyDictionary<Guid, IReadOnlyList<JobTag>>> ReadTagsByJobIdAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        Guid[] jobIds,
        CancellationToken cancellationToken)
    {
        if (jobIds.Length == 0)
        {
            return new Dictionary<Guid, IReadOnlyList<JobTag>>();
        }

        await using var command = connection.CreateCommand();
        command.CommandText =
          $"""
          select job_id, name, value
          from {context.Names.JobTags}
          where job_id = any(@job_ids)
          order by job_id asc, ordinal asc, name asc, value asc;
          """;
        command.Parameters.AddWithValue("job_ids", jobIds);

        var tags = new Dictionary<Guid, List<JobTag>>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var jobId = reader.GetGuid(0);
            if (!tags.TryGetValue(jobId, out var jobTags))
            {
                jobTags = [];
                tags.Add(jobId, jobTags);
            }

            jobTags.Add(new JobTag(reader.GetString(1), reader.GetString(2)));
        }

        var result = new Dictionary<Guid, IReadOnlyList<JobTag>>(tags.Count);
        foreach (var pair in tags)
        {
            result.Add(pair.Key, pair.Value);
        }

        return result;
    }

    public static async ValueTask<IReadOnlyDictionary<Guid, IReadOnlyList<string>>> ReadGroupsByJobIdAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        Guid[] jobIds,
        CancellationToken cancellationToken)
    {
        if (jobIds.Length == 0)
        {
            return new Dictionary<Guid, IReadOnlyList<string>>();
        }

        await using var command = connection.CreateCommand();
        command.CommandText =
          $"""
          select job_id, group_key
          from {context.Names.JobConcurrencyGroups}
          where job_id = any(@job_ids)
          order by job_id asc, group_key asc;
          """;
        command.Parameters.AddWithValue("job_ids", jobIds);

        var groups = new Dictionary<Guid, List<string>>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var jobId = reader.GetGuid(0);
            if (!groups.TryGetValue(jobId, out var jobGroups))
            {
                jobGroups = [];
                groups.Add(jobId, jobGroups);
            }

            jobGroups.Add(reader.GetString(1));
        }

        var result = new Dictionary<Guid, IReadOnlyList<string>>(groups.Count);
        foreach (var pair in groups)
        {
            result.Add(pair.Key, pair.Value);
        }

        return result;
    }

    public static async ValueTask<IReadOnlyDictionary<Guid, JobProgressSnapshot>> ReadLatestProgressByJobIdAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        Guid[] jobIds,
        CancellationToken cancellationToken)
    {
        if (jobIds.Length == 0)
        {
            return new Dictionary<Guid, JobProgressSnapshot>();
        }

        await using var command = connection.CreateCommand();
        command.CommandText =
          $"""
          select distinct on (job_id)
              job_id,
              progress_percent,
              message,
              occurred_at_utc
          from {context.Names.JobEvents}
          where job_id = any(@job_ids)
            and kind = 'Progress'
          order by job_id asc, event_sequence desc;
          """;
        command.Parameters.AddWithValue("job_ids", jobIds);

        var progress = new Dictionary<Guid, JobProgressSnapshot>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            progress.Add(
              reader.GetGuid(0),
              new JobProgressSnapshot(
                reader.IsDBNull(1) ? null : reader.GetDouble(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                PostgresConversion.ToDateTimeOffset(reader.GetValue(3))));
        }

        return progress;
    }

    public static async ValueTask<JobProgressSnapshot?> ReadLatestProgressAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        Guid jobId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
          $"""
          select progress_percent, message, occurred_at_utc
          from {context.Names.JobEvents}
          where job_id = @job_id
            and kind = 'Progress'
          order by event_sequence desc
          limit 1;
          """;
        command.Parameters.AddWithValue("job_id", jobId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new JobProgressSnapshot(
          reader.IsDBNull(0) ? null : reader.GetDouble(0),
          reader.IsDBNull(1) ? null : reader.GetString(1),
          PostgresConversion.ToDateTimeOffset(reader.GetValue(2)));
    }

    public static async ValueTask<PostgresJobInspectionRow?> ReadJobRowAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        Guid jobId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
          $"""
          {JobSelectSql(context)}
          where job.job_id = @job_id;
          """;
        command.Parameters.AddWithValue("job_id", jobId);

        var rows = await ReadRowsAsync(command, cancellationToken).ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }

    public static async ValueTask<IReadOnlyList<PostgresJobInspectionRow>> ReadRowsAsync(
        NpgsqlCommand command,
        CancellationToken cancellationToken)
    {
        var rows = new List<PostgresJobInspectionRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new PostgresJobInspectionRow(
              reader.GetGuid(0),
              PostgresConversion.ToJobState(reader.GetValue(1)),
              reader.GetString(2),
              reader.GetString(3),
              reader.GetInt32(4),
              reader.GetInt64(5),
              PostgresConversion.ToDateTimeOffset(reader.GetValue(6)),
              reader.IsDBNull(7) ? null : PostgresConversion.ToDateTimeOffset(reader.GetValue(7)),
              reader.GetInt32(8),
              reader.GetInt32(9),
              reader.IsDBNull(10) ? null : reader.GetString(10),
              reader.IsDBNull(11) ? null : PostgresConversion.ToDateTimeOffset(reader.GetValue(11)),
              reader.IsDBNull(12) ? null : PostgresConversion.ToDateTimeOffset(reader.GetValue(12)),
              reader.IsDBNull(13) ? null : PostgresConversion.ToDateTimeOffset(reader.GetValue(13)),
              reader.IsDBNull(14) ? null : PostgresConversion.ToDateTimeOffset(reader.GetValue(14)),
              reader.IsDBNull(15) ? null : reader.GetString(15),
              reader.IsDBNull(16) ? null : PostgresConversion.ToDateTimeOffset(reader.GetValue(16)),
              reader.IsDBNull(17) ? null : PostgresConversion.ToDateTimeOffset(reader.GetValue(17)),
              reader.IsDBNull(18) ? null : reader.GetGuid(18),
              reader.IsDBNull(19) ? null : PostgresConversion.ToDateTimeOffset(reader.GetValue(19)),
              reader.IsDBNull(20) ? null : PostgresConversion.ToDateTimeOffset(reader.GetValue(20)),
              PostgresConversion.ToScheduleOccurrenceKind(reader.GetValue(21))));
        }

        return rows;
    }

    public static async ValueTask<DateTimeOffset> ReadCurrentTimestampAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "select transaction_timestamp();";
        return PostgresConversion.ToDateTimeOffset(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
          ?? throw new InvalidOperationException("PostgreSQL did not return transaction_timestamp()."));
    }

    public static string JobSelectSql(PostgresOperationContext context)
      => $"""
         select
             job.job_id,
             job.state,
             job.service_type,
             job.method_name,
             job.priority,
             job.enqueue_sequence,
             job.enqueued_at_utc,
             job.not_before_utc,
             job.attempt_count,
             job.max_attempts,
             job.source_schedule_key,
             job.completed_at_utc,
             job.failed_at_utc,
             job.canceled_at_utc,
             job.claimed_at_utc,
             job.claimed_by_node_id,
             job.lease_expires_at_utc,
             job.scheduled_fire_at_utc,
             job.retry_clone_source_job_id,
             job.cancellation_requested_at_utc,
             job.cancellation_observed_at_utc,
             job.schedule_occurrence_kind
         from {context.Names.Jobs} job
         """;
}
