namespace Sheddueller.Postgres.Internal.Operations;

using Npgsql;

using Sheddueller.Inspection.Jobs;
using Sheddueller.Storage;

/// <summary>
/// Turns a page of job rows into inspection summaries, reading their tags, groups, latest progress and queue
/// positions with one batched query each rather than one query per job.
/// </summary>
internal static class PostgresJobSummaryLoader
{
    /// <summary>
    /// Creates a summary for each row, in row order. Delayed jobs are judged against <c>nowUtc</c>, or, when it is
    /// null, against the current transaction's timestamp, read only if a queued row needs it.
    /// </summary>
    public static async ValueTask<IReadOnlyList<JobInspectionSummary>> LoadAsync(
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
        var queuePositionsByJobId = await ReadQueuePositionsAsync(context, connection, rows, nowUtc, cancellationToken).ConfigureAwait(false);

        return CreateSummaries(rows, tagsByJobId, groupsByJobId, latestProgressByJobId, queuePositionsByJobId);
    }

    /// <summary>
    /// Creates a summary for each row, keyed by job id.
    /// </summary>
    public static async ValueTask<IReadOnlyDictionary<Guid, JobInspectionSummary>> LoadByJobIdAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        IReadOnlyList<PostgresJobInspectionRow> rows,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var summaries = await LoadAsync(context, connection, rows, nowUtc, cancellationToken).ConfigureAwait(false);
        var byJobId = new Dictionary<Guid, JobInspectionSummary>(summaries.Count);
        foreach (var summary in summaries)
        {
            byJobId[summary.JobId] = summary;
        }

        return byJobId;
    }

    /// <summary>
    /// Works out where each job stands in the queue: terminal, claimed, delayed, waiting for prerequisites, blocked by
    /// its groups, or claimable at a position in claim order.
    /// </summary>
    public static async ValueTask<IReadOnlyDictionary<Guid, JobQueuePosition>> ReadQueuePositionsAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        IReadOnlyList<PostgresJobInspectionRow> rows,
        DateTimeOffset? nowUtc,
        CancellationToken cancellationToken)
    {
        var positions = new Dictionary<Guid, JobQueuePosition>(rows.Count);
        var readyQueuedRows = new List<PostgresJobInspectionRow>();
        var queuedJobIds = rows
            .Where(static row => row.State == JobState.Queued)
            .Select(static row => row.JobId)
            .Distinct()
            .ToArray();
        var dependencyBlockedJobIds = await ReadDependencyBlockedJobIdsAsync(
            context,
            connection,
            queuedJobIds,
            cancellationToken)
          .ConfigureAwait(false);
        DateTimeOffset? resolvedNowUtc = null;

        foreach (var row in rows)
        {
            switch (row.State)
            {
                case JobState.Canceled:
                    positions[row.JobId] = new JobQueuePosition(row.JobId, JobQueuePositionKind.Canceled, Position: null, "Job was canceled.");
                    break;

                case JobState.Completed:
                case JobState.Failed:
                    positions[row.JobId] = new JobQueuePosition(row.JobId, JobQueuePositionKind.Terminal, Position: null, "Job is terminal.");
                    break;

                case JobState.Claimed:
                    positions[row.JobId] = new JobQueuePosition(row.JobId, JobQueuePositionKind.Claimed, Position: null, "Job is currently claimed.");
                    break;

                case JobState.Queued:
                    if (dependencyBlockedJobIds.Contains(row.JobId))
                    {
                        positions[row.JobId] = new JobQueuePosition(
                          row.JobId,
                          JobQueuePositionKind.WaitingForDependencies,
                          Position: null,
                          "Job is waiting for prerequisite jobs to become terminal.");
                        break;
                    }

                    resolvedNowUtc ??= nowUtc
                      ?? await PostgresOperationContext.ReadTransactionTimestampAsync(connection, transaction: null, cancellationToken).ConfigureAwait(false);
                    if (row.NotBeforeUtc is { } notBeforeUtc && notBeforeUtc > resolvedNowUtc.Value)
                    {
                        positions[row.JobId] = row.FailedAtUtc is null
                          ? new JobQueuePosition(row.JobId, JobQueuePositionKind.Delayed, Position: null, $"Job is delayed until {notBeforeUtc:O}.")
                          : new JobQueuePosition(row.JobId, JobQueuePositionKind.RetryWaiting, Position: null, $"Job is waiting to retry until {notBeforeUtc:O}.");
                    }
                    else
                    {
                        readyQueuedRows.Add(row);
                    }

                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(rows), row.State, "Job state is not supported.");
            }
        }

        if (readyQueuedRows.Count == 0)
        {
            return positions;
        }

        var readyJobIds = readyQueuedRows.Select(static row => row.JobId).Distinct().ToArray();
        var claimablePositions = await ReadClaimablePositionsAsync(context, connection, readyJobIds, cancellationToken)
          .ConfigureAwait(false);

        foreach (var row in readyQueuedRows)
        {
            positions[row.JobId] = claimablePositions.TryGetValue(row.JobId, out var position)
              ? new JobQueuePosition(row.JobId, JobQueuePositionKind.Claimable, position, "Job is currently claimable.")
              : new JobQueuePosition(row.JobId, JobQueuePositionKind.BlockedByConcurrency, Position: null, "Job is blocked by concurrency group limits or rate limits.");
        }

        return positions;
    }

    private static List<JobInspectionSummary> CreateSummaries(
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

    private static async ValueTask<IReadOnlyDictionary<Guid, IReadOnlyList<JobTag>>> ReadTagsByJobIdAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        Guid[] jobIds,
        CancellationToken cancellationToken)
    {
        if (jobIds.Length == 0)
        {
            return new Dictionary<Guid, IReadOnlyList<JobTag>>();
        }

        await using var command = PostgresCommands.Create(
          connection,
          transaction: null,
          $"""
          select job_id, name, value
          from {context.Names.JobTags}
          where job_id = any(@job_ids)
          order by job_id asc, ordinal asc, name asc, value asc;
          """);
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

    private static async ValueTask<IReadOnlyDictionary<Guid, IReadOnlyList<string>>> ReadGroupsByJobIdAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        Guid[] jobIds,
        CancellationToken cancellationToken)
    {
        if (jobIds.Length == 0)
        {
            return new Dictionary<Guid, IReadOnlyList<string>>();
        }

        await using var command = PostgresCommands.Create(
          connection,
          transaction: null,
          $"""
          select job_id, group_key
          from {context.Names.JobConcurrencyGroups}
          where job_id = any(@job_ids)
          order by job_id asc, group_key asc;
          """);
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

    private static async ValueTask<IReadOnlyDictionary<Guid, JobProgressSnapshot>> ReadLatestProgressByJobIdAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        Guid[] jobIds,
        CancellationToken cancellationToken)
    {
        if (jobIds.Length == 0)
        {
            return new Dictionary<Guid, JobProgressSnapshot>();
        }

        await using var command = PostgresCommands.Create(
          connection,
          transaction: null,
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
          """);
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

    private static async ValueTask<IReadOnlyDictionary<Guid, long>> ReadClaimablePositionsAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        Guid[] jobIds,
        CancellationToken cancellationToken)
    {
        if (jobIds.Length == 0)
        {
            return new Dictionary<Guid, long>();
        }

        await using var command = PostgresCommands.Create(
          connection,
          transaction: null,
          $"""
          with claimable as (
              select
                  job.job_id,
                  row_number() over (order by {PostgresSqlFragments.ClaimOrder("job")}) as position
              from {context.Names.Jobs} job
              where {PostgresSqlFragments.JobIsClaimable(context.Names, "job", PostgresSqlFragments.TransactionNow)}
          )
          select job_id, position
          from claimable
          where job_id = any(@job_ids);
          """);
        command.Parameters.AddWithValue("job_ids", jobIds);

        var positions = new Dictionary<Guid, long>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            positions.Add(reader.GetGuid(0), reader.GetInt64(1));
        }

        return positions;
    }

    private static async ValueTask<HashSet<Guid>> ReadDependencyBlockedJobIdsAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        Guid[] jobIds,
        CancellationToken cancellationToken)
    {
        if (jobIds.Length == 0)
        {
            return [];
        }

        await using var command = PostgresCommands.Create(
          connection,
          transaction: null,
          $"""
          select job.job_id
          from {context.Names.Jobs} job
          where job.job_id = any(@job_ids)
            and {PostgresSqlFragments.HasUnfinishedPrerequisites(context.Names, "job")};
          """);
        command.Parameters.AddWithValue("job_ids", jobIds);

        var blocked = new HashSet<Guid>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            blocked.Add(reader.GetGuid(0));
        }

        return blocked;
    }
}
