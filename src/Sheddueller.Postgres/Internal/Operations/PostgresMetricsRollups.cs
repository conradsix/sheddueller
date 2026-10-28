namespace Sheddueller.Postgres.Internal.Operations;

using Npgsql;

using NpgsqlTypes;

using Sheddueller;
using Sheddueller.Storage;

internal static class PostgresMetricsRollups
{
    public const int BucketSizeSeconds = 5;

    internal static readonly long[] DurationHistogramThresholdsMs =
    [
        1,
        2,
        5,
        10,
        20,
        50,
        100,
        200,
        500,
        1_000,
        2_000,
        5_000,
        10_000,
        30_000,
        60_000,
        120_000,
        300_000,
        600_000,
        1_800_000,
        3_600_000,
        7_200_000,
        21_600_000,
        86_400_000,
    ];

    /// <summary>
    /// Classifies a job event for the metrics rollups, from its kind and, for a lifecycle event, its message.
    /// </summary>
    public static PostgresJobEventMetric ClassifyJobEvent(
        JobEventKind kind,
        string? message)
      => kind switch
      {
          JobEventKind.AttemptStarted => PostgresJobEventMetric.AttemptStarted,
          JobEventKind.AttemptFailed => PostgresJobEventMetric.AttemptFailed,
          JobEventKind.Lifecycle => message switch
          {
              PostgresJobEventMessages.Queued => PostgresJobEventMetric.Enqueued,
              PostgresJobEventMessages.Completed => PostgresJobEventMetric.Succeeded,
              PostgresJobEventMessages.Failed => PostgresJobEventMetric.Failed,
              PostgresJobEventMessages.Canceled => PostgresJobEventMetric.Canceled,
              not null when message.StartsWith(PostgresJobEventMessages.SupersededFailurePrefix, StringComparison.Ordinal)
                => PostgresJobEventMetric.Failed,
              _ => PostgresJobEventMetric.None,
          },
          _ => PostgresJobEventMetric.None,
      };

    public static async ValueTask RecordJobEventAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        JobEvent jobEvent,
        PostgresJobEventMetric metric,
        CancellationToken cancellationToken)
    {
        if (metric == PostgresJobEventMetric.None)
        {
            return;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
          $"""
          with job as (
              select
                  job_id,
                  enqueued_at_utc,
                  claimed_at_utc,
                  completed_at_utc,
                  failed_at_utc,
                  canceled_at_utc,
                  scheduled_fire_at_utc,
                  schedule_occurrence_kind
              from {context.Names.Jobs}
              where job_id = @job_id
          ),
          counter_samples as (
              select {BucketStartedAtSql("job.enqueued_at_utc")} as bucket_started_at_utc, 1::bigint as enqueued_count, 0::bigint as claimed_started_count, 0::bigint as succeeded_count, 0::bigint as failed_count, 0::bigint as canceled_count, 0::bigint as retry_event_count
              from job
              where @metric = 'Enqueued'
              union all
              select {BucketStartedAtSql("coalesce(job.claimed_at_utc, @event_occurred_at_utc)")}, 0::bigint, 1::bigint, 0::bigint, 0::bigint, 0::bigint, 0::bigint
              from job
              where @metric = 'AttemptStarted'
              union all
              select {BucketStartedAtSql("@event_occurred_at_utc")}, 0::bigint, 0::bigint, 0::bigint, 0::bigint, 0::bigint, 1::bigint
              from job
              where @metric = 'AttemptFailed'
              union all
              select {BucketStartedAtSql("job.completed_at_utc")}, 0::bigint, 0::bigint, 1::bigint, 0::bigint, 0::bigint, 0::bigint
              from job
              where @metric = 'Succeeded'
                and job.completed_at_utc is not null
              union all
              select {BucketStartedAtSql("job.failed_at_utc")}, 0::bigint, 0::bigint, 0::bigint, 1::bigint, 0::bigint, 0::bigint
              from job
              where @metric = 'Failed'
                and job.failed_at_utc is not null
              union all
              select {BucketStartedAtSql("job.canceled_at_utc")}, 0::bigint, 0::bigint, 0::bigint, 0::bigint, 1::bigint, 0::bigint
              from job
              where @metric = 'Canceled'
                and job.canceled_at_utc is not null
          ),
          bucket_counts as (
              select
                  bucket_started_at_utc,
                  sum(enqueued_count) as enqueued_count,
                  sum(claimed_started_count) as claimed_started_count,
                  sum(succeeded_count) as succeeded_count,
                  sum(failed_count) as failed_count,
                  sum(canceled_count) as canceled_count,
                  sum(retry_event_count) as retry_event_count
              from counter_samples
              group by bucket_started_at_utc
          )
          insert into {context.Names.MetricsBuckets} as bucket (
              bucket_started_at_utc,
              enqueued_count,
              claimed_started_count,
              succeeded_count,
              failed_count,
              canceled_count,
              retry_event_count)
          select
              bucket_started_at_utc,
              enqueued_count,
              claimed_started_count,
              succeeded_count,
              failed_count,
              canceled_count,
              retry_event_count
          from bucket_counts
          on conflict (bucket_started_at_utc) do update
          set enqueued_count = bucket.enqueued_count + excluded.enqueued_count,
              claimed_started_count = bucket.claimed_started_count + excluded.claimed_started_count,
              succeeded_count = bucket.succeeded_count + excluded.succeeded_count,
              failed_count = bucket.failed_count + excluded.failed_count,
              canceled_count = bucket.canceled_count + excluded.canceled_count,
              retry_event_count = bucket.retry_event_count + excluded.retry_event_count;

          with job as (
              select
                  job_id,
                  enqueued_at_utc,
                  claimed_at_utc,
                  completed_at_utc,
                  failed_at_utc,
                  canceled_at_utc,
                  scheduled_fire_at_utc,
                  schedule_occurrence_kind
              from {context.Names.Jobs}
              where job_id = @job_id
          ),
          histogram_samples as (
              select
                  'queue_latency'::text as metric,
                  {BucketStartedAtSql("coalesce(job.claimed_at_utc, @event_occurred_at_utc)")} as bucket_started_at_utc,
                  extract(epoch from (coalesce(job.claimed_at_utc, @event_occurred_at_utc) - job.enqueued_at_utc)) * 1000 as value_ms
              from job
              where @metric = 'AttemptStarted'
                and coalesce(job.claimed_at_utc, @event_occurred_at_utc) >= job.enqueued_at_utc
              union all
              select
                  'schedule_fire_lag'::text,
                  {BucketStartedAtSql("job.enqueued_at_utc")},
                  extract(epoch from (job.enqueued_at_utc - job.scheduled_fire_at_utc)) * 1000
              from job
              where @metric = 'Enqueued'
                and job.schedule_occurrence_kind = 'Automatic'
                and job.scheduled_fire_at_utc is not null
                and job.enqueued_at_utc >= job.scheduled_fire_at_utc
              union all
              select
                  'execution_duration'::text,
                  {BucketStartedAtSql("job.completed_at_utc")},
                  extract(epoch from (job.completed_at_utc - job.claimed_at_utc)) * 1000
              from job
              where @metric = 'Succeeded'
                and job.claimed_at_utc is not null
                and job.completed_at_utc is not null
                and job.completed_at_utc >= job.claimed_at_utc
              union all
              select
                  'execution_duration'::text,
                  {BucketStartedAtSql("job.failed_at_utc")},
                  extract(epoch from (job.failed_at_utc - job.claimed_at_utc)) * 1000
              from job
              where @metric = 'Failed'
                and job.claimed_at_utc is not null
                and job.failed_at_utc is not null
                and job.failed_at_utc >= job.claimed_at_utc
              union all
              select
                  'execution_duration'::text,
                  {BucketStartedAtSql("job.canceled_at_utc")},
                  extract(epoch from (job.canceled_at_utc - job.claimed_at_utc)) * 1000
              from job
              where @metric = 'Canceled'
                and job.claimed_at_utc is not null
                and job.canceled_at_utc is not null
                and job.canceled_at_utc >= job.claimed_at_utc
          ),
          binned_samples as (
              select
                  sample.metric,
                  sample.bucket_started_at_utc,
                  coalesce(
                      (
                          select threshold.ordinality::integer - 1
                          from unnest(@duration_thresholds_ms::bigint[]) with ordinality as threshold(threshold_ms, ordinality)
                          where sample.value_ms <= threshold.threshold_ms
                          order by threshold.ordinality asc
                          limit 1
                      ),
                      @last_bin_index) as bin_index,
                  count(*) as sample_count
              from histogram_samples sample
              where sample.value_ms >= 0
              group by sample.metric, sample.bucket_started_at_utc, bin_index
          )
          insert into {context.Names.MetricsHistogramBins} as histogram (
              bucket_started_at_utc,
              metric,
              bin_index,
              sample_count)
          select
              bucket_started_at_utc,
              metric,
              bin_index,
              sample_count
          from binned_samples
          on conflict (bucket_started_at_utc, metric, bin_index) do update
          set sample_count = histogram.sample_count + excluded.sample_count;
          """;
        command.Parameters.AddWithValue("job_id", jobEvent.JobId);
        command.Parameters.Add("metric", NpgsqlDbType.Text).Value = metric.ToString();
        command.Parameters.Add("event_occurred_at_utc", NpgsqlDbType.TimestampTz).Value = jobEvent.OccurredAtUtc;
        AddHistogramParameters(command);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask RecordStagedQueuedJobsAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
          $"""
          with counter_samples as (
              select
                  {BucketStartedAtSql("job.enqueued_at_utc")} as bucket_started_at_utc,
                  count(*)::bigint as enqueued_count
              from {context.Names.Jobs} job
              join sheddueller_enqueue_results result on result.job_id = job.job_id
              where result.was_enqueued = true
              group by bucket_started_at_utc
          )
          insert into {context.Names.MetricsBuckets} as bucket (
              bucket_started_at_utc,
              enqueued_count)
          select
              bucket_started_at_utc,
              enqueued_count
          from counter_samples
          on conflict (bucket_started_at_utc) do update
          set enqueued_count = bucket.enqueued_count + excluded.enqueued_count;

          with histogram_samples as (
              select
                  {BucketStartedAtSql("job.enqueued_at_utc")} as bucket_started_at_utc,
                  extract(epoch from (job.enqueued_at_utc - job.scheduled_fire_at_utc)) * 1000 as value_ms
              from {context.Names.Jobs} job
              join sheddueller_enqueue_results result on result.job_id = job.job_id
              where result.was_enqueued = true
                and job.schedule_occurrence_kind = 'Automatic'
                and job.scheduled_fire_at_utc is not null
                and job.enqueued_at_utc >= job.scheduled_fire_at_utc
          ),
          binned_samples as (
              select
                  bucket_started_at_utc,
                  coalesce(
                      (
                          select threshold.ordinality::integer - 1
                          from unnest(@duration_thresholds_ms::bigint[]) with ordinality as threshold(threshold_ms, ordinality)
                          where sample.value_ms <= threshold.threshold_ms
                          order by threshold.ordinality asc
                          limit 1
                      ),
                      @last_bin_index) as bin_index,
                  count(*) as sample_count
              from histogram_samples sample
              where sample.value_ms >= 0
              group by bucket_started_at_utc, bin_index
          )
          insert into {context.Names.MetricsHistogramBins} as histogram (
              bucket_started_at_utc,
              metric,
              bin_index,
              sample_count)
          select
              bucket_started_at_utc,
              'schedule_fire_lag',
              bin_index,
              sample_count
          from binned_samples
          on conflict (bucket_started_at_utc, metric, bin_index) do update
          set sample_count = histogram.sample_count + excluded.sample_count;
          """;
        AddHistogramParameters(command);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask RecordCanceledJobsAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<Guid> jobIds,
        CancellationToken cancellationToken)
    {
        if (jobIds.Count == 0)
        {
            return;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
          $"""
          with canceled_jobs as (
              select
                  {BucketStartedAtSql("job.canceled_at_utc")} as bucket_started_at_utc,
                  count(*)::bigint as canceled_count
              from {context.Names.Jobs} job
              join unnest(@job_ids::uuid[]) canceled_job(job_id) on canceled_job.job_id = job.job_id
              where job.canceled_at_utc is not null
              group by bucket_started_at_utc
          )
          insert into {context.Names.MetricsBuckets} as bucket (
              bucket_started_at_utc,
              canceled_count)
          select
              bucket_started_at_utc,
              canceled_count
          from canceled_jobs
          on conflict (bucket_started_at_utc) do update
          set canceled_count = bucket.canceled_count + excluded.canceled_count;
          """;
        command.Parameters.Add("job_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid).Value = jobIds.ToArray();

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes rollups older than the configured retention, at most once per cleanup interval across the cluster.
    /// </summary>
    /// <remarks>
    /// Runs from each job retention pass and from metrics reads. Most calls find cleanup isn't due, and decide that
    /// with a plain read, so they write nothing.
    /// </remarks>
    public static async ValueTask CleanupAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        MetricsCleanupConfiguration configuration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (!await ShouldCleanupAsync(context, connection, transaction: null, configuration.CleanupInterval, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var acquired = await PostgresAdvisoryLocks.TryAcquireTransactionLockAsync(
          connection,
          transaction,
          PostgresAdvisoryLocks.MetricsCleanupLockKey,
          context.Options.SchemaName,
          cancellationToken)
          .ConfigureAwait(false);
        if (!acquired)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!await ShouldCleanupAsync(context, connection, transaction, configuration.CleanupInterval, cancellationToken).ConfigureAwait(false))
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        await PostgresOperationContext.ExecuteCountAsync(
          connection,
          transaction,
          $"""
          delete from {context.Names.MetricsBuckets}
          where bucket_started_at_utc < transaction_timestamp() - @retention;

          update {context.Names.MetricsRollupState}
          set last_cleanup_at_utc = transaction_timestamp()
          where singleton_id = 1;
          """,
          command => command.Parameters.AddWithValue("retention", configuration.Retention),
          cancellationToken)
          .ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether the last cleanup is at least <paramref name="cleanupInterval" /> old. Inside a transaction it also locks
    /// the cleanup state row, so the check holds until that transaction records the new cleanup.
    /// </summary>
    private static async ValueTask<bool> ShouldCleanupAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        TimeSpan cleanupInterval,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
          $"""
          select last_cleanup_at_utc is null
              or last_cleanup_at_utc <= transaction_timestamp() - @cleanup_interval
          from {context.Names.MetricsRollupState}
          where singleton_id = 1
          {(transaction is null ? string.Empty : "for update")};
          """;
        command.Parameters.AddWithValue("cleanup_interval", cleanupInterval);

        return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
          ?? throw new InvalidOperationException("PostgreSQL did not return metrics cleanup state."));
    }

    private static void AddHistogramParameters(NpgsqlCommand command)
    {
        command.Parameters.Add("duration_thresholds_ms", NpgsqlDbType.Array | NpgsqlDbType.Bigint).Value =
          DurationHistogramThresholdsMs;
        command.Parameters.AddWithValue("last_bin_index", DurationHistogramThresholdsMs.Length - 1);
    }

    internal static string BucketStartedAtSql(string timestampSql)
      => $"to_timestamp(floor(extract(epoch from {timestampSql}) / {BucketSizeSeconds}) * {BucketSizeSeconds})";
}
