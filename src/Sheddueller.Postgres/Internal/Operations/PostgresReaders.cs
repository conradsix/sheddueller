namespace Sheddueller.Postgres.Internal.Operations;

using Npgsql;

using Sheddueller.Storage;

/// <summary>
/// Row mappers shared by several queries, each next to the column list it reads.
/// </summary>
internal static class PostgresReaders
{
    /// <summary>
    /// The columns that <see cref="ReadClaimedJob" /> reads, in ordinal order, unqualified for a <c>returning</c>
    /// clause.
    /// </summary>
    public const string ClaimedJobColumnsSql =
      """
      job_id,
      enqueue_sequence,
      priority,
      service_type,
      method_name,
      method_parameter_types,
      invocation_target_kind,
      method_parameter_bindings,
      serialized_arguments_content_type,
      serialized_arguments,
      attempt_count,
      max_attempts,
      lease_token,
      lease_expires_at_utc,
      retry_backoff_kind,
      retry_base_delay_ms,
      retry_max_delay_ms,
      source_schedule_key,
      scheduled_fire_at_utc
      """;

    /// <summary>
    /// The columns that <see cref="ReadPostgresClaimedJob" /> reads, in ordinal order, from the jobs table aliased
    /// <c>job</c>.
    /// </summary>
    public static string PostgresClaimedJobColumnsSql(PostgresNames names)
      => $"""
         job.job_id,
         job.attempt_count,
         job.max_attempts,
         job.retry_backoff_kind,
         job.retry_base_delay_ms,
         job.retry_max_delay_ms,
         job.idempotency_key,
         {PostgresJobGroups.GroupKeysSql(names, "job")}
         """;

    /// <summary>
    /// The columns that <see cref="ReadScheduleDefinition" /> reads, in ordinal order, from the recurring schedules
    /// table aliased <c>schedule</c>.
    /// </summary>
    public const string ScheduleDefinitionColumnsSql =
      """
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
      """;

    public static ClaimedJob ReadClaimedJob(NpgsqlDataReader reader, IReadOnlyList<string> groupKeys)
      => new(
        reader.GetGuid(0),
        reader.GetInt64(1),
        reader.GetInt32(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetFieldValue<string[]>(5),
        PostgresConversion.ToPayload(reader.GetValue(8), reader.GetValue(9)),
        groupKeys,
        reader.GetInt32(10),
        reader.GetInt32(11),
        reader.GetGuid(12),
        PostgresConversion.ToDateTimeOffset(reader.GetValue(13)),
        PostgresConversion.ToRetryBackoffKind(reader.GetValue(14)),
        PostgresConversion.FromMilliseconds(reader.GetValue(15)),
        PostgresConversion.FromMilliseconds(reader.GetValue(16)),
        reader.IsDBNull(17) ? null : reader.GetString(17),
        reader.IsDBNull(18) ? null : PostgresConversion.ToDateTimeOffset(reader.GetValue(18)),
        PostgresConversion.ToInvocationTargetKind(reader.GetValue(6)),
        PostgresConversion.ToParameterBindings(reader.GetValue(7)));

    public static PostgresClaimedJob ReadPostgresClaimedJob(NpgsqlDataReader reader)
      => new(
        reader.GetGuid(0),
        reader.GetInt32(1),
        reader.GetInt32(2),
        PostgresConversion.ToRetryBackoffKind(reader.GetValue(3)),
        PostgresConversion.FromMilliseconds(reader.GetValue(4)),
        PostgresConversion.FromMilliseconds(reader.GetValue(5)),
        reader.IsDBNull(6) ? null : reader.GetString(6),
        reader.GetFieldValue<string[]>(7));

    public static PostgresScheduleDefinition ReadScheduleDefinition(
        NpgsqlDataReader reader,
        IReadOnlyList<string> groupKeys,
        IReadOnlyList<JobTag> tags)
      => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetBoolean(2),
        PostgresConversion.ToRecurringOverlapMode(reader.GetValue(3)),
        reader.GetInt32(4),
        reader.GetString(5),
        reader.GetString(6),
        reader.GetFieldValue<string[]>(7),
        PostgresConversion.ToInvocationTargetKind(reader.GetValue(8)),
        PostgresConversion.ToParameterBindings(reader.GetValue(9)),
        PostgresConversion.ToPayload(reader.GetValue(10), reader.GetValue(11)),
        groupKeys,
        tags,
        PostgresConversion.ToRetryPolicy(
          reader.GetBoolean(12),
          reader.GetInt32(13),
          reader.GetValue(14),
          reader.GetValue(15),
          reader.GetValue(16)),
        reader.IsDBNull(17) ? null : PostgresConversion.ToDateTimeOffset(reader.GetValue(17)));
}
