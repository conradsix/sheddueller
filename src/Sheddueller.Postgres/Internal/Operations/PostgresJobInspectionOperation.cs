namespace Sheddueller.Postgres.Internal.Operations;

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;

using Npgsql;

using Sheddueller.Inspection.Jobs;
using Sheddueller.Serialization;
using Sheddueller.Storage;

internal static class PostgresJobInspectionOperation
{
    private const string OperationalContinuationTokenPrefix = "op:";
    private const int SerializedArgumentDisplayByteLimit = 64 * 1024;

    /// <summary>
    /// The job columns that <see cref="ReadRow"/> reads, in ordinal order.
    /// </summary>
    private const string JobColumnsSql =
      """
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
      """;

    private static readonly JsonSerializerOptions SerializedArgumentDisplayJsonOptions = new()
    {
        WriteIndented = true,
    };

    public static async ValueTask<JobInspectionOverview> GetOverviewAsync(
        PostgresOperationContext context,
        CancellationToken cancellationToken)
    {
        await using var connection = await context.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await PostgresOperationContext.BeginReadSnapshotAsync(connection, cancellationToken).ConfigureAwait(false);
        var counts = await ReadStateCountsAsync(context, connection, cancellationToken).ConfigureAwait(false);
        var nowUtc = await PostgresOperationContext.ReadTransactionTimestampAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var runningRows = await ReadSummaryRowsAsync(
          context,
          connection,
          "where state = 'Claimed' order by claimed_at_utc desc nulls last, enqueue_sequence desc limit 10",
          static _ => { },
          cancellationToken)
          .ConfigureAwait(false);
        var recentlyFailedRows = await ReadSummaryRowsAsync(
          context,
          connection,
          "where state = 'Failed' order by failed_at_utc desc nulls last, enqueue_sequence desc limit 10",
          static _ => { },
          cancellationToken)
          .ConfigureAwait(false);
        var queuedRows = await ReadSummaryRowsAsync(
          context,
          connection,
          $"""
          where {PostgresSqlFragments.JobIsClaimable(context.Names, "job", PostgresSqlFragments.TransactionNow)}
          order by {PostgresSqlFragments.ClaimOrder("job")}
          limit 10
          """,
          static _ => { },
          cancellationToken)
          .ConfigureAwait(false);
        var delayedRows = await ReadSummaryRowsAsync(
          context,
          connection,
          """
          where state = 'Queued'
            and not_before_utc > transaction_timestamp()
            and failed_at_utc is null
          order by not_before_utc asc, enqueue_sequence asc
          limit 10
          """,
          static _ => { },
          cancellationToken)
          .ConfigureAwait(false);
        var retryWaitingRows = await ReadSummaryRowsAsync(
          context,
          connection,
          """
          where state = 'Queued'
            and not_before_utc > transaction_timestamp()
            and failed_at_utc is not null
          order by not_before_utc asc, enqueue_sequence asc
          limit 10
          """,
          static _ => { },
          cancellationToken)
          .ConfigureAwait(false);

        var allRows = runningRows
          .Concat(recentlyFailedRows)
          .Concat(queuedRows)
          .Concat(delayedRows)
          .Concat(retryWaitingRows)
          .ToArray();
        var summaries = await CreateSummaryMapAsync(context, connection, allRows, nowUtc, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new JobInspectionOverview(
          counts,
          SelectSummaries(runningRows, summaries),
          SelectSummaries(recentlyFailedRows, summaries),
          SelectSummaries(queuedRows, summaries),
          SelectSummaries(delayedRows, summaries),
          SelectSummaries(retryWaitingRows, summaries));
    }

    public static async ValueTask<JobInspectionPage> SearchJobsAsync(
        PostgresOperationContext context,
        JobInspectionQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ValidateJobQuery(query);
        await using var connection = await context.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await PostgresOperationContext.BeginReadSnapshotAsync(connection, cancellationToken).ConfigureAwait(false);

        void configureFilters(NpgsqlCommand command, List<string> conditions)
        {
            if (query.States is { Count: > 0 } states)
            {
                conditions.Add("job.state = any(@states)");
                command.Parameters.AddWithValue("states", states.Select(PostgresConversion.ToText).ToArray());
            }

            if (NormalizeContains(query.HandlerContains) is { } handlerContains)
            {
                conditions.Add("job.handler_search_text ilike @handler_contains escape '\\'");
                command.Parameters.AddWithValue("handler_contains", CreateContainsPattern(handlerContains));
            }

            if (NormalizeContains(query.TagContains) is { } tagContains)
            {
                conditions.Add($"exists (select 1 from {context.Names.JobTags} tag where tag.job_id = job.job_id and (tag.name || ':' || tag.value) ilike @tag_contains escape '\\')");
                command.Parameters.AddWithValue("tag_contains", CreateContainsPattern(tagContains));
            }

            if (NormalizeContains(query.ConcurrencyGroupContains) is { } groupContains)
            {
                conditions.Add($"exists (select 1 from {context.Names.JobConcurrencyGroups} job_group where job_group.job_id = job.job_id and job_group.group_key ilike @group_contains escape '\\')");
                command.Parameters.AddWithValue("group_contains", CreateContainsPattern(groupContains));
            }
        }

        await using var countCommand = connection.CreateCommand();
        var countConditions = new List<string>();
        configureFilters(countCommand, countConditions);
        countCommand.CommandText =
          $"""
          select count(*)
          from {context.Names.Jobs} job
          {CreateWhereClause(countConditions)};
          """;
        var totalCount = Convert.ToInt64(
          await countCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
          CultureInfo.InvariantCulture);

        await using var command = connection.CreateCommand();
        var conditions = new List<string>();
        configureFilters(command, conditions);
        command.Parameters.AddWithValue("limit", query.PageSize + 1);
        var whereClause = CreateWhereClause(conditions);
        string? continuationToken;

        switch (query.Sort)
        {
            case JobInspectionSort.NewestFirst:
                {
                    if (DecodeNewestContinuationToken(query.ContinuationToken) is { } sequence)
                    {
                        conditions.Add("job.enqueue_sequence < @after_sequence");
                        command.Parameters.AddWithValue("after_sequence", sequence);
                        whereClause = CreateWhereClause(conditions);
                    }

                    command.CommandText =
                      $"""
                  {JobSelectSql(context)}
                  {whereClause}
                  order by job.enqueue_sequence desc
                  limit @limit;
                  """;

                    var newestRows = await ReadRowsAsync(command, cancellationToken).ConfigureAwait(false);
                    var newestPageRows = newestRows.Take(query.PageSize).ToArray();
                    var newestJobs = await CreateSummariesAsync(context, connection, newestPageRows, cancellationToken).ConfigureAwait(false);
                    continuationToken = newestRows.Count > query.PageSize
                      ? newestPageRows[^1].EnqueueSequence.ToString(CultureInfo.InvariantCulture)
                      : null;

                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return new JobInspectionPage(newestJobs, continuationToken, totalCount);
                }

            case JobInspectionSort.Operational:
                {
                    var offset = DecodeOperationalContinuationToken(query.ContinuationToken);
                    command.Parameters.AddWithValue("offset", offset);
                    command.CommandText =
                      $"""
                  with filtered_jobs as (
                      {JobSelectSql(context)}
                      {whereClause}
                  )
                  select *
                  from filtered_jobs job
                  order by {OperationalOrderBySql(context)}
                  offset @offset
                  limit @limit;
                  """;

                    var operationalRows = await ReadRowsAsync(command, cancellationToken).ConfigureAwait(false);
                    var operationalPageRows = operationalRows.Take(query.PageSize).ToArray();
                    var operationalJobs = await CreateSummariesAsync(context, connection, operationalPageRows, cancellationToken).ConfigureAwait(false);
                    continuationToken = operationalRows.Count > query.PageSize
                      ? EncodeOperationalContinuationToken(offset + query.PageSize)
                      : null;

                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return new JobInspectionPage(operationalJobs, continuationToken, totalCount);
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(query), query.Sort, "Job inspection sort is not supported.");
        }
    }

    private static async ValueTask<IReadOnlyList<JobInspectionSummary>> CreateSummariesAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        IReadOnlyList<PostgresJobInspectionRow> rows,
        CancellationToken cancellationToken)
      => await CreateSummariesAsync(context, connection, rows, nowUtc: null, cancellationToken).ConfigureAwait(false);

    private static async ValueTask<IReadOnlyList<JobInspectionSummary>> CreateSummariesAsync(
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

    private static async ValueTask<IReadOnlyDictionary<Guid, JobInspectionSummary>> CreateSummaryMapAsync(
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

    private static List<JobInspectionSummary> SelectSummaries(
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

    public static async ValueTask<JobInspectionDetail?> GetJobAsync(
        PostgresOperationContext context,
        Guid jobId,
        CancellationToken cancellationToken)
    {
        await using var connection = await context.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await PostgresOperationContext.BeginReadSnapshotAsync(connection, cancellationToken).ConfigureAwait(false);
        var detailRow = await ReadJobDetailRowAsync(context, connection, jobId, cancellationToken).ConfigureAwait(false);
        if (detailRow is null)
        {
            return null;
        }

        var (row, invocation) = detailRow.Value;
        var summaries = await CreateSummariesAsync(context, connection, [row], cancellationToken).ConfigureAwait(false);
        var detail = new JobInspectionDetail(
          summaries[0],
          row.ClaimedAtUtc,
          row.ClaimedByNodeId,
          row.LeaseExpiresAtUtc,
          row.ScheduledFireAtUtc)
        {
            Invocation = invocation,
            RetryCloneJobIds = await ReadRetryCloneJobIdsAsync(context, connection, jobId, cancellationToken).ConfigureAwait(false),
            PrerequisiteJobIds = await ReadPrerequisiteJobIdsAsync(context, connection, jobId, cancellationToken).ConfigureAwait(false),
            DependentJobIds = await ReadDependentJobIdsAsync(context, connection, jobId, cancellationToken).ConfigureAwait(false),
        };
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return detail;
    }

    public static async ValueTask<JobQueuePosition> GetQueuePositionAsync(
        PostgresOperationContext context,
        Guid jobId,
        CancellationToken cancellationToken)
    {
        await using var connection = await context.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await PostgresOperationContext.BeginReadSnapshotAsync(connection, cancellationToken).ConfigureAwait(false);
        var row = await ReadJobRowAsync(context, connection, jobId, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return new JobQueuePosition(jobId, JobQueuePositionKind.NotFound, Position: null, "Job was not found.");
        }

        var positions = await ReadQueuePositionsAsync(context, connection, [row], nowUtc: null, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return positions[jobId];
    }

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

    private static async ValueTask<IReadOnlyDictionary<JobState, int>> ReadStateCountsAsync(
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

    private static string CreateWhereClause(List<string> conditions)
      => conditions.Count == 0 ? string.Empty : $"where {string.Join(" and ", conditions)}";

    private static async ValueTask<IReadOnlyList<PostgresJobInspectionRow>> ReadSummaryRowsAsync(
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

    private static async ValueTask<IReadOnlyDictionary<Guid, JobQueuePosition>> ReadQueuePositionsAsync(
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

        await using var command = connection.CreateCommand();
        command.CommandText =
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
          """;
        command.Parameters.AddWithValue("job_ids", jobIds);

        var positions = new Dictionary<Guid, long>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            positions.Add(reader.GetGuid(0), reader.GetInt64(1));
        }

        return positions;
    }

    private static async ValueTask<PostgresJobInspectionRow?> ReadJobRowAsync(
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

    private static async ValueTask<(PostgresJobInspectionRow Row, JobInvocationInspection Invocation)?> ReadJobDetailRowAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        Guid jobId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
          $"""
          select
              {JobColumnsSql},
              job.method_parameter_types,
              job.invocation_target_kind,
              job.method_parameter_bindings,
              job.serialized_arguments_content_type,
              job.serialized_arguments
          from {context.Names.Jobs} job
          where job.job_id = @job_id;
          """;
        command.Parameters.AddWithValue("job_id", jobId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var row = ReadRow(reader);
        var parameterTypes = reader.GetFieldValue<string[]>(reader.GetOrdinal("method_parameter_types"));
        var targetKind = PostgresConversion.ToInvocationTargetKind(reader.GetValue(reader.GetOrdinal("invocation_target_kind")));
        var parameterBindings = JobMethodParameterBindingResolver.Normalize(
          parameterTypes,
          PostgresConversion.ToParameterBindings(reader.GetValue(reader.GetOrdinal("method_parameter_bindings"))));
        var payload = PostgresConversion.ToPayload(
          reader.GetValue(reader.GetOrdinal("serialized_arguments_content_type")),
          reader.GetValue(reader.GetOrdinal("serialized_arguments")));

        return (row, CreateInvocation(row, targetKind, parameterTypes, parameterBindings, payload));
    }

    private static JobInvocationInspection CreateInvocation(
        PostgresJobInspectionRow row,
        JobInvocationTargetKind targetKind,
        string[] parameterTypes,
        IReadOnlyList<JobMethodParameterBinding> parameterBindings,
        SerializedJobPayload payload)
    {
        var serializedValueJson = new string?[parameterTypes.Length];
        var status = PopulateSerializedValueJson(payload, parameterBindings, serializedValueJson, out var statusMessage);
        var parameters = new JobInvocationParameterInspection[parameterTypes.Length];

        for (var i = 0; i < parameterTypes.Length; i++)
        {
            parameters[i] = new JobInvocationParameterInspection(
              i,
              parameterTypes[i],
              parameterBindings[i],
              serializedValueJson[i]);
        }

        var reconstructedCall = JobInvocationDisplayFormatter.Format(row.ServiceType, row.MethodName, parameters);

        return new JobInvocationInspection(
          targetKind,
          row.ServiceType,
          row.MethodName,
          reconstructedCall,
          parameters,
          payload.ContentType,
          payload.Data.LongLength,
          status,
          statusMessage);
    }

    private static JobSerializedArgumentsInspectionStatus PopulateSerializedValueJson(
        SerializedJobPayload payload,
        IReadOnlyList<JobMethodParameterBinding> parameterBindings,
        string?[] serializedValueJson,
        out string? statusMessage)
    {
        if (!string.Equals(payload.ContentType, SystemTextJsonJobPayloadSerializer.JsonContentType, StringComparison.Ordinal))
        {
            statusMessage = string.Create(
              CultureInfo.InvariantCulture,
              $"Serialized arguments use unsupported content type '{payload.ContentType}'.");
            return JobSerializedArgumentsInspectionStatus.UnsupportedContentType;
        }

        if (payload.Data.LongLength > SerializedArgumentDisplayByteLimit)
        {
            statusMessage = string.Create(
              CultureInfo.InvariantCulture,
              $"Serialized arguments are {payload.Data.LongLength:N0} bytes, exceeding the {SerializedArgumentDisplayByteLimit:N0} byte display limit.");
            return JobSerializedArgumentsInspectionStatus.TooLarge;
        }

        var serializedParameterIndexes = parameterBindings
          .Select((binding, index) => (binding, index))
          .Where(parameter => parameter.binding.Kind == JobMethodParameterBindingKind.Serialized)
          .Select(parameter => parameter.index)
          .ToArray();

        try
        {
            using var document = JsonDocument.Parse(payload.Data);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array)
            {
                statusMessage = "Serialized arguments payload is not a JSON array.";
                return JobSerializedArgumentsInspectionStatus.InvalidPayload;
            }

            if (root.GetArrayLength() != serializedParameterIndexes.Length)
            {
                statusMessage = string.Create(
                  CultureInfo.InvariantCulture,
                  $"Serialized arguments contain {root.GetArrayLength()} values for {serializedParameterIndexes.Length} serialized parameters.");
                return JobSerializedArgumentsInspectionStatus.ArgumentCountMismatch;
            }

            var argumentIndex = 0;
            foreach (var element in root.EnumerateArray())
            {
                serializedValueJson[serializedParameterIndexes[argumentIndex]] = JsonSerializer.Serialize(element, SerializedArgumentDisplayJsonOptions);
                argumentIndex++;
            }

            statusMessage = null;
            return JobSerializedArgumentsInspectionStatus.Displayable;
        }
        catch (JsonException)
        {
            statusMessage = "Serialized arguments payload is not valid JSON.";
            return JobSerializedArgumentsInspectionStatus.InvalidPayload;
        }
    }

    private static async ValueTask<IReadOnlyList<PostgresJobInspectionRow>> ReadRowsAsync(
        NpgsqlCommand command,
        CancellationToken cancellationToken)
    {
        var rows = new List<PostgresJobInspectionRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(ReadRow(reader));
        }

        return rows;
    }

    private static PostgresJobInspectionRow ReadRow(NpgsqlDataReader reader)
      => new(
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
        PostgresConversion.ToScheduleOccurrenceKind(reader.GetValue(21)));

    private static async ValueTask<IReadOnlyList<JobEvent>> ReadEventPageAsync(
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

    private static async ValueTask<IReadOnlyList<Guid>> ReadRetryCloneJobIdsAsync(
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

    private static async ValueTask<IReadOnlyList<Guid>> ReadPrerequisiteJobIdsAsync(
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

    private static async ValueTask<IReadOnlyList<Guid>> ReadDependentJobIdsAsync(
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

    private static async ValueTask<IReadOnlyList<Guid>> ReadJobIdsAsync(
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

        await using var command = connection.CreateCommand();
        command.CommandText =
          $"""
          select job.job_id
          from {context.Names.Jobs} job
          where job.job_id = any(@job_ids)
            and {PostgresSqlFragments.HasUnfinishedPrerequisites(context.Names, "job")};
          """;
        command.Parameters.AddWithValue("job_ids", jobIds);

        var blocked = new HashSet<Guid>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            blocked.Add(reader.GetGuid(0));
        }

        return blocked;
    }

    private static string JobSelectSql(PostgresOperationContext context)
      => $"""
         select
             {JobColumnsSql}
         from {context.Names.Jobs} job
         """;

    private static string OperationalOrderBySql(PostgresOperationContext context)
      => $"""
         case job.state
             when 'Claimed' then 0
             when 'Queued' then 1
             else 2
         end asc,
         case when job.state = 'Claimed' then job.claimed_at_utc end desc nulls last,
         case when job.state = 'Claimed' then job.enqueue_sequence end desc,
         case
             when job.state <> 'Queued' then 0
             when job.not_before_utc is not null
                  and job.not_before_utc > transaction_timestamp()
                  and job.failed_at_utc is not null then 2
             when job.not_before_utc is not null
                  and job.not_before_utc > transaction_timestamp() then 3
             when {PostgresSqlFragments.HasUnfinishedPrerequisites(context.Names, "job")}
                 or {PostgresSqlFragments.HasBlockingGroup(context.Names, "job", PostgresSqlFragments.TransactionNow)} then 1
             else 0
         end asc,
         case when job.state = 'Queued' then job.priority end desc,
         case when job.state = 'Queued' then job.enqueue_sequence end asc,
         {PostgresSqlFragments.TerminalAt("job")} desc nulls last,
         job.enqueue_sequence desc
         """;

    private static void ValidateJobQuery(JobInspectionQuery query)
    {
        if (query.PageSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(query), query.PageSize, "Job inspection query page size must be positive.");
        }

        if (query.States is not null)
        {
            foreach (var state in query.States)
            {
                if (!Enum.IsDefined(state))
                {
                    throw new ArgumentOutOfRangeException(nameof(query), state, "Job state is not supported.");
                }
            }
        }

        if (!Enum.IsDefined(query.Sort))
        {
            throw new ArgumentOutOfRangeException(nameof(query), query.Sort, "Job inspection sort is not supported.");
        }

        _ = query.Sort switch
        {
            JobInspectionSort.NewestFirst => DecodeNewestContinuationToken(query.ContinuationToken) ?? 0,
            JobInspectionSort.Operational => DecodeOperationalContinuationToken(query.ContinuationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(query), query.Sort, "Job inspection sort is not supported."),
        };
    }

    private static string? NormalizeContains(string? value)
      => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string CreateContainsPattern(string value)
      => string.Concat("%", EscapeLikePattern(value), "%");

    private static string EscapeLikePattern(string value)
      => value
        .Replace(@"\", @"\\", StringComparison.Ordinal)
        .Replace("%", @"\%", StringComparison.Ordinal)
        .Replace("_", @"\_", StringComparison.Ordinal);

    private static void ValidateEventReadOptions(JobEventReadOptions options)
    {
        if (options.Limit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.Limit, "Job event read options limit must be positive.");
        }
    }

    private static string EncodeOperationalContinuationToken(long offset)
      => string.Concat(OperationalContinuationTokenPrefix, offset.ToString(CultureInfo.InvariantCulture));

    private static long DecodeOperationalContinuationToken(string? continuationToken)
    {
        if (continuationToken is null)
        {
            return 0;
        }

        var offsetToken = continuationToken.StartsWith(OperationalContinuationTokenPrefix, StringComparison.Ordinal)
          ? continuationToken[OperationalContinuationTokenPrefix.Length..]
          : continuationToken;

        if (!long.TryParse(offsetToken, NumberStyles.None, CultureInfo.InvariantCulture, out var offset) || offset < 0)
        {
            throw new ArgumentException("Job inspection continuation token is invalid.", nameof(continuationToken));
        }

        return offset;
    }

    private static long? DecodeNewestContinuationToken(string? continuationToken)
    {
        if (continuationToken is null)
        {
            return null;
        }

        if (!long.TryParse(continuationToken, NumberStyles.None, CultureInfo.InvariantCulture, out var enqueueSequence) || enqueueSequence <= 0)
        {
            throw new ArgumentException("Job inspection continuation token is invalid.", nameof(continuationToken));
        }

        return enqueueSequence;
    }

    private sealed record PostgresJobInspectionRow(
        Guid JobId,
        JobState State,
        string ServiceType,
        string MethodName,
        int Priority,
        long EnqueueSequence,
        DateTimeOffset EnqueuedAtUtc,
        DateTimeOffset? NotBeforeUtc,
        int AttemptCount,
        int MaxAttempts,
        string? SourceScheduleKey,
        DateTimeOffset? CompletedAtUtc,
        DateTimeOffset? FailedAtUtc,
        DateTimeOffset? CanceledAtUtc,
        DateTimeOffset? ClaimedAtUtc,
        string? ClaimedByNodeId,
        DateTimeOffset? LeaseExpiresAtUtc,
        DateTimeOffset? ScheduledFireAtUtc,
        Guid? RetryCloneSourceJobId,
        DateTimeOffset? CancellationRequestedAtUtc,
        DateTimeOffset? CancellationObservedAtUtc,
        ScheduleOccurrenceKind? ScheduleOccurrenceKind);
}
