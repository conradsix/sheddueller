namespace Sheddueller.Postgres.Internal.Operations;

using System.Globalization;
using System.Runtime.CompilerServices;

using Npgsql;

using Sheddueller.Inspection.Jobs;
using Sheddueller.Storage;

internal static class PostgresJobInspectionOperation
{
    /// <summary>
    /// The invocation columns that <see cref="ReadJobDetailRowAsync" /> reads after
    /// <see cref="PostgresJobInspectionRow.ColumnsSql" />, in ordinal order.
    /// </summary>
    private const string InvocationColumnsSql =
      """
      job.method_parameter_types,
      job.invocation_target_kind,
      job.method_parameter_bindings,
      job.serialized_arguments_content_type,
      job.serialized_arguments
      """;

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
          cancellationToken)
          .ConfigureAwait(false);
        var recentlyFailedRows = await ReadSummaryRowsAsync(
          context,
          connection,
          "where state = 'Failed' order by failed_at_utc desc nulls last, enqueue_sequence desc limit 10",
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
          cancellationToken)
          .ConfigureAwait(false);

        var allRows = runningRows
          .Concat(recentlyFailedRows)
          .Concat(queuedRows)
          .Concat(delayedRows)
          .Concat(retryWaitingRows)
          .ToArray();
        var summaries = await PostgresJobSummaryLoader.LoadByJobIdAsync(context, connection, allRows, nowUtc, cancellationToken).ConfigureAwait(false);
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
                    if (PostgresJobInspectionContinuationTokens.DecodeNewest(query.ContinuationToken) is { } sequence)
                    {
                        conditions.Add("job.enqueue_sequence < @after_sequence");
                        command.Parameters.AddWithValue("after_sequence", sequence);
                        whereClause = CreateWhereClause(conditions);
                    }

                    command.CommandText =
                      $"""
                  {PostgresJobInspectionRow.SelectSql(context.Names)}
                  {whereClause}
                  order by job.enqueue_sequence desc
                  limit @limit;
                  """;

                    var newestRows = await PostgresJobInspectionRow.ReadAllAsync(command, cancellationToken).ConfigureAwait(false);
                    var newestPageRows = newestRows.Take(query.PageSize).ToArray();
                    var newestJobs = await PostgresJobSummaryLoader.LoadAsync(context, connection, newestPageRows, nowUtc: null, cancellationToken).ConfigureAwait(false);
                    continuationToken = newestRows.Count > query.PageSize
                      ? PostgresJobInspectionContinuationTokens.EncodeNewest(newestPageRows[^1].EnqueueSequence)
                      : null;

                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return new JobInspectionPage(newestJobs, continuationToken, totalCount);
                }

            case JobInspectionSort.Operational:
                {
                    var offset = PostgresJobInspectionContinuationTokens.DecodeOperational(query.ContinuationToken);
                    command.Parameters.AddWithValue("offset", offset);
                    command.CommandText =
                      $"""
                  with filtered_jobs as (
                      {PostgresJobInspectionRow.SelectSql(context.Names)}
                      {whereClause}
                  )
                  select *
                  from filtered_jobs job
                  order by {OperationalOrderBySql(context)}
                  offset @offset
                  limit @limit;
                  """;

                    var operationalRows = await PostgresJobInspectionRow.ReadAllAsync(command, cancellationToken).ConfigureAwait(false);
                    var operationalPageRows = operationalRows.Take(query.PageSize).ToArray();
                    var operationalJobs = await PostgresJobSummaryLoader.LoadAsync(context, connection, operationalPageRows, nowUtc: null, cancellationToken).ConfigureAwait(false);
                    continuationToken = operationalRows.Count > query.PageSize
                      ? PostgresJobInspectionContinuationTokens.EncodeOperational(offset + query.PageSize)
                      : null;

                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return new JobInspectionPage(operationalJobs, continuationToken, totalCount);
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(query), query.Sort, "Job inspection sort is not supported.");
        }
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
        var summaries = await PostgresJobSummaryLoader.LoadAsync(context, connection, [row], nowUtc: null, cancellationToken).ConfigureAwait(false);
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

        var positions = await PostgresJobSummaryLoader.ReadQueuePositionsAsync(context, connection, [row], nowUtc: null, cancellationToken).ConfigureAwait(false);
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
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
          $"""
          {PostgresJobInspectionRow.SelectSql(context.Names)}
          {clause};
          """;

        return await PostgresJobInspectionRow.ReadAllAsync(command, cancellationToken).ConfigureAwait(false);
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
          {PostgresJobInspectionRow.SelectSql(context.Names)}
          where job.job_id = @job_id;
          """;
        command.Parameters.AddWithValue("job_id", jobId);

        var rows = await PostgresJobInspectionRow.ReadAllAsync(command, cancellationToken).ConfigureAwait(false);
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
              {PostgresJobInspectionRow.ColumnsSql},
              {InvocationColumnsSql}
          from {context.Names.Jobs} job
          where job.job_id = @job_id;
          """;
        command.Parameters.AddWithValue("job_id", jobId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var row = PostgresJobInspectionRow.Read(reader);
        const int InvocationOrdinal = PostgresJobInspectionRow.ColumnCount;
        var invocation = PostgresJobInvocationFormatter.Format(
          PostgresConversion.ToInvocationTargetKind(reader.GetValue(InvocationOrdinal + 1)),
          row.ServiceType,
          row.MethodName,
          reader.GetFieldValue<string[]>(InvocationOrdinal),
          PostgresConversion.ToParameterBindings(reader.GetValue(InvocationOrdinal + 2)),
          PostgresConversion.ToPayload(reader.GetValue(InvocationOrdinal + 3), reader.GetValue(InvocationOrdinal + 4)));

        return (row, invocation);
    }

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

        return await ReadJobIdsAsync(command, cancellationToken).ConfigureAwait(false);
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
            JobInspectionSort.NewestFirst => PostgresJobInspectionContinuationTokens.DecodeNewest(query.ContinuationToken) ?? 0,
            JobInspectionSort.Operational => PostgresJobInspectionContinuationTokens.DecodeOperational(query.ContinuationToken),
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
}
