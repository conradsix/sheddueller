namespace Sheddueller.Postgres.Internal.Operations;

using Npgsql;

using NpgsqlTypes;

using Sheddueller.Storage;

internal static class PostgresSchedules
{
    public static async ValueTask<PostgresScheduleDefinition?> ReadScheduleDefinitionForUpdateAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string scheduleKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
          $"""
          select
              schedule.schedule_key,
              schedule.cron_expression,
              schedule.is_paused,
              schedule.overlap_mode,
              schedule.priority,
              schedule.service_type,
              schedule.method_name,
              schedule.method_parameter_types,
              schedule.invocation_target_kind,
              schedule.method_parameter_bindings,
              schedule.serialized_arguments_content_type,
              schedule.serialized_arguments,
              schedule.retry_policy_configured,
              schedule.max_attempts,
              schedule.retry_backoff_kind,
              schedule.retry_base_delay_ms,
              schedule.retry_max_delay_ms,
              schedule.next_fire_at_utc
          from {context.Names.RecurringSchedules} schedule
          where schedule.schedule_key = @schedule_key
          for update;
          """;
        command.Parameters.AddWithValue("schedule_key", scheduleKey);

        PostgresScheduleDefinition? schedule = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                schedule = PostgresReaders.ReadScheduleDefinition(reader, [], []);
            }
        }

        if (schedule is null)
        {
            return null;
        }

        var groupKeys = await ReadScheduleGroupKeysAsync(context, connection, transaction, schedule.ScheduleKey, cancellationToken)
          .ConfigureAwait(false);
        var tags = await PostgresScheduleTags.ReadScheduleTagsAsync(context, connection, transaction, schedule.ScheduleKey, cancellationToken)
          .ConfigureAwait(false);
        return schedule with { ConcurrencyGroupKeys = groupKeys, Tags = tags };
    }

    public static async ValueTask InsertScheduleAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        UpsertRecurringScheduleRequest request,
        PostgresRetryPolicy retry,
        DateTimeOffset nextFireAtUtc,
        CancellationToken cancellationToken)
    {
        await PostgresOperationContext.ExecuteCountAsync(
          connection,
          transaction,
          $"""
          insert into {context.Names.RecurringSchedules} (
              schedule_key,
              cron_expression,
              is_paused,
              overlap_mode,
              priority,
              service_type,
              method_name,
              method_parameter_types,
              invocation_target_kind,
              method_parameter_bindings,
              serialized_arguments_content_type,
              serialized_arguments,
              retry_policy_configured,
              max_attempts,
              retry_backoff_kind,
              retry_base_delay_ms,
              retry_max_delay_ms,
              next_fire_at_utc,
              created_at_utc,
              updated_at_utc)
          values (
              @schedule_key,
              @cron_expression,
              @is_paused,
              @overlap_mode,
              @priority,
              @service_type,
              @method_name,
              @method_parameter_types,
              @invocation_target_kind,
              @method_parameter_bindings,
              @serialized_arguments_content_type,
              @serialized_arguments,
              @retry_policy_configured,
              @max_attempts,
              @retry_backoff_kind,
              @retry_base_delay_ms,
              @retry_max_delay_ms,
              @next_fire_at_utc,
              transaction_timestamp(),
              transaction_timestamp());
          """,
          command => AddScheduleParameters(command, request, retry, isPaused: false, nextFireAtUtc),
          cancellationToken)
          .ConfigureAwait(false);
        await ReplaceScheduleGroupsAsync(context, connection, transaction, request.ScheduleKey, request.ConcurrencyGroupKeys, cancellationToken)
          .ConfigureAwait(false);
        await PostgresScheduleTags.ReplaceScheduleTagsAsync(context, connection, transaction, request.ScheduleKey, request.Tags, cancellationToken)
          .ConfigureAwait(false);
    }

    public static async ValueTask UpdateScheduleAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        UpsertRecurringScheduleRequest request,
        PostgresRetryPolicy retry,
        bool isPaused,
        DateTimeOffset? nextFireAtUtc,
        CancellationToken cancellationToken)
    {
        await PostgresOperationContext.ExecuteCountAsync(
          connection,
          transaction,
          $"""
          update {context.Names.RecurringSchedules}
          set cron_expression = @cron_expression,
              is_paused = @is_paused,
              overlap_mode = @overlap_mode,
              priority = @priority,
              service_type = @service_type,
              method_name = @method_name,
              method_parameter_types = @method_parameter_types,
              invocation_target_kind = @invocation_target_kind,
              method_parameter_bindings = @method_parameter_bindings,
              serialized_arguments_content_type = @serialized_arguments_content_type,
              serialized_arguments = @serialized_arguments,
              retry_policy_configured = @retry_policy_configured,
              max_attempts = @max_attempts,
              retry_backoff_kind = @retry_backoff_kind,
              retry_base_delay_ms = @retry_base_delay_ms,
              retry_max_delay_ms = @retry_max_delay_ms,
              next_fire_at_utc = @next_fire_at_utc,
              updated_at_utc = transaction_timestamp()
          where schedule_key = @schedule_key;
          """,
          command => AddScheduleParameters(command, request, retry, isPaused, nextFireAtUtc),
          cancellationToken)
          .ConfigureAwait(false);
        await PostgresOperationContext.ExecuteCountAsync(
          connection,
          transaction,
          $"delete from {context.Names.ScheduleConcurrencyGroups} where schedule_key = @schedule_key;",
          command => command.Parameters.AddWithValue("schedule_key", request.ScheduleKey),
          cancellationToken)
          .ConfigureAwait(false);
        await ReplaceScheduleGroupsAsync(context, connection, transaction, request.ScheduleKey, request.ConcurrencyGroupKeys, cancellationToken)
          .ConfigureAwait(false);
        await PostgresScheduleTags.ReplaceScheduleTagsAsync(context, connection, transaction, request.ScheduleKey, request.Tags, cancellationToken)
          .ConfigureAwait(false);
    }

    public static async ValueTask<IReadOnlyList<RecurringScheduleInfo>> ReadSchedulesAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        string whereClause,
        Action<NpgsqlCommand> configure,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
          $"""
          select
              schedule.schedule_key,
              schedule.cron_expression,
              schedule.is_paused,
              schedule.overlap_mode,
              schedule.priority,
              schedule.retry_policy_configured,
              schedule.max_attempts,
              schedule.retry_backoff_kind,
              schedule.retry_base_delay_ms,
              schedule.retry_max_delay_ms,
              schedule.next_fire_at_utc,
              coalesce(array_agg(schedule_group.group_key order by schedule_group.group_key) filter (where schedule_group.group_key is not null), array[]::text[]) as group_keys
          from {context.Names.RecurringSchedules} schedule
          left join {context.Names.ScheduleConcurrencyGroups} schedule_group on schedule_group.schedule_key = schedule.schedule_key
          {whereClause}
          group by schedule.schedule_key
          order by schedule.schedule_key asc;
          """;
        configure(command);

        var schedules = new List<RecurringScheduleInfo>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            schedules.Add(new RecurringScheduleInfo(
              reader.GetString(0),
              reader.GetString(1),
              reader.GetBoolean(2),
              PostgresConversion.ToRecurringOverlapMode(reader.GetValue(3)),
              reader.GetInt32(4),
              reader.GetFieldValue<string[]>(11),
              PostgresConversion.ToRetryPolicy(
                reader.GetBoolean(5),
                reader.GetInt32(6),
                reader.GetValue(7),
                reader.GetValue(8),
                reader.GetValue(9)),
              reader.IsDBNull(10) ? null : PostgresConversion.ToDateTimeOffset(reader.GetValue(10))));
        }

        await reader.DisposeAsync().ConfigureAwait(false);

        for (var i = 0; i < schedules.Count; i++)
        {
            var tags = await PostgresScheduleTags.ReadScheduleTagsAsync(context, connection, transaction: null, schedules[i].ScheduleKey, cancellationToken)
              .ConfigureAwait(false);
            schedules[i] = schedules[i] with { Tags = tags };
        }

        return schedules;
    }

    public static async ValueTask<IReadOnlyList<PostgresScheduleDefinition>> ReadDueSchedulesAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
          $"""
          select
              schedule.schedule_key,
              schedule.cron_expression,
              schedule.is_paused,
              schedule.overlap_mode,
              schedule.priority,
              schedule.service_type,
              schedule.method_name,
              schedule.method_parameter_types,
              schedule.invocation_target_kind,
              schedule.method_parameter_bindings,
              schedule.serialized_arguments_content_type,
              schedule.serialized_arguments,
              schedule.retry_policy_configured,
              schedule.max_attempts,
              schedule.retry_backoff_kind,
              schedule.retry_base_delay_ms,
              schedule.retry_max_delay_ms,
              schedule.next_fire_at_utc
          from {context.Names.RecurringSchedules} schedule
          where schedule.is_paused = false
            and schedule.next_fire_at_utc <= transaction_timestamp()
          order by schedule.next_fire_at_utc asc, schedule.schedule_key asc
          for update skip locked;
          """;

        var schedules = new List<PostgresScheduleDefinition>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                schedules.Add(PostgresReaders.ReadScheduleDefinition(reader, [], []));
            }
        }

        for (var i = 0; i < schedules.Count; i++)
        {
            var groupKeys = await ReadScheduleGroupKeysAsync(context, connection, transaction, schedules[i].ScheduleKey, cancellationToken)
              .ConfigureAwait(false);
            var tags = await PostgresScheduleTags.ReadScheduleTagsAsync(context, connection, transaction, schedules[i].ScheduleKey, cancellationToken)
              .ConfigureAwait(false);
            schedules[i] = schedules[i] with { ConcurrencyGroupKeys = groupKeys, Tags = tags };
        }

        return schedules;
    }

    public static async ValueTask<bool> HasNonTerminalOccurrenceAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string scheduleKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
          $"""
          select exists (
              select 1
              from {context.Names.Jobs}
              where source_schedule_key = @schedule_key
                and state in ('Queued', 'Claimed'));
          """;
        command.Parameters.AddWithValue("schedule_key", scheduleKey);

        return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? false);
    }

    public static async ValueTask<long> InsertMaterializedJobAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PostgresScheduleDefinition schedule,
        PostgresRetryPolicy retry,
        Guid jobId,
        DateTimeOffset? scheduledFireAtUtc,
        ScheduleOccurrenceKind occurrenceKind,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
          $"""
          insert into {context.Names.Jobs} (
              job_id,
              state,
              priority,
              enqueued_at_utc,
              service_type,
              method_name,
              method_parameter_types,
              invocation_target_kind,
              method_parameter_bindings,
              serialized_arguments_content_type,
              serialized_arguments,
              attempt_count,
              max_attempts,
              retry_backoff_kind,
              retry_base_delay_ms,
              retry_max_delay_ms,
              source_schedule_key,
              scheduled_fire_at_utc,
              schedule_occurrence_kind)
          values (
              @job_id,
              'Queued',
              @priority,
              transaction_timestamp(),
              @service_type,
              @method_name,
              @method_parameter_types,
              @invocation_target_kind,
              @method_parameter_bindings,
              @serialized_arguments_content_type,
              @serialized_arguments,
              0,
              @max_attempts,
              @retry_backoff_kind,
              @retry_base_delay_ms,
              @retry_max_delay_ms,
              @source_schedule_key,
              @scheduled_fire_at_utc,
              @schedule_occurrence_kind)
          returning enqueue_sequence;
          """;
        command.Parameters.AddWithValue("job_id", jobId);
        command.Parameters.AddWithValue("priority", schedule.Priority);
        command.Parameters.AddWithValue("service_type", schedule.ServiceType);
        command.Parameters.AddWithValue("method_name", schedule.MethodName);
        command.Parameters.AddWithValue("method_parameter_types", schedule.MethodParameterTypes.ToArray());
        command.Parameters.AddWithValue("invocation_target_kind", PostgresConversion.ToText(schedule.InvocationTargetKind));
        command.Parameters.AddWithValue(
          "method_parameter_bindings",
          NpgsqlDbType.Array | NpgsqlDbType.Text,
          PostgresOperationContext.ToDbValue(schedule.MethodParameterBindings?.Select(PostgresConversion.ToText).ToArray()));
        command.Parameters.AddWithValue("serialized_arguments_content_type", schedule.SerializedArguments.ContentType);
        command.Parameters.AddWithValue("serialized_arguments", schedule.SerializedArguments.Data);
        command.Parameters.AddWithValue("max_attempts", retry.MaxAttempts);
        command.Parameters.AddWithValue("retry_backoff_kind", PostgresOperationContext.ToDbValue(PostgresConversion.ToText(retry.BackoffKind)));
        command.Parameters.AddWithValue("retry_base_delay_ms", PostgresOperationContext.ToDbValue(PostgresConversion.ToMilliseconds(retry.BaseDelay)));
        command.Parameters.AddWithValue("retry_max_delay_ms", PostgresOperationContext.ToDbValue(PostgresConversion.ToMilliseconds(retry.MaxDelay)));
        command.Parameters.AddWithValue("source_schedule_key", schedule.ScheduleKey);
        command.Parameters.AddWithValue("scheduled_fire_at_utc", PostgresOperationContext.ToDbValue(scheduledFireAtUtc));
        command.Parameters.AddWithValue("schedule_occurrence_kind", PostgresConversion.ToText(occurrenceKind));

        var enqueueSequence = (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
          ?? throw new InvalidOperationException("PostgreSQL did not return an enqueue sequence for the materialized job."));

        await PostgresJobGroups.ReplaceJobGroupsAsync(context, connection, transaction, jobId, schedule.ConcurrencyGroupKeys, cancellationToken)
          .ConfigureAwait(false);
        await PostgresJobTags.ReplaceJobTagsAsync(context, connection, transaction, jobId, schedule.Tags, cancellationToken)
          .ConfigureAwait(false);
        await PostgresJobEvents.AppendAndNotifyInTransactionAsync(
          context,
          connection,
          transaction,
          new AppendJobEventRequest(jobId, JobEventKind.Lifecycle, AttemptNumber: 0, Message: PostgresJobEventMessages.Queued),
          cancellationToken)
          .ConfigureAwait(false);

        return enqueueSequence;
    }

    private static async ValueTask ReplaceScheduleGroupsAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string scheduleKey,
        IReadOnlyList<string> groupKeys,
        CancellationToken cancellationToken)
    {
        foreach (var groupKey in groupKeys.Distinct(StringComparer.Ordinal))
        {
            await PostgresOperationContext.ExecuteCountAsync(
              connection,
              transaction,
              $"""
              insert into {context.Names.ScheduleConcurrencyGroups} (schedule_key, group_key)
              values (@schedule_key, @group_key)
              on conflict (schedule_key, group_key) do nothing;
              """,
              command =>
              {
                  command.Parameters.AddWithValue("schedule_key", scheduleKey);
                  command.Parameters.AddWithValue("group_key", groupKey);
              },
              cancellationToken)
              .ConfigureAwait(false);
        }
    }

    private static async ValueTask<IReadOnlyList<string>> ReadScheduleGroupKeysAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string scheduleKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
          $"""
          select group_key
          from {context.Names.ScheduleConcurrencyGroups}
          where schedule_key = @schedule_key
          order by group_key asc;
          """;
        command.Parameters.AddWithValue("schedule_key", scheduleKey);

        var groupKeys = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            groupKeys.Add(reader.GetString(0));
        }

        return groupKeys;
    }

    private static void AddScheduleParameters(
        NpgsqlCommand command,
        UpsertRecurringScheduleRequest request,
        PostgresRetryPolicy retry,
        bool isPaused,
        DateTimeOffset? nextFireAtUtc)
    {
        command.Parameters.AddWithValue("schedule_key", request.ScheduleKey);
        command.Parameters.AddWithValue("cron_expression", request.CronExpression);
        command.Parameters.AddWithValue("is_paused", isPaused);
        command.Parameters.AddWithValue("overlap_mode", PostgresConversion.ToText(request.OverlapMode));
        command.Parameters.AddWithValue("priority", request.Priority);
        command.Parameters.AddWithValue("service_type", request.ServiceType);
        command.Parameters.AddWithValue("method_name", request.MethodName);
        command.Parameters.AddWithValue("method_parameter_types", request.MethodParameterTypes.ToArray());
        command.Parameters.AddWithValue("invocation_target_kind", PostgresConversion.ToText(request.InvocationTargetKind));
        command.Parameters.AddWithValue(
          "method_parameter_bindings",
          NpgsqlDbType.Array | NpgsqlDbType.Text,
          PostgresOperationContext.ToDbValue(request.MethodParameterBindings?.Select(PostgresConversion.ToText).ToArray()));
        command.Parameters.AddWithValue("serialized_arguments_content_type", request.SerializedArguments.ContentType);
        command.Parameters.AddWithValue("serialized_arguments", request.SerializedArguments.Data);
        command.Parameters.AddWithValue("retry_policy_configured", retry.IsConfigured);
        command.Parameters.AddWithValue("max_attempts", retry.MaxAttempts);
        command.Parameters.AddWithValue("retry_backoff_kind", PostgresOperationContext.ToDbValue(PostgresConversion.ToText(retry.BackoffKind)));
        command.Parameters.AddWithValue("retry_base_delay_ms", PostgresOperationContext.ToDbValue(PostgresConversion.ToMilliseconds(retry.BaseDelay)));
        command.Parameters.AddWithValue("retry_max_delay_ms", PostgresOperationContext.ToDbValue(PostgresConversion.ToMilliseconds(retry.MaxDelay)));
        command.Parameters.AddWithValue("next_fire_at_utc", PostgresOperationContext.ToDbValue(nextFireAtUtc));
    }
}
