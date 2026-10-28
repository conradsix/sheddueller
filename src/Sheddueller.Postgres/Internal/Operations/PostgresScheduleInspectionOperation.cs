namespace Sheddueller.Postgres.Internal.Operations;

using System.Globalization;

using Npgsql;

using Sheddueller.Inspection.Schedules;
using Sheddueller.Storage;

internal static class PostgresScheduleInspectionOperation
{
    public static async ValueTask<ScheduleInspectionPage> SearchSchedulesAsync(
        PostgresOperationContext context,
        ScheduleInspectionQuery query,
        CancellationToken cancellationToken)
    {
        ValidateQuery(query);
        await using var connection = await context.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await PostgresOperationContext.BeginReadSnapshotAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var countCommand = connection.CreateCommand();
        var countConditions = new List<string>();
        ConfigureFilters(context, countCommand, countConditions, query);
        countCommand.CommandText =
          $"""
          select count(*)
          from {context.Names.RecurringSchedules} schedule
          {PostgresSql.WhereClause(countConditions)};
          """;
        var totalCount = Convert.ToInt64(await countCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);

        await using var command = connection.CreateCommand();
        var conditions = new List<string>();
        ConfigureFilters(context, command, conditions, query);
        if (query.ContinuationToken is not null)
        {
            conditions.Add("schedule.schedule_key > @after_schedule_key");
            command.Parameters.AddWithValue("after_schedule_key", query.ContinuationToken);
        }

        command.Parameters.AddWithValue("limit", query.PageSize + 1);
        command.CommandText =
          $"""
          {ScheduleSelectSql(context)}
          {CreateWhereClause(conditions)}
          order by schedule.schedule_key asc
          limit @limit;
          """;

        var rows = await ReadScheduleRowsAsync(context, connection, command, cancellationToken).ConfigureAwait(false);
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
        await using var command = connection.CreateCommand();
        command.CommandText =
          $"""
          {ScheduleSelectSql(context)}
          where schedule.schedule_key = @schedule_key
          group by schedule.schedule_key;
          """;
        command.Parameters.AddWithValue("schedule_key", scheduleKey);

        var rows = await ReadScheduleRowsAsync(context, connection, command, cancellationToken).ConfigureAwait(false);
        if (rows.Count == 0)
        {
            return null;
        }

        var occurrences = await ReadOccurrencesAsync(context, connection, scheduleKey, cancellationToken).ConfigureAwait(false);
        var retryPolicy = await ReadRetryPolicyAsync(context, connection, scheduleKey, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new ScheduleInspectionDetail(
          rows[0],
          retryPolicy,
          [.. occurrences.Take(20)],
          occurrences.FirstOrDefault(occurrence => occurrence.State == JobState.Completed),
          occurrences.FirstOrDefault(occurrence => occurrence.State == JobState.Failed));
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

    private static async ValueTask<IReadOnlyList<ScheduleInspectionSummary>> ReadScheduleRowsAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        NpgsqlCommand command,
        CancellationToken cancellationToken)
    {
        var rows = new List<ScheduleInspectionSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new ScheduleInspectionSummary(
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
              PostgresConversion.ToDateTimeOffset(reader.GetValue(9))));
        }

        await reader.DisposeAsync().ConfigureAwait(false);
        for (var i = 0; i < rows.Count; i++)
        {
            var tags = await PostgresScheduleTags.ReadScheduleTagsAsync(context, connection, transaction: null, rows[i].ScheduleKey, cancellationToken)
              .ConfigureAwait(false);
            rows[i] = rows[i] with { Tags = tags };
        }

        return rows;
    }

    private static async ValueTask<IReadOnlyList<ScheduleInspectionOccurrence>> ReadOccurrencesAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        string scheduleKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
          $"""
          select
              job_id,
              scheduled_fire_at_utc,
              coalesce(schedule_occurrence_kind, 'Automatic'),
              state,
              enqueued_at_utc,
              completed_at_utc,
              failed_at_utc
          from {context.Names.Jobs}
          where source_schedule_key = @schedule_key
          order by enqueue_sequence desc;
          """;
        command.Parameters.AddWithValue("schedule_key", scheduleKey);

        var occurrences = new List<ScheduleInspectionOccurrence>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            occurrences.Add(new ScheduleInspectionOccurrence(
              reader.GetGuid(0),
              reader.IsDBNull(1) ? null : PostgresConversion.ToDateTimeOffset(reader.GetValue(1)),
              Enum.Parse<ScheduleOccurrenceKind>(reader.GetString(2)),
              PostgresConversion.ToJobState(reader.GetValue(3)),
              PostgresConversion.ToDateTimeOffset(reader.GetValue(4)),
              reader.IsDBNull(5) ? null : PostgresConversion.ToDateTimeOffset(reader.GetValue(5)),
              reader.IsDBNull(6) ? null : PostgresConversion.ToDateTimeOffset(reader.GetValue(6))));
        }

        return occurrences;
    }

    private static async ValueTask<RetryPolicy?> ReadRetryPolicyAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        string scheduleKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
          $"""
          select retry_policy_configured, max_attempts, retry_backoff_kind, retry_base_delay_ms, retry_max_delay_ms
          from {context.Names.RecurringSchedules}
          where schedule_key = @schedule_key;
          """;
        command.Parameters.AddWithValue("schedule_key", scheduleKey);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return PostgresConversion.ToRetryPolicy(
          reader.GetBoolean(0),
          reader.GetInt32(1),
          reader.GetValue(2),
          reader.GetValue(3),
          reader.GetValue(4));
    }

    private static string ScheduleSelectSql(PostgresOperationContext context)
      => $"""
         select
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
         from {context.Names.RecurringSchedules} schedule
         left join {context.Names.ScheduleConcurrencyGroups} schedule_group on schedule_group.schedule_key = schedule.schedule_key
         """;

    private static string CreateWhereClause(List<string> conditions)
      => $"{PostgresSql.WhereClause(conditions)} group by schedule.schedule_key";

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
