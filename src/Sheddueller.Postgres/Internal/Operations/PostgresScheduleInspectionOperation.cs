namespace Sheddueller.Postgres.Internal.Operations;

using System.Globalization;

using Npgsql;

using Sheddueller.Inspection.Schedules;
using Sheddueller.Storage;

internal static class PostgresScheduleInspectionOperation
{
    /// <summary>
    /// How many of a schedule's most recent occurrences its detail lists.
    /// </summary>
    private const int RecentOccurrenceLimit = 20;

    /// <summary>
    /// The columns that <see cref="ReadSummary" /> reads, in ordinal order, from the recurring schedules table aliased
    /// <c>schedule</c> left joined to its groups aliased <c>schedule_group</c>, grouped by schedule.
    /// </summary>
    private const string SummaryColumnsSql =
      """
      schedule.schedule_key,
      schedule.service_type,
      schedule.method_name,
      schedule.cron_expression,
      schedule.is_paused,
      schedule.overlap_mode,
      schedule.next_fire_at_utc,
      schedule.priority,
      coalesce(array_agg(schedule_group.group_key order by schedule_group.group_key) filter (where schedule_group.group_key is not null), array[]::text[]) as group_keys,
      schedule.updated_at_utc
      """;

    /// <summary>
    /// The retry policy columns that the detail read selects after <see cref="SummaryColumnsSql" />, in ordinal order.
    /// </summary>
    private const string RetryPolicyColumnsSql =
      """
      schedule.retry_policy_configured,
      schedule.max_attempts,
      schedule.retry_backoff_kind,
      schedule.retry_base_delay_ms,
      schedule.retry_max_delay_ms
      """;

    /// <summary>
    /// The columns that <see cref="ReadOccurrence" /> reads, in ordinal order, from the jobs table.
    /// </summary>
    private const string OccurrenceColumnsSql =
      """
      job_id,
      scheduled_fire_at_utc,
      coalesce(schedule_occurrence_kind, 'Automatic'),
      state,
      enqueued_at_utc,
      completed_at_utc,
      failed_at_utc
      """;

    public static async ValueTask<ScheduleInspectionPage> SearchSchedulesAsync(
        PostgresOperationContext context,
        ScheduleInspectionQuery query,
        CancellationToken cancellationToken)
    {
        ValidateQuery(query);
        await using var connection = await context.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await PostgresOperationContext.BeginReadSnapshotAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var countCommand = PostgresCommands.Create(
          connection,
          transaction: null,
          built =>
          {
              var conditions = new List<string>();
              ConfigureFilters(context, built, conditions, query);
              return $"""
                select count(*)
                from {context.Names.RecurringSchedules} schedule
                {PostgresSql.WhereClause(conditions)};
                """;
          });
        var totalCount = Convert.ToInt64(await countCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);

        await using var command = PostgresCommands.Create(
          connection,
          transaction: null,
          built =>
          {
              var conditions = new List<string>();
              ConfigureFilters(context, built, conditions, query);
              if (query.ContinuationToken is not null)
              {
                  conditions.Add("schedule.schedule_key > @after_schedule_key");
                  built.Parameters.AddWithValue("after_schedule_key", query.ContinuationToken);
              }

              built.Parameters.AddWithValue("limit", query.PageSize + 1);
              return $"""
                select
                    {SummaryColumnsSql}
                {ScheduleFromSql(context)}
                {PostgresSql.WhereClause(conditions)}
                group by schedule.schedule_key
                order by schedule.schedule_key asc
                limit @limit;
                """;
          });

        var rows = new List<ScheduleInspectionSummary>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(ReadSummary(reader));
            }
        }

        await AttachTagsAsync(context, connection, rows, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        var pageRows = rows.Take(query.PageSize).ToArray();
        return new ScheduleInspectionPage(
          pageRows,
          rows.Count > query.PageSize ? pageRows[^1].ScheduleKey : null,
          totalCount);
    }

    public static async ValueTask<ScheduleInspectionDetail?> GetScheduleAsync(
        PostgresOperationContext context,
        string scheduleKey,
        CancellationToken cancellationToken)
    {
        await using var connection = await context.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await PostgresOperationContext.BeginReadSnapshotAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var command = PostgresCommands.Create(
          connection,
          transaction: null,
          $"""
          select
              {SummaryColumnsSql},
              {RetryPolicyColumnsSql}
          {ScheduleFromSql(context)}
          where schedule.schedule_key = @schedule_key
          group by schedule.schedule_key;
          """);
        command.Parameters.AddWithValue("schedule_key", scheduleKey);

        ScheduleInspectionSummary summary;
        RetryPolicy? retryPolicy;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            summary = ReadSummary(reader);
            retryPolicy = PostgresConversion.ToRetryPolicy(
              reader.GetBoolean(10),
              reader.GetInt32(11),
              reader.GetValue(12),
              reader.GetValue(13),
              reader.GetValue(14));
        }

        List<ScheduleInspectionSummary> rows = [summary];
        await AttachTagsAsync(context, connection, rows, cancellationToken).ConfigureAwait(false);
        var (recent, lastCompleted, lastFailed) = await ReadOccurrencesAsync(context, connection, scheduleKey, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new ScheduleInspectionDetail(rows[0], retryPolicy, recent, lastCompleted, lastFailed);
    }

    private static void ConfigureFilters(
        PostgresOperationContext context,
        NpgsqlCommand command,
        List<string> conditions,
        ScheduleInspectionQuery query)
    {
        if (!string.IsNullOrWhiteSpace(query.ScheduleKey))
        {
            conditions.Add("strpos(lower(schedule.schedule_key), lower(@schedule_key)) > 0");
            command.Parameters.AddWithValue("schedule_key", query.ScheduleKey.Trim());
        }

        if (query.IsPaused is { } isPaused)
        {
            conditions.Add("schedule.is_paused = @is_paused");
            command.Parameters.AddWithValue("is_paused", isPaused);
        }

        if (query.ServiceType is not null)
        {
            conditions.Add("schedule.service_type = @service_type");
            command.Parameters.AddWithValue("service_type", query.ServiceType);
        }

        if (query.MethodName is not null)
        {
            conditions.Add("schedule.method_name = @method_name");
            command.Parameters.AddWithValue("method_name", query.MethodName);
        }

        if (query.Tag is { } tag)
        {
            conditions.Add($"exists (select 1 from {context.Names.ScheduleTags} tag where tag.schedule_key = schedule.schedule_key and tag.name = @tag_name and tag.value = @tag_value)");
            command.Parameters.AddWithValue("tag_name", tag.Name.Trim());
            command.Parameters.AddWithValue("tag_value", tag.Value.Trim());
        }
    }

    private static ScheduleInspectionSummary ReadSummary(NpgsqlDataReader reader)
      => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetBoolean(4),
        PostgresConversion.ToRecurringOverlapMode(reader.GetValue(5)),
        reader.IsDBNull(6) ? null : PostgresConversion.ToDateTimeOffset(reader.GetValue(6)),
        reader.GetInt32(7),
        reader.GetFieldValue<string[]>(8),
        [],
        PostgresConversion.ToDateTimeOffset(reader.GetValue(9)));

    private static async ValueTask AttachTagsAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        List<ScheduleInspectionSummary> rows,
        CancellationToken cancellationToken)
    {
        var tagsByKey = await PostgresScheduleTags.ReadScheduleTagsByKeyAsync(
          context,
          connection,
          transaction: null,
          [.. rows.Select(static row => row.ScheduleKey)],
          cancellationToken)
          .ConfigureAwait(false);
        for (var i = 0; i < rows.Count; i++)
        {
            rows[i] = rows[i] with { Tags = tagsByKey.TryGetValue(rows[i].ScheduleKey, out var tags) ? tags : [] };
        }
    }

    /// <summary>
    /// Reads the schedule's most recent occurrences, newest first, and its most recent completed and failed
    /// occurrences, however old, with one query.
    /// </summary>
    private static async ValueTask<(IReadOnlyList<ScheduleInspectionOccurrence> Recent, ScheduleInspectionOccurrence? LastCompleted, ScheduleInspectionOccurrence? LastFailed)> ReadOccurrencesAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        string scheduleKey,
        CancellationToken cancellationToken)
    {
        // The values of the query's part column.
        const int Recent = 0;
        const int LastCompleted = 1;
        const int LastFailed = 2;

        await using var command = PostgresCommands.Create(
          connection,
          transaction: null,
          $"""
          select occurrence.*
          from (
              (
                  select 0 as part, enqueue_sequence, {OccurrenceColumnsSql}
                  from {context.Names.Jobs}
                  where source_schedule_key = @schedule_key
                  order by enqueue_sequence desc
                  limit @recent_limit
              )
              union all
              (
                  select 1, enqueue_sequence, {OccurrenceColumnsSql}
                  from {context.Names.Jobs}
                  where source_schedule_key = @schedule_key
                    and state = 'Completed'
                  order by enqueue_sequence desc
                  limit 1
              )
              union all
              (
                  select 2, enqueue_sequence, {OccurrenceColumnsSql}
                  from {context.Names.Jobs}
                  where source_schedule_key = @schedule_key
                    and state = 'Failed'
                  order by enqueue_sequence desc
                  limit 1
              )
          ) occurrence
          order by occurrence.part asc, occurrence.enqueue_sequence desc;
          """);
        command.Parameters.AddWithValue("schedule_key", scheduleKey);
        command.Parameters.AddWithValue("recent_limit", RecentOccurrenceLimit);

        var recent = new List<ScheduleInspectionOccurrence>();
        ScheduleInspectionOccurrence? lastCompleted = null;
        ScheduleInspectionOccurrence? lastFailed = null;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var occurrence = ReadOccurrence(reader, ordinal: 2);
            switch (reader.GetInt32(0))
            {
                case Recent:
                    recent.Add(occurrence);
                    break;

                case LastCompleted:
                    lastCompleted = occurrence;
                    break;

                case LastFailed:
                    lastFailed = occurrence;
                    break;
            }
        }

        return (recent, lastCompleted, lastFailed);
    }

    private static ScheduleInspectionOccurrence ReadOccurrence(NpgsqlDataReader reader, int ordinal)
      => new(
        reader.GetGuid(ordinal),
        reader.IsDBNull(ordinal + 1) ? null : PostgresConversion.ToDateTimeOffset(reader.GetValue(ordinal + 1)),
        Enum.Parse<ScheduleOccurrenceKind>(reader.GetString(ordinal + 2)),
        PostgresConversion.ToJobState(reader.GetValue(ordinal + 3)),
        PostgresConversion.ToDateTimeOffset(reader.GetValue(ordinal + 4)),
        reader.IsDBNull(ordinal + 5) ? null : PostgresConversion.ToDateTimeOffset(reader.GetValue(ordinal + 5)),
        reader.IsDBNull(ordinal + 6) ? null : PostgresConversion.ToDateTimeOffset(reader.GetValue(ordinal + 6)));

    private static string ScheduleFromSql(PostgresOperationContext context)
      => $"""
         from {context.Names.RecurringSchedules} schedule
         left join {context.Names.ScheduleConcurrencyGroups} schedule_group on schedule_group.schedule_key = schedule.schedule_key
         """;

    private static void ValidateQuery(ScheduleInspectionQuery query)
    {
        if (query.PageSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(query), query.PageSize, "Schedule inspection query page size must be positive.");
        }

        if (query.ContinuationToken is { Length: 0 })
        {
            throw new ArgumentException("Schedule inspection continuation token is invalid.", nameof(query));
        }

        if (query.Tag is { } tag && (tag.Name.Trim().Length == 0 || tag.Value.Trim().Length == 0))
        {
            throw new ArgumentException("Inspection tag filters must have non-empty name and value.", nameof(query));
        }
    }
}
