namespace Sheddueller.Postgres.Internal.Operations;

using System.Globalization;

using Npgsql;

using Sheddueller.Inspection.ConcurrencyGroups;

internal static class PostgresConcurrencyGroupInspectionOperation
{
    /// <summary>
    /// The columns that <see cref="ReadGroupSummariesAsync" /> reads, in ordinal order, from the <c>summary</c> CTE of
    /// <see cref="GroupSummaryCteSql" />.
    /// </summary>
    private const string GroupSummaryColumnsSql =
      """
      summary.group_key,
      summary.default_limit,
      summary.override_limit,
      summary.effective_limit,
      summary.current_occupancy,
      summary.blocked_count,
      summary.is_saturated,
      summary.updated_at_utc,
      summary.default_rate_permit_count,
      summary.default_rate_period,
      summary.rate_limit_override_enabled,
      summary.configured_rate_permit_count,
      summary.configured_rate_period,
      summary.effective_rate_permit_count,
      summary.effective_rate_period,
      summary.rate_theoretical_arrival_at_utc,
      summary.is_rate_limited,
      summary.concurrency_blocked_count,
      summary.rate_blocked_count
      """;

    public static async ValueTask<ConcurrencyGroupInspectionPage> SearchAsync(
        PostgresOperationContext context,
        ConcurrencyGroupInspectionQuery query,
        CancellationToken cancellationToken)
    {
        ValidateQuery(query);
        await using var connection = await context.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await PostgresOperationContext.BeginReadSnapshotAsync(connection, cancellationToken).ConfigureAwait(false);
        var totalCount = await ReadTotalCountAsync(context, connection, query, cancellationToken).ConfigureAwait(false);
        var groups = await ReadGroupSummaryPageAsync(context, connection, query, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        var pageItems = groups.Take(query.PageSize).ToArray();

        return new ConcurrencyGroupInspectionPage(
          pageItems,
          groups.Count > query.PageSize ? pageItems[^1].GroupKey : null,
          totalCount);
    }

    public static async ValueTask<ConcurrencyGroupInspectionDetail?> GetAsync(
        PostgresOperationContext context,
        string groupKey,
        CancellationToken cancellationToken)
    {
        await using var connection = await context.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await PostgresOperationContext.BeginReadSnapshotAsync(connection, cancellationToken).ConfigureAwait(false);
        var summary = await ReadGroupSummaryAsync(context, connection, groupKey, cancellationToken).ConfigureAwait(false);
        if (summary is null)
        {
            return null;
        }

        var detail = new ConcurrencyGroupInspectionDetail(
          summary,
          await ReadClaimedJobIdsAsync(context, connection, groupKey, cancellationToken).ConfigureAwait(false),
          await ReadBlockedJobIdsAsync(context, connection, groupKey, BlockKind.Any, cancellationToken).ConfigureAwait(false))
        {
            ConcurrencyBlockedJobIds = await ReadBlockedJobIdsAsync(
              context,
              connection,
              groupKey,
              BlockKind.Concurrency,
              cancellationToken).ConfigureAwait(false),
            RateBlockedJobIds = await ReadBlockedJobIdsAsync(
              context,
              connection,
              groupKey,
              BlockKind.Rate,
              cancellationToken).ConfigureAwait(false),
        };
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return detail;
    }

    private static async ValueTask<long> ReadTotalCountAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        ConcurrencyGroupInspectionQuery query,
        CancellationToken cancellationToken)
    {
        await using var command = PostgresCommands.Create(
          connection,
          transaction: null,
          built =>
          {
              var conditions = new List<string>();
              ConfigureFilters(built, conditions, query);
              return $"""
                {GroupSummaryCteSql(context)}
                select count(*)
                from summary
                {PostgresSql.WhereClause(conditions)};
                """;
          });

        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    private static async ValueTask<IReadOnlyList<ConcurrencyGroupInspectionSummary>> ReadGroupSummaryPageAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        ConcurrencyGroupInspectionQuery query,
        CancellationToken cancellationToken)
    {
        await using var command = PostgresCommands.Create(
          connection,
          transaction: null,
          built =>
          {
              var conditions = new List<string>();
              ConfigureFilters(built, conditions, query);
              if (query.ContinuationToken is not null)
              {
                  conditions.Add("summary.group_key > @after_group_key");
                  built.Parameters.AddWithValue("after_group_key", query.ContinuationToken);
              }

              built.Parameters.AddWithValue("limit", query.PageSize + 1);
              return $"""
                {GroupSummaryCteSql(context)}
                select
                    {GroupSummaryColumnsSql}
                from summary
                {PostgresSql.WhereClause(conditions)}
                order by summary.group_key asc
                limit @limit;
                """;
          });

        return await ReadGroupSummariesAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<ConcurrencyGroupInspectionSummary?> ReadGroupSummaryAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        string groupKey,
        CancellationToken cancellationToken)
    {
        await using var command = PostgresCommands.Create(
          connection,
          transaction: null,
          $"""
          {GroupSummaryCteSql(context)}
          select
              {GroupSummaryColumnsSql}
          from summary
          where summary.group_key = @group_key;
          """);
        command.Parameters.AddWithValue("group_key", groupKey);

        var rows = await ReadGroupSummariesAsync(command, cancellationToken).ConfigureAwait(false);
        return rows.Count == 0 ? null : rows[0];
    }

    private static async ValueTask<IReadOnlyList<ConcurrencyGroupInspectionSummary>> ReadGroupSummariesAsync(
        NpgsqlCommand command,
        CancellationToken cancellationToken)
    {
        var groups = new List<ConcurrencyGroupInspectionSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var defaultLimit = reader.IsDBNull(1) ? null : (int?)reader.GetInt32(1);
            var overrideLimit = reader.IsDBNull(2) ? null : (int?)reader.GetInt32(2);
            var limit = reader.GetInt32(3);
            var occupancy = reader.GetInt32(4);
            groups.Add(new ConcurrencyGroupInspectionSummary(
              reader.GetString(0),
              limit,
              occupancy,
              Convert.ToInt32(reader.GetInt64(5), CultureInfo.InvariantCulture),
              reader.GetBoolean(6),
              reader.IsDBNull(7) ? null : PostgresConversion.ToDateTimeOffset(reader.GetValue(7)))
            {
                DefaultLimit = defaultLimit,
                OverrideLimit = overrideLimit,
                DefaultRateLimit = PostgresSql.ReadRateLimit(reader, 8, 9),
                HasRateLimitOverride = reader.GetBoolean(10),
                OverrideRateLimit = PostgresSql.ReadRateLimit(reader, 11, 12),
                EffectiveRateLimit = PostgresSql.ReadRateLimit(reader, 13, 14),
                NextRatePermitAtUtc = reader.IsDBNull(15) ? null : PostgresConversion.ToDateTimeOffset(reader.GetValue(15)),
                IsRateLimited = reader.GetBoolean(16),
                ConcurrencyBlockedJobCount = Convert.ToInt32(reader.GetInt64(17), CultureInfo.InvariantCulture),
                RateBlockedJobCount = Convert.ToInt32(reader.GetInt64(18), CultureInfo.InvariantCulture),
            });
        }

        return groups;
    }

    private static void ConfigureFilters(
        NpgsqlCommand command,
        List<string> conditions,
        ConcurrencyGroupInspectionQuery query)
    {
        if (query.GroupKey is not null)
        {
            conditions.Add("summary.group_key = @group_key");
            command.Parameters.AddWithValue("group_key", query.GroupKey);
        }

        if (query.IsSaturated is { } isSaturated)
        {
            conditions.Add("summary.is_saturated = @is_saturated");
            command.Parameters.AddWithValue("is_saturated", isSaturated);
        }

        if (query.HasBlockedJobs is { } hasBlockedJobs)
        {
            conditions.Add("(summary.blocked_count > 0) = @has_blocked_jobs");
            command.Parameters.AddWithValue("has_blocked_jobs", hasBlockedJobs);
        }

        if (query.IsRateLimited is { } isRateLimited)
        {
            conditions.Add("summary.is_rate_limited = @is_rate_limited");
            command.Parameters.AddWithValue("is_rate_limited", isRateLimited);
        }
    }

    private static string GroupSummaryCteSql(PostgresOperationContext context)
      => $"""
         with group_keys as (
             select group_key from {context.Names.ConcurrencyGroups}
             union
             select group_key from {context.Names.JobConcurrencyGroups}
         ),
         group_state as (
             select
                 group_keys.group_key,
                 concurrency_group.default_limit,
                 concurrency_group.configured_limit as override_limit,
                 coalesce(concurrency_group.effective_limit, 1) as effective_limit,
                 coalesce(concurrency_group.in_use_count, 0) as current_occupancy,
                 {PostgresSqlFragments.GroupIsSaturated("concurrency_group")} as is_saturated,
                 concurrency_group.default_rate_permit_count,
                 concurrency_group.default_rate_period,
                 coalesce(concurrency_group.rate_limit_override_enabled, false) as rate_limit_override_enabled,
                 concurrency_group.configured_rate_permit_count,
                 concurrency_group.configured_rate_period,
                 concurrency_group.effective_rate_permit_count,
                 concurrency_group.effective_rate_period,
                 concurrency_group.rate_theoretical_arrival_at_utc,
                 {PostgresSqlFragments.GroupIsRateLimited("concurrency_group", PostgresSqlFragments.TransactionNow)} as is_rate_limited,
                 concurrency_group.updated_at_utc
             from group_keys
             left join {context.Names.ConcurrencyGroups} concurrency_group on concurrency_group.group_key = group_keys.group_key
         ),
         queued as (
             select job_group.group_key, count(*) as queued_count
             from {context.Names.JobConcurrencyGroups} job_group
             join {context.Names.Jobs} job on job.job_id = job_group.job_id
             where {PostgresSqlFragments.JobIsDue("job", PostgresSqlFragments.TransactionNow)}
             group by job_group.group_key
         ),
         summary as (
             select
                 group_state.*,
                 case
                     when group_state.is_saturated or group_state.is_rate_limited then coalesce(queued.queued_count, 0)
                     else 0
                 end as blocked_count,
                 case when group_state.is_saturated then coalesce(queued.queued_count, 0) else 0 end as concurrency_blocked_count,
                 case when group_state.is_rate_limited then coalesce(queued.queued_count, 0) else 0 end as rate_blocked_count
             from group_state
             left join queued on queued.group_key = group_state.group_key
         )
         """;

    private static async ValueTask<IReadOnlyList<Guid>> ReadClaimedJobIdsAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        string groupKey,
        CancellationToken cancellationToken)
      => await ReadJobIdsAsync(
        connection,
        $"""
        select job.job_id
        from {context.Names.Jobs} job
        join {context.Names.JobConcurrencyGroups} job_group on job_group.job_id = job.job_id
        where job_group.group_key = @group_key
          and job.state = 'Claimed'
        order by job.enqueue_sequence asc;
        """,
        groupKey,
        cancellationToken)
        .ConfigureAwait(false);

    private static async ValueTask<IReadOnlyList<Guid>> ReadBlockedJobIdsAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        string groupKey,
        BlockKind blockKind,
        CancellationToken cancellationToken)
      => await ReadJobIdsAsync(
        connection,
        $"""
        select job.job_id
        from {context.Names.Jobs} job
        join {context.Names.JobConcurrencyGroups} job_group on job_group.job_id = job.job_id
        left join {context.Names.ConcurrencyGroups} concurrency_group on concurrency_group.group_key = job_group.group_key
        where job_group.group_key = @group_key
          and {PostgresSqlFragments.JobIsDue("job", PostgresSqlFragments.TransactionNow)}
          and ({CreateBlockCondition(blockKind)})
        order by job.priority desc, job.enqueue_sequence asc;
        """,
        groupKey,
        cancellationToken)
        .ConfigureAwait(false);

    private static string CreateBlockCondition(BlockKind blockKind)
      => blockKind switch
      {
          BlockKind.Concurrency => PostgresSqlFragments.GroupIsSaturated("concurrency_group"),
          BlockKind.Rate => PostgresSqlFragments.GroupIsRateLimited("concurrency_group", PostgresSqlFragments.TransactionNow),
          _ => $"{PostgresSqlFragments.GroupIsSaturated("concurrency_group")} or {PostgresSqlFragments.GroupIsRateLimited("concurrency_group", PostgresSqlFragments.TransactionNow)}",
      };

    private static async ValueTask<IReadOnlyList<Guid>> ReadJobIdsAsync(
        NpgsqlConnection connection,
        string commandText,
        string groupKey,
        CancellationToken cancellationToken)
    {
        await using var command = PostgresCommands.Create(connection, transaction: null, commandText);
        command.Parameters.AddWithValue("group_key", groupKey);

        return await PostgresSql.ReadGuidsAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateQuery(ConcurrencyGroupInspectionQuery query)
    {
        if (query.PageSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(query), query.PageSize, "Concurrency group inspection query page size must be positive.");
        }

        if (query.ContinuationToken is { Length: 0 })
        {
            throw new ArgumentException("Concurrency group inspection continuation token is invalid.", nameof(query));
        }
    }

    private enum BlockKind
    {
        Any,
        Concurrency,
        Rate,
    }
}
