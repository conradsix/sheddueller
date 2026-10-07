namespace Sheddueller.Postgres.Internal.Operations;

using System.Globalization;

using Npgsql;

using Sheddueller.Inspection.Jobs;
using Sheddueller.Storage;

internal static class PostgresJobInspectionOperation
{
    public static async ValueTask<JobInspectionOverview> GetOverviewAsync(
        PostgresOperationContext context,
        CancellationToken cancellationToken)
    {
        await using var connection = await context.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var counts = await PostgresJobInspectionRows.ReadStateCountsAsync(context, connection, cancellationToken).ConfigureAwait(false);
        var nowUtc = await PostgresJobInspectionRows.ReadCurrentTimestampAsync(connection, cancellationToken).ConfigureAwait(false);
        var runningRows = await PostgresJobInspectionRows.ReadSummaryRowsAsync(
          context,
          connection,
          "where state = 'Claimed' order by claimed_at_utc desc nulls last, enqueue_sequence desc limit 10",
          static _ => { },
          cancellationToken)
          .ConfigureAwait(false);
        var recentlyFailedRows = await PostgresJobInspectionRows.ReadSummaryRowsAsync(
          context,
          connection,
          "where state = 'Failed' order by failed_at_utc desc nulls last, enqueue_sequence desc limit 10",
          static _ => { },
          cancellationToken)
          .ConfigureAwait(false);
        var queuedRows = await PostgresJobInspectionRows.ReadSummaryRowsAsync(
          context,
          connection,
          $"""
          where state = 'Queued'
            and (not_before_utc is null or not_before_utc <= @now_utc)
            and not exists (
                select 1
                from {context.Names.JobConcurrencyGroups} job_group
                left join {context.Names.ConcurrencyGroups} concurrency_group on concurrency_group.group_key = job_group.group_key
                where job_group.job_id = job.job_id
                  and coalesce(concurrency_group.in_use_count, 0) >= coalesce(concurrency_group.effective_limit, 1)
            )
          order by priority desc, enqueue_sequence asc
          limit 10
          """,
          command => command.Parameters.AddWithValue("now_utc", nowUtc),
          cancellationToken)
          .ConfigureAwait(false);
        var delayedRows = await PostgresJobInspectionRows.ReadSummaryRowsAsync(
          context,
          connection,
          """
          where state = 'Queued'
            and not_before_utc > @now_utc
            and failed_at_utc is null
          order by not_before_utc asc, enqueue_sequence asc
          limit 10
          """,
          command => command.Parameters.AddWithValue("now_utc", nowUtc),
          cancellationToken)
          .ConfigureAwait(false);
        var retryWaitingRows = await PostgresJobInspectionRows.ReadSummaryRowsAsync(
          context,
          connection,
          """
          where state = 'Queued'
            and not_before_utc > @now_utc
            and failed_at_utc is not null
          order by not_before_utc asc, enqueue_sequence asc
          limit 10
          """,
          command => command.Parameters.AddWithValue("now_utc", nowUtc),
          cancellationToken)
          .ConfigureAwait(false);

        var allRows = runningRows
          .Concat(recentlyFailedRows)
          .Concat(queuedRows)
          .Concat(delayedRows)
          .Concat(retryWaitingRows)
          .ToArray();
        var summaries = await PostgresJobInspectionRows.CreateSummaryMapAsync(context, connection, allRows, nowUtc, cancellationToken).ConfigureAwait(false);

        return new JobInspectionOverview(
          counts,
          PostgresJobInspectionRows.SelectSummaries(runningRows, summaries),
          PostgresJobInspectionRows.SelectSummaries(recentlyFailedRows, summaries),
          PostgresJobInspectionRows.SelectSummaries(queuedRows, summaries),
          PostgresJobInspectionRows.SelectSummaries(delayedRows, summaries),
          PostgresJobInspectionRows.SelectSummaries(retryWaitingRows, summaries));
    }

    public static async ValueTask<JobInspectionPage> SearchJobsAsync(
        PostgresOperationContext context,
        JobInspectionQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        PostgresJobInspectionQuery.ValidateJobQuery(query);
        await using var connection = await context.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        void configureFilters(NpgsqlCommand command, List<string> conditions)
        {
            if (query.States is { Count: > 0 } states)
            {
                conditions.Add("job.state = any(@states)");
                command.Parameters.AddWithValue("states", states.Select(PostgresConversion.ToText).ToArray());
            }

            if (PostgresJobInspectionQuery.NormalizeContains(query.HandlerContains) is { } handlerContains)
            {
                conditions.Add("job.handler_search_text ilike @handler_contains escape '\\'");
                command.Parameters.AddWithValue("handler_contains", PostgresJobInspectionQuery.CreateContainsPattern(handlerContains));
            }

            if (PostgresJobInspectionQuery.NormalizeContains(query.TagContains) is { } tagContains)
            {
                conditions.Add($"exists (select 1 from {context.Names.JobTags} tag where tag.job_id = job.job_id and (tag.name || ':' || tag.value) ilike @tag_contains escape '\\')");
                command.Parameters.AddWithValue("tag_contains", PostgresJobInspectionQuery.CreateContainsPattern(tagContains));
            }

            if (PostgresJobInspectionQuery.NormalizeContains(query.ConcurrencyGroupContains) is { } groupContains)
            {
                conditions.Add($"exists (select 1 from {context.Names.JobConcurrencyGroups} job_group where job_group.job_id = job.job_id and job_group.group_key ilike @group_contains escape '\\')");
                command.Parameters.AddWithValue("group_contains", PostgresJobInspectionQuery.CreateContainsPattern(groupContains));
            }
        }

        await using var countCommand = connection.CreateCommand();
        var countConditions = new List<string>();
        configureFilters(countCommand, countConditions);
        countCommand.CommandText =
          $"""
          select count(*)
          from {context.Names.Jobs} job
          {PostgresJobInspectionQuery.CreateWhereClause(countConditions)};
          """;
        var totalCount = Convert.ToInt64(
          await countCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
          CultureInfo.InvariantCulture);

        await using var command = connection.CreateCommand();
        var conditions = new List<string>();
        configureFilters(command, conditions);
        command.Parameters.AddWithValue("limit", query.PageSize + 1);
        var whereClause = PostgresJobInspectionQuery.CreateWhereClause(conditions);
        string? continuationToken;

        switch (query.Sort)
        {
            case JobInspectionSort.NewestFirst:
                {
                    if (PostgresJobInspectionQuery.DecodeNewestContinuationToken(query.ContinuationToken) is { } sequence)
                    {
                        conditions.Add("job.enqueue_sequence < @after_sequence");
                        command.Parameters.AddWithValue("after_sequence", sequence);
                        whereClause = PostgresJobInspectionQuery.CreateWhereClause(conditions);
                    }

                    command.CommandText =
                      $"""
                  {PostgresJobInspectionRows.JobSelectSql(context)}
                  {whereClause}
                  order by job.enqueue_sequence desc
                  limit @limit;
                  """;

                    var newestRows = await PostgresJobInspectionRows.ReadRowsAsync(command, cancellationToken).ConfigureAwait(false);
                    var newestPageRows = newestRows.Take(query.PageSize).ToArray();
                    var newestJobs = await PostgresJobInspectionRows.CreateSummariesAsync(context, connection, newestPageRows, cancellationToken).ConfigureAwait(false);
                    continuationToken = newestRows.Count > query.PageSize
                      ? newestPageRows[^1].EnqueueSequence.ToString(CultureInfo.InvariantCulture)
                      : null;

                    return new JobInspectionPage(newestJobs, continuationToken, totalCount);
                }

            case JobInspectionSort.Operational:
                {
                    var offset = PostgresJobInspectionQuery.DecodeOperationalContinuationToken(query.ContinuationToken);
                    command.Parameters.AddWithValue("offset", offset);
                    command.CommandText =
                      $"""
                  with filtered_jobs as (
                      {PostgresJobInspectionRows.JobSelectSql(context)}
                      {whereClause}
                  )
                  select *
                  from filtered_jobs job
                  order by {PostgresJobInspectionQuery.OperationalOrderBySql(context)}
                  offset @offset
                  limit @limit;
                  """;

                    var operationalRows = await PostgresJobInspectionRows.ReadRowsAsync(command, cancellationToken).ConfigureAwait(false);
                    var operationalPageRows = operationalRows.Take(query.PageSize).ToArray();
                    var operationalJobs = await PostgresJobInspectionRows.CreateSummariesAsync(context, connection, operationalPageRows, cancellationToken).ConfigureAwait(false);
                    continuationToken = operationalRows.Count > query.PageSize
                      ? PostgresJobInspectionQuery.EncodeOperationalContinuationToken(offset + query.PageSize)
                      : null;

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
        var row = await PostgresJobInspectionRows.ReadJobRowAsync(context, connection, jobId, cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        return new JobInspectionDetail(
          await PostgresJobInspectionRows.CreateSummaryAsync(context, connection, row, cancellationToken).ConfigureAwait(false),
          row.ClaimedAtUtc,
          row.ClaimedByNodeId,
          row.LeaseExpiresAtUtc,
          row.ScheduledFireAtUtc)
        {
            Invocation = await PostgresJobInvocationReader.ReadInvocationAsync(context, connection, row, cancellationToken).ConfigureAwait(false),
            RetryCloneJobIds = await PostgresJobInspectionEvents.ReadRetryCloneJobIdsAsync(context, connection, jobId, cancellationToken).ConfigureAwait(false),
            PrerequisiteJobIds = await PostgresJobInspectionEvents.ReadPrerequisiteJobIdsAsync(context, connection, jobId, cancellationToken).ConfigureAwait(false),
            DependentJobIds = await PostgresJobInspectionEvents.ReadDependentJobIdsAsync(context, connection, jobId, cancellationToken).ConfigureAwait(false),
        };
    }

    public static ValueTask<JobQueuePosition> GetQueuePositionAsync(PostgresOperationContext context, Guid jobId, CancellationToken cancellationToken)
      => PostgresJobQueuePositions.GetQueuePositionAsync(context, jobId, cancellationToken);

    public static IAsyncEnumerable<JobEvent> ReadEventsAsync(PostgresOperationContext context, Guid jobId, JobEventReadOptions? options, CancellationToken cancellationToken)
      => PostgresJobInspectionEvents.ReadEventsAsync(context, jobId, options, cancellationToken);

    public static ValueTask<int> CleanupAsync(PostgresOperationContext context, TimeSpan retention, CancellationToken cancellationToken)
      => PostgresJobInspectionEvents.CleanupAsync(context, retention, cancellationToken);
}
