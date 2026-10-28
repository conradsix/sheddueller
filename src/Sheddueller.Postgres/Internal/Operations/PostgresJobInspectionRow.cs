namespace Sheddueller.Postgres.Internal.Operations;

using Npgsql;

using Sheddueller.Storage;

/// <summary>
/// The job columns that job inspection reads for a summary.
/// </summary>
internal sealed record PostgresJobInspectionRow(
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
    ScheduleOccurrenceKind? ScheduleOccurrenceKind)
{
    /// <summary>
    /// The columns that <see cref="Read" /> reads, in ordinal order, from the jobs table aliased <c>job</c>.
    /// </summary>
    public const string ColumnsSql =
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

    /// <summary>
    /// The number of columns in <see cref="ColumnsSql" />: a query that selects more columns after them reads those
    /// from this ordinal on.
    /// </summary>
    public const int ColumnCount = 22;

    /// <summary>
    /// Selects <see cref="ColumnsSql" /> from the jobs table aliased <c>job</c>; the caller appends the rest of the
    /// query.
    /// </summary>
    public static string SelectSql(PostgresNames names)
      => $"""
         select
             {ColumnsSql}
         from {names.Jobs} job
         """;

    public static PostgresJobInspectionRow Read(NpgsqlDataReader reader)
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

    public static async ValueTask<IReadOnlyList<PostgresJobInspectionRow>> ReadAllAsync(
        NpgsqlCommand command,
        CancellationToken cancellationToken)
    {
        var rows = new List<PostgresJobInspectionRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(Read(reader));
        }

        return rows;
    }
}
