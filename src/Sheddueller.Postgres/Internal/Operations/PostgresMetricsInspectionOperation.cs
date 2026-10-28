namespace Sheddueller.Postgres.Internal.Operations;

using System.Globalization;

using Npgsql;

using Sheddueller;
using Sheddueller.Inspection.Metrics;

internal static class PostgresMetricsInspectionOperation
{
    private const string QueueLatencyMetric = "queue_latency";
    private const string ExecutionDurationMetric = "execution_duration";
    private const string ScheduleFireLagMetric = "schedule_fire_lag";

    private static readonly TimeSpan[] DefaultMetricWindows = [TimeSpan.FromMinutes(5), TimeSpan.FromHours(1), TimeSpan.FromHours(24)];

    public static async ValueTask<MetricsInspectionSnapshot> GetAsync(
        PostgresOperationContext context,
        MetricsInspectionQuery query,
        MetricsCleanupConfiguration cleanupConfiguration,
        TimeSpan staleThreshold,
        TimeSpan deadThreshold,
        CancellationToken cancellationToken)
    {
        var windows = query.Windows is { Count: > 0 } ? query.Windows : DefaultMetricWindows;
        if (windows.Any(window => window <= TimeSpan.Zero))
        {
            throw new ArgumentOutOfRangeException(nameof(query), query.Windows, "Inspection metric windows must be positive.");
        }

        await using var connection = await context.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await PostgresMetricsRollups.CleanupAsync(context, connection, cleanupConfiguration, cancellationToken).ConfigureAwait(false);

        await using var transaction = await PostgresOperationContext.BeginReadSnapshotAsync(connection, cancellationToken).ConfigureAwait(false);
        var current = await ReadCurrentCountsAsync(context, connection, staleThreshold, deadThreshold, cancellationToken).ConfigureAwait(false);

        var metrics = new List<MetricsInspectionWindow>(windows.Count);
        foreach (var window in windows)
        {
            metrics.Add(await ReadWindowAsync(context, connection, window, current, cancellationToken).ConfigureAwait(false));
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new MetricsInspectionSnapshot(metrics);
    }

    private static async ValueTask<MetricsInspectionWindow> ReadWindowAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        TimeSpan window,
        PostgresCurrentMetricsCounts current,
        CancellationToken cancellationToken)
    {
        var counts = await ReadWindowCountsAsync(context, connection, window, cancellationToken).ConfigureAwait(false);
        var percentiles = await ReadWindowPercentilesAsync(context, connection, window, cancellationToken).ConfigureAwait(false);
        var minutes = Math.Max(window.TotalMinutes, double.Epsilon);

        return new MetricsInspectionWindow(
          window,
          current.QueuedCount,
          current.ClaimedCount,
          Convert.ToInt32(counts.FailedCount, CultureInfo.InvariantCulture),
          Convert.ToInt32(counts.CanceledCount, CultureInfo.InvariantCulture),
          current.OldestQueuedAge,
          counts.EnqueuedCount / minutes,
          counts.ClaimedStartedCount / minutes,
          counts.SucceededCount / minutes,
          counts.FailedCount / minutes,
          counts.CanceledCount / minutes,
          counts.RetryEventCount / minutes,
          percentiles.QueueLatencyP50,
          percentiles.QueueLatencyP95,
          percentiles.ExecutionDurationP50,
          percentiles.ExecutionDurationP95,
          percentiles.ScheduleFireLagP95,
          current.SaturatedGroupCount,
          current.ActiveNodeCount,
          current.StaleNodeCount,
          current.DeadNodeCount);
    }

    private static async ValueTask<PostgresCurrentMetricsCounts> ReadCurrentCountsAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        TimeSpan staleThreshold,
        TimeSpan deadThreshold,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
          $"""
          with queued_counts as (
              select
                  count(*) as queued_count,
                  max(transaction_timestamp() - enqueued_at_utc) as oldest_queued_age
              from {context.Names.Jobs}
              where state = 'Queued'
          ),
          claimed_counts as (
              select count(*) as claimed_count
              from {context.Names.Jobs}
              where state = 'Claimed'
          ),
          saturated_groups as (
              select count(*) as saturated_group_count
              from {context.Names.ConcurrencyGroups} concurrency_group
              where {PostgresSqlFragments.GroupIsSaturated("concurrency_group")}
          ),
          node_counts as (
              select
                  count(*) filter (where transaction_timestamp() - last_heartbeat_at_utc < @stale_threshold) as active_node_count,
                  count(*) filter (where transaction_timestamp() - last_heartbeat_at_utc >= @stale_threshold and transaction_timestamp() - last_heartbeat_at_utc < @dead_threshold) as stale_node_count,
                  count(*) filter (where transaction_timestamp() - last_heartbeat_at_utc >= @dead_threshold) as dead_node_count
              from {context.Names.WorkerNodes}
          )
          select
              queued_counts.queued_count,
              claimed_counts.claimed_count,
              queued_counts.oldest_queued_age,
              saturated_groups.saturated_group_count,
              node_counts.active_node_count,
              node_counts.stale_node_count,
              node_counts.dead_node_count
          from queued_counts, claimed_counts, saturated_groups, node_counts;
          """;
        command.Parameters.AddWithValue("stale_threshold", staleThreshold);
        command.Parameters.AddWithValue("dead_threshold", deadThreshold);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("PostgreSQL did not return current inspection metrics.");
        }

        return new PostgresCurrentMetricsCounts(
          Convert.ToInt32(reader.GetInt64(0), CultureInfo.InvariantCulture),
          Convert.ToInt32(reader.GetInt64(1), CultureInfo.InvariantCulture),
          reader.IsDBNull(2) ? null : reader.GetTimeSpan(2),
          Convert.ToInt32(reader.GetInt64(3), CultureInfo.InvariantCulture),
          Convert.ToInt32(reader.GetInt64(4), CultureInfo.InvariantCulture),
          Convert.ToInt32(reader.GetInt64(5), CultureInfo.InvariantCulture),
          Convert.ToInt32(reader.GetInt64(6), CultureInfo.InvariantCulture));
    }

    private static async ValueTask<PostgresWindowRollupCounts> ReadWindowCountsAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        TimeSpan window,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
          $"""
          select
              coalesce(sum(enqueued_count), 0),
              coalesce(sum(claimed_started_count), 0),
              coalesce(sum(succeeded_count), 0),
              coalesce(sum(failed_count), 0),
              coalesce(sum(canceled_count), 0),
              coalesce(sum(retry_event_count), 0)
          from {context.Names.MetricsBuckets}
          where bucket_started_at_utc >= {PostgresMetricsRollups.BucketStartedAtSql("transaction_timestamp() - @window")};
          """;
        command.Parameters.AddWithValue("window", window);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("PostgreSQL did not return rolled-up inspection metrics.");
        }

        return new PostgresWindowRollupCounts(
          Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
          Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture),
          Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture),
          Convert.ToInt64(reader.GetValue(3), CultureInfo.InvariantCulture),
          Convert.ToInt64(reader.GetValue(4), CultureInfo.InvariantCulture),
          Convert.ToInt64(reader.GetValue(5), CultureInfo.InvariantCulture));
    }

    private static async ValueTask<PostgresWindowPercentiles> ReadWindowPercentilesAsync(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        TimeSpan window,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
          $"""
          with histogram as (
              select
                  metric,
                  bin_index,
                  sum(sample_count) as sample_count
              from {context.Names.MetricsHistogramBins}
              where bucket_started_at_utc >= {PostgresMetricsRollups.BucketStartedAtSql("transaction_timestamp() - @window")}
              group by metric, bin_index
          ),
          ordered as (
              select
                  metric,
                  bin_index,
                  sum(sample_count) over (partition by metric order by bin_index asc) as cumulative_count,
                  sum(sample_count) over (partition by metric) as total_count
              from histogram
          )
          select
              metric,
              min(bin_index) filter (where cumulative_count >= ceiling(total_count * 0.50)) as p50_bin,
              min(bin_index) filter (where cumulative_count >= ceiling(total_count * 0.95)) as p95_bin
          from ordered
          group by metric;
          """;
        command.Parameters.AddWithValue("window", window);

        TimeSpan? queueLatencyP50 = null;
        TimeSpan? queueLatencyP95 = null;
        TimeSpan? executionDurationP50 = null;
        TimeSpan? executionDurationP95 = null;
        TimeSpan? scheduleFireLagP95 = null;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var metric = reader.GetString(0);
            var p50 = ReadDurationBin(reader, 1);
            var p95 = ReadDurationBin(reader, 2);

            switch (metric)
            {
                case QueueLatencyMetric:
                    queueLatencyP50 = p50;
                    queueLatencyP95 = p95;
                    break;
                case ExecutionDurationMetric:
                    executionDurationP50 = p50;
                    executionDurationP95 = p95;
                    break;
                case ScheduleFireLagMetric:
                    scheduleFireLagP95 = p95;
                    break;
            }
        }

        return new PostgresWindowPercentiles(
          queueLatencyP50,
          queueLatencyP95,
          executionDurationP50,
          executionDurationP95,
          scheduleFireLagP95);
    }

    private static TimeSpan? ReadDurationBin(
        NpgsqlDataReader reader,
        int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        var binIndex = Math.Clamp(reader.GetInt32(ordinal), 0, PostgresMetricsRollups.DurationHistogramThresholdsMs.Length - 1);
        return TimeSpan.FromMilliseconds(PostgresMetricsRollups.DurationHistogramThresholdsMs[binIndex]);
    }

    private sealed record PostgresCurrentMetricsCounts(
        int QueuedCount,
        int ClaimedCount,
        TimeSpan? OldestQueuedAge,
        int SaturatedGroupCount,
        int ActiveNodeCount,
        int StaleNodeCount,
        int DeadNodeCount);

    private sealed record PostgresWindowRollupCounts(
        long EnqueuedCount,
        long ClaimedStartedCount,
        long SucceededCount,
        long FailedCount,
        long CanceledCount,
        long RetryEventCount);

    private sealed record PostgresWindowPercentiles(
        TimeSpan? QueueLatencyP50,
        TimeSpan? QueueLatencyP95,
        TimeSpan? ExecutionDurationP50,
        TimeSpan? ExecutionDurationP95,
        TimeSpan? ScheduleFireLagP95);
}
