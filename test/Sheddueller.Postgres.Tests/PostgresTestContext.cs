#pragma warning disable CA1849 // Test row readers keep assertions compact and do not run on production paths.
#pragma warning disable CA2000 // Factory transfers ServiceProvider ownership to PostgresTestContext.

namespace Sheddueller.Postgres.Tests;

using System.Globalization;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using Sheddueller;
using Sheddueller.Inspection.ConcurrencyGroups;
using Sheddueller.Inspection.Jobs;
using Sheddueller.Inspection.Metrics;
using Sheddueller.Inspection.Nodes;
using Sheddueller.Inspection.Schedules;
using Sheddueller.Postgres;
using Sheddueller.Storage;

internal sealed class PostgresTestContext(
    NpgsqlDataSource dataSource,
    ServiceProvider provider,
    string schemaName) : IAsyncDisposable
{
    public NpgsqlDataSource DataSource { get; } = dataSource;

    public ServiceProvider Provider { get; } = provider;

    public string SchemaName { get; } = schemaName;

    public IJobStore Store => this.Provider.GetRequiredService<IJobStore>();

    public IJobEventSink EventSink => this.Provider.GetRequiredService<IJobEventSink>();

    public IJobInspectionReader InspectionReader => this.Provider.GetRequiredService<IJobInspectionReader>();

    public IScheduleInspectionReader ScheduleReader => this.Provider.GetRequiredService<IScheduleInspectionReader>();

    public IConcurrencyGroupInspectionReader ConcurrencyGroupReader => this.Provider.GetRequiredService<IConcurrencyGroupInspectionReader>();

    public INodeInspectionReader NodeReader => this.Provider.GetRequiredService<INodeInspectionReader>();

    public IMetricsInspectionReader MetricsReader => this.Provider.GetRequiredService<IMetricsInspectionReader>();

    public IJobRetentionStore RetentionStore => this.Provider.GetRequiredService<IJobRetentionStore>();

    public IJobEventRetentionStore EventRetentionStore => this.Provider.GetRequiredService<IJobEventRetentionStore>();

    public static async ValueTask<PostgresTestContext> CreateMigratedAsync(PostgresFixture fixture)
    {
        var schemaName = CreateSchemaName();
        var provider = CreateProvider(fixture.DataSource, schemaName);
        await provider.GetRequiredService<IPostgresMigrator>().ApplyAsync();
        return new PostgresTestContext(fixture.DataSource, provider, schemaName);
    }

    public static PostgresTestContext CreateUnmigrated(PostgresFixture fixture)
    {
        var schemaName = CreateSchemaName();
        return new PostgresTestContext(fixture.DataSource, CreateProvider(fixture.DataSource, schemaName), schemaName);
    }

    public static ServiceProvider CreateProvider(NpgsqlDataSource dataSource, string schemaName)
    {
        var services = new ServiceCollection();
        services.AddSheddueller(sheddueller => sheddueller.UsePostgres(options =>
        {
            options.DataSource = dataSource;
            options.SchemaName = schemaName;
        }));

        return services.BuildServiceProvider();
    }

    public async ValueTask DisposeAsync()
      => await this.Provider.DisposeAsync();

    public async ValueTask MakeScheduleDueAsync(string scheduleKey)
      => await this.ExecuteAsync(
        $"""
        update {this.Table("recurring_schedules")}
        set next_fire_at_utc = transaction_timestamp() - interval '1 minute'
        where schedule_key = @schedule_key;
        """,
        command => command.Parameters.AddWithValue("schedule_key", scheduleKey));

    public async ValueTask ForceClaimExpiredAsync(Guid jobId)
      => await this.ExecuteAsync(
        $"""
        update {this.Table("jobs")}
        set lease_expires_at_utc = transaction_timestamp() - interval '1 millisecond'
        where job_id = @job_id;
        """,
        command => command.Parameters.AddWithValue("job_id", jobId));

    public async ValueTask MakeJobDueAsync(Guid jobId)
      => await this.ExecuteAsync(
        $"""
        update {this.Table("jobs")}
        set not_before_utc = transaction_timestamp() - interval '1 millisecond'
        where job_id = @job_id;
        """,
        command => command.Parameters.AddWithValue("job_id", jobId));

    /// <summary>
    /// Moves a group's rate-limit state back by <paramref name="elapsed" />, as if that much time had passed since its
    /// last permit was taken.
    /// </summary>
    public async ValueTask AdvanceRateLimitClockAsync(string groupKey, TimeSpan elapsed)
      => await this.ExecuteAsync(
        $"""
        update {this.Table("concurrency_groups")}
        set rate_theoretical_arrival_at_utc = rate_theoretical_arrival_at_utc - @elapsed
        where group_key = @group_key;
        """,
        command =>
        {
            command.Parameters.AddWithValue("group_key", groupKey);
            command.Parameters.AddWithValue("elapsed", elapsed);
        });

    public async ValueTask<int?> ReadSchemaVersionAsync()
    {
        var result = await this.ExecuteScalarAsync(
          $"select schema_version from {this.Table("schema_info")} where singleton_id = 1;",
          _ => { });
        return result is null or DBNull ? null : Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    public async ValueTask<PostgresJobRow> ReadJobAsync(Guid jobId)
    {
        await using var command = this.DataSource.CreateCommand(
          $"""
          select
              job_id,
              state,
              priority,
              enqueue_sequence,
              enqueued_at_utc,
              not_before_utc,
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
              claimed_by_node_id,
              claimed_at_utc,
              lease_token,
              lease_expires_at_utc,
              last_heartbeat_at_utc,
              completed_at_utc,
              failed_at_utc,
              canceled_at_utc,
              failure_type_name,
              failure_message,
              failure_stack_trace,
              source_schedule_key,
              scheduled_fire_at_utc,
              schedule_occurrence_kind,
              cancellation_requested_at_utc,
              cancellation_observed_at_utc,
              idempotency_key
          from {this.Table("jobs")}
          where job_id = @job_id;
          """);
        command.Parameters.AddWithValue("job_id", jobId);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException($"Job '{jobId}' was not found.");
        }

        return new PostgresJobRow(
          reader.GetGuid(0),
          reader.GetString(1),
          reader.GetInt32(2),
          reader.GetInt64(3),
          ToDateTimeOffset(reader.GetValue(4)),
          reader.IsDBNull(5) ? null : ToDateTimeOffset(reader.GetValue(5)),
          reader.GetString(6),
          reader.GetString(7),
          reader.GetFieldValue<string[]>(8),
          reader.GetString(9),
          reader.IsDBNull(10) ? null : reader.GetFieldValue<string[]>(10),
          reader.GetString(11),
          reader.GetFieldValue<byte[]>(12),
          reader.GetInt32(13),
          reader.GetInt32(14),
          reader.IsDBNull(15) ? null : reader.GetString(15),
          reader.IsDBNull(16) ? null : reader.GetInt64(16),
          reader.IsDBNull(17) ? null : reader.GetInt64(17),
          reader.IsDBNull(18) ? null : reader.GetString(18),
          reader.IsDBNull(19) ? null : ToDateTimeOffset(reader.GetValue(19)),
          reader.IsDBNull(20) ? null : reader.GetGuid(20),
          reader.IsDBNull(21) ? null : ToDateTimeOffset(reader.GetValue(21)),
          reader.IsDBNull(22) ? null : ToDateTimeOffset(reader.GetValue(22)),
          reader.IsDBNull(23) ? null : ToDateTimeOffset(reader.GetValue(23)),
          reader.IsDBNull(24) ? null : ToDateTimeOffset(reader.GetValue(24)),
          reader.IsDBNull(25) ? null : ToDateTimeOffset(reader.GetValue(25)),
          reader.IsDBNull(26) ? null : reader.GetString(26),
          reader.IsDBNull(27) ? null : reader.GetString(27),
          reader.IsDBNull(28) ? null : reader.GetString(28),
          reader.IsDBNull(29) ? null : reader.GetString(29),
          reader.IsDBNull(30) ? null : ToDateTimeOffset(reader.GetValue(30)),
          reader.IsDBNull(31) ? null : reader.GetString(31),
          reader.IsDBNull(32) ? null : ToDateTimeOffset(reader.GetValue(32)),
          reader.IsDBNull(33) ? null : ToDateTimeOffset(reader.GetValue(33)),
          reader.IsDBNull(34) ? null : reader.GetString(34));
    }

    public async ValueTask<PostgresScheduleRow> ReadScheduleAsync(string scheduleKey)
    {
        await using var command = this.DataSource.CreateCommand(
          $"""
          select
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
              next_fire_at_utc
          from {this.Table("recurring_schedules")}
          where schedule_key = @schedule_key;
          """);
        command.Parameters.AddWithValue("schedule_key", scheduleKey);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException($"Schedule '{scheduleKey}' was not found.");
        }

        return new PostgresScheduleRow(
          reader.GetString(0),
          reader.GetString(1),
          reader.GetBoolean(2),
          reader.GetString(3),
          reader.GetInt32(4),
          reader.GetString(5),
          reader.GetString(6),
          reader.GetFieldValue<string[]>(7),
          reader.GetString(8),
          reader.IsDBNull(9) ? null : reader.GetFieldValue<string[]>(9),
          reader.GetString(10),
          reader.GetFieldValue<byte[]>(11),
          reader.GetBoolean(12),
          reader.GetInt32(13),
          reader.IsDBNull(14) ? null : reader.GetString(14),
          reader.IsDBNull(15) ? null : reader.GetInt64(15),
          reader.IsDBNull(16) ? null : reader.GetInt64(16),
          reader.IsDBNull(17) ? null : ToDateTimeOffset(reader.GetValue(17)));
    }

    public async ValueTask<IReadOnlyList<string>> ReadJobGroupKeysAsync(Guid jobId)
      => await this.ReadStringListAsync(
        $"select group_key from {this.Table("job_concurrency_groups")} where job_id = @id order by group_key asc;",
        command => command.Parameters.AddWithValue("id", jobId));

    public async ValueTask<IReadOnlyList<JobTag>> ReadJobTagsAsync(Guid jobId)
    {
        await using var command = this.DataSource.CreateCommand(
          $"""
          select name, value
          from {this.Table("job_tags")}
          where job_id = @id
          order by ordinal asc, name asc, value asc;
          """);
        command.Parameters.AddWithValue("id", jobId);

        var tags = new List<JobTag>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            tags.Add(new JobTag(reader.GetString(0), reader.GetString(1)));
        }

        return tags;
    }

    public async ValueTask<IReadOnlyList<JobTag>> ReadScheduleTagsAsync(string scheduleKey)
    {
        await using var command = this.DataSource.CreateCommand(
          $"""
          select name, value
          from {this.Table("schedule_tags")}
          where schedule_key = @id
          order by ordinal asc, name asc, value asc;
          """);
        command.Parameters.AddWithValue("id", scheduleKey);

        var tags = new List<JobTag>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            tags.Add(new JobTag(reader.GetString(0), reader.GetString(1)));
        }

        return tags;
    }

    public async ValueTask<long> ReadJobEventSequenceAsync(Guid jobId)
    {
        var result = await this.ExecuteScalarAsync(
          $"select job_event_sequence from {this.Table("jobs")} where job_id = @job_id;",
          command => command.Parameters.AddWithValue("job_id", jobId));
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    public async ValueTask<long> SumEnqueuedMetricAsync()
    {
        var result = await this.ExecuteScalarAsync(
          $"select coalesce(sum(enqueued_count), 0) from {this.Table("metrics_buckets")};",
          _ => { });
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    public async ValueTask<long> SumHistogramSamplesAsync(string metric)
    {
        var result = await this.ExecuteScalarAsync(
          $"select coalesce(sum(sample_count), 0) from {this.Table("metrics_histogram_bins")} where metric = @metric;",
          command => command.Parameters.AddWithValue("metric", metric));
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    public async ValueTask<int> CountJobEventsAsync(Guid jobId)
    {
        var result = await this.ExecuteScalarAsync(
          $"select count(*) from {this.Table("job_events")} where job_id = @job_id;",
          command => command.Parameters.AddWithValue("job_id", jobId));
        return Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    public async ValueTask InsertLogEventsAsync(Guid jobId, int count)
      => await this.ExecuteAsync(
        $"""
        with job as (
            update {this.Table("jobs")}
            set job_event_sequence = job_event_sequence + @count
            where job_id = @job_id
            returning job_event_sequence - @count as last_sequence
        )
        insert into {this.Table("job_events")} (
            job_id,
            event_sequence,
            event_id,
            kind,
            occurred_at_utc,
            attempt_number,
            log_level,
            message)
        select
            @job_id,
            job.last_sequence + series.n,
            md5(@job_id::text || ':' || (job.last_sequence + series.n)::text)::uuid,
            'Log',
            transaction_timestamp(),
            1,
            'Information',
            'log ' || series.n
        from job, generate_series(1, @count) as series(n);
        """,
        command =>
        {
            command.Parameters.AddWithValue("job_id", jobId);
            command.Parameters.AddWithValue("count", count);
        });

    public async ValueTask<IReadOnlyList<JobEvent>> ReadJobEventsAsync(Guid jobId)
    {
        var events = new List<JobEvent>();
        await foreach (var jobEvent in this.InspectionReader.ReadEventsAsync(jobId))
        {
            events.Add(jobEvent);
        }

        return events;
    }

    public async ValueTask<int> CountJobsAsync()
    {
        var result = await this.ExecuteScalarAsync(
          $"select count(*) from {this.Table("jobs")};",
          _ => { });
        return Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    public async ValueTask<int> CountQueuedJobsWithIdempotencyKeyAsync(string idempotencyKey)
    {
        var result = await this.ExecuteScalarAsync(
          $"select count(*) from {this.Table("jobs")} where state = 'Queued' and idempotency_key = @idempotency_key;",
          command => command.Parameters.AddWithValue("idempotency_key", idempotencyKey));
        return Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    public async ValueTask<IReadOnlyList<string>> ReadScheduleGroupKeysAsync(string scheduleKey)
      => await this.ReadStringListAsync(
        $"select group_key from {this.Table("schedule_concurrency_groups")} where schedule_key = @id order by group_key asc;",
        command => command.Parameters.AddWithValue("id", scheduleKey));

    public async ValueTask<PostgresConcurrencyGroupRow?> ReadConcurrencyGroupAsync(string groupKey)
    {
        await using var command = this.DataSource.CreateCommand(
          $"""
          select
              group_key,
              configured_limit,
              default_limit,
              effective_limit,
              in_use_count,
              rate_limit_override_enabled,
              configured_rate_permit_count,
              configured_rate_period,
              default_rate_permit_count,
              default_rate_period,
              effective_rate_permit_count,
              effective_rate_period,
              rate_theoretical_arrival_at_utc
          from {this.Table("concurrency_groups")}
          where group_key = @group_key;
          """);
        command.Parameters.AddWithValue("group_key", groupKey);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        return new PostgresConcurrencyGroupRow(
          reader.GetString(0),
          reader.IsDBNull(1) ? null : reader.GetInt32(1),
          reader.IsDBNull(2) ? null : reader.GetInt32(2),
          reader.GetInt32(3),
          reader.GetInt32(4),
          reader.GetBoolean(5),
          ReadRateLimit(reader, 6, 7),
          ReadRateLimit(reader, 8, 9),
          ReadRateLimit(reader, 10, 11),
          reader.IsDBNull(12) ? null : ToDateTimeOffset(reader.GetValue(12)));
    }

    public async ValueTask<int> CountJobsForScheduleAsync(string scheduleKey)
    {
        var result = await this.ExecuteScalarAsync(
          $"select count(*) from {this.Table("jobs")} where source_schedule_key = @schedule_key;",
          command => command.Parameters.AddWithValue("schedule_key", scheduleKey));
        return Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    public async ValueTask<int> CountSchedulesAsync()
    {
        var result = await this.ExecuteScalarAsync(
          $"select count(*) from {this.Table("recurring_schedules")};",
          _ => { });
        return Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    public string Table(string tableName)
      => $"{Quote(this.SchemaName)}.{Quote(tableName)}";

    private async ValueTask<IReadOnlyList<string>> ReadStringListAsync(
        string commandText,
        Action<NpgsqlCommand> configure)
    {
        await using var command = this.DataSource.CreateCommand(commandText);
        configure(command);

        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    public async ValueTask ExecuteAsync(
        string commandText,
        Action<NpgsqlCommand> configure)
    {
        await using var command = this.DataSource.CreateCommand(commandText);
        configure(command);
        await command.ExecuteNonQueryAsync();
    }

    private async ValueTask<object?> ExecuteScalarAsync(
        string commandText,
        Action<NpgsqlCommand> configure)
    {
        await using var command = this.DataSource.CreateCommand(commandText);
        configure(command);
        return await command.ExecuteScalarAsync();
    }

    private static string CreateSchemaName()
      => "sheddueller_" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

    private static string Quote(string identifier)
      => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static DateTimeOffset ToDateTimeOffset(object value)
      => value switch
      {
          DateTimeOffset dateTimeOffset => dateTimeOffset,
          DateTime dateTime => new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)),
          _ => throw new InvalidOperationException($"Unexpected timestamp type '{value.GetType()}'."),
      };

    private static ConcurrencyGroupRateLimit? ReadRateLimit(
        NpgsqlDataReader reader,
        int countOrdinal,
        int periodOrdinal)
      => reader.IsDBNull(countOrdinal)
        ? null
        : new ConcurrencyGroupRateLimit(reader.GetInt32(countOrdinal), reader.GetTimeSpan(periodOrdinal));
}

internal sealed record PostgresJobRow(
    Guid JobId,
    string State,
    int Priority,
    long EnqueueSequence,
    DateTimeOffset EnqueuedAtUtc,
    DateTimeOffset? NotBeforeUtc,
    string ServiceType,
    string MethodName,
    IReadOnlyList<string> MethodParameterTypes,
    string InvocationTargetKind,
    IReadOnlyList<string>? MethodParameterBindings,
    string SerializedArgumentsContentType,
    byte[] SerializedArguments,
    int AttemptCount,
    int MaxAttempts,
    string? RetryBackoffKind,
    long? RetryBaseDelayMs,
    long? RetryMaxDelayMs,
    string? ClaimedByNodeId,
    DateTimeOffset? ClaimedAtUtc,
    Guid? LeaseToken,
    DateTimeOffset? LeaseExpiresAtUtc,
    DateTimeOffset? LastHeartbeatAtUtc,
    DateTimeOffset? CompletedAtUtc,
    DateTimeOffset? FailedAtUtc,
    DateTimeOffset? CanceledAtUtc,
    string? FailureTypeName,
    string? FailureMessage,
    string? FailureStackTrace,
    string? SourceScheduleKey,
    DateTimeOffset? ScheduledFireAtUtc,
    string? ScheduleOccurrenceKind,
    DateTimeOffset? CancellationRequestedAtUtc,
    DateTimeOffset? CancellationObservedAtUtc,
    string? IdempotencyKey);

internal sealed record PostgresScheduleRow(
    string ScheduleKey,
    string CronExpression,
    bool IsPaused,
    string OverlapMode,
    int Priority,
    string ServiceType,
    string MethodName,
    IReadOnlyList<string> MethodParameterTypes,
    string InvocationTargetKind,
    IReadOnlyList<string>? MethodParameterBindings,
    string SerializedArgumentsContentType,
    byte[] SerializedArguments,
    bool RetryPolicyConfigured,
    int MaxAttempts,
    string? RetryBackoffKind,
    long? RetryBaseDelayMs,
    long? RetryMaxDelayMs,
    DateTimeOffset? NextFireAtUtc);

internal sealed record PostgresConcurrencyGroupRow(
    string GroupKey,
    int? ConfiguredLimit,
    int? DefaultLimit,
    int EffectiveLimit,
    int InUseCount,
    bool RateLimitOverrideEnabled,
    ConcurrencyGroupRateLimit? ConfiguredRateLimit,
    ConcurrencyGroupRateLimit? DefaultRateLimit,
    ConcurrencyGroupRateLimit? EffectiveRateLimit,
    DateTimeOffset? RateTheoreticalArrivalAtUtc);
