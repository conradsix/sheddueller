namespace Sheddueller.Postgres.Tests;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Sheddueller;
using Sheddueller.Inspection.Metrics;
using Sheddueller.Postgres.Internal;
using Sheddueller.Postgres.Internal.Operations;
using Sheddueller.Storage;
using Sheddueller.Tests.Logging;

using Shouldly;

public sealed class PostgresMetricsRollupTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task MetricsRead_OldRollupBuckets_RemovesExpiredBucketsAndHistogramBins()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        await InsertOldRollupAsync(context);

        await context.Provider.GetRequiredService<IMetricsInspectionReader>()
          .GetMetricsAsync(new MetricsInspectionQuery([TimeSpan.FromMinutes(5)]));

        (await CountAsync(context, "metrics_buckets")).ShouldBe(0L);
        (await CountAsync(context, "metrics_histogram_bins")).ShouldBe(0L);
    }

    [Fact]
    public async Task MetricsRead_PersistedMetricsRetention_RetainsBucketsInsideConfiguredWindow()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        await context.Provider.GetRequiredService<IShedduellerCleanupConfigurationStore>()
          .SetCleanupConfigurationAsync(new ShedduellerCleanupConfiguration(
            JobRetentionCleanupConfiguration.FromOptions(new JobRetentionOptions()),
            new JobEventCleanupConfiguration(TimeSpan.FromDays(7), TimeSpan.FromHours(1)),
            new MetricsCleanupConfiguration(TimeSpan.FromDays(10), TimeSpan.FromHours(1))));
        await InsertOldRollupAsync(context);

        await context.Provider.GetRequiredService<IMetricsInspectionReader>()
          .GetMetricsAsync(new MetricsInspectionQuery([TimeSpan.FromMinutes(5)]));

        (await CountAsync(context, "metrics_buckets")).ShouldBe(1L);
        (await CountAsync(context, "metrics_histogram_bins")).ShouldBe(1L);
    }

    [Fact]
    public async Task RetentionPass_OldRollupBuckets_RemovesThemWithoutAnyMetricsRead()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        await InsertOldRollupAsync(context);

        await RunRetentionPassAsync(context);

        (await CountAsync(context, "metrics_buckets")).ShouldBe(0L);
        (await CountAsync(context, "metrics_histogram_bins")).ShouldBe(0L);
    }

    [Fact]
    public async Task RetentionPass_MetricsCleanupNotDue_KeepsOldRollupBuckets()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        await InsertOldRollupAsync(context);
        await using (var command = context.DataSource.CreateCommand(
          $"update {context.Table("metrics_rollup_state")} set last_cleanup_at_utc = transaction_timestamp() - interval '5 minutes';"))
        {
            await command.ExecuteNonQueryAsync();
        }

        await RunRetentionPassAsync(context);

        (await CountAsync(context, "metrics_buckets")).ShouldBe(1L);
    }

    [Fact]
    public async Task RetentionPass_MetricsCleanupFails_ReturnsDeletedJobsAndLogsFailure()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        var jobId = Guid.NewGuid();
        await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(jobId));
        var claimed = await PostgresTestData.ClaimAsync(context.Store);
        (await context.Store.MarkCompletedAsync(new CompleteJobRequest(jobId, "node-1", claimed.LeaseToken, DateTimeOffset.UtcNow.AddDays(-2)))).ShouldBeTrue();
        await using (var command = context.DataSource.CreateCommand($"drop table {context.Table("metrics_rollup_state")};"))
        {
            await command.ExecuteNonQueryAsync();
        }

        using var logs = new TestLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var store = new PostgresJobStore(
          context.Provider.GetRequiredService<ShedduellerPostgresOptions>(),
          Options.Create(new ShedduellerOptions()),
          loggerFactory.CreateLogger<PostgresJobStore>());

        var result = await store.CleanupTerminalJobsAsync(new JobRetentionCleanupRequest(
          DateTimeOffset.UtcNow.AddDays(-1),
          failedBeforeUtc: null,
          canceledBeforeUtc: null,
          batchSize: 10));

        result.DeletedCount.ShouldBe(1);
        var entry = logs.SingleByEventId(1230);
        entry.Level.ShouldBe(LogLevel.Warning);
        entry.Properties["SchemaName"].ShouldBe(context.SchemaName);
    }

    [Fact]
    public async Task JobOutcomes_CompletedFailedRetriedSupersededAndCanceled_CountInRollups()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        var store = context.Store;

        var completed = Guid.NewGuid();
        await store.EnqueueAsync(PostgresTestData.CreateRequest(completed));
        var completedClaim = await PostgresTestData.ClaimAsync(store);
        (await store.MarkCompletedAsync(new CompleteJobRequest(completed, "node-1", completedClaim.LeaseToken, DateTimeOffset.UtcNow))).ShouldBeTrue();

        var failed = Guid.NewGuid();
        await store.EnqueueAsync(PostgresTestData.CreateRequest(failed));
        var failedClaim = await PostgresTestData.ClaimAsync(store);
        (await store.MarkFailedAsync(new FailJobRequest(failed, "node-1", failedClaim.LeaseToken, DateTimeOffset.UtcNow, PostgresTestData.CreateFailure()))).ShouldBeTrue();

        var retried = Guid.NewGuid();
        await store.EnqueueAsync(PostgresTestData.CreateRequest(
          retried,
          maxAttempts: 2,
          retryBackoffKind: RetryBackoffKind.Fixed,
          retryBaseDelay: TimeSpan.FromHours(1)));
        var retriedClaim = await PostgresTestData.ClaimAsync(store);
        (await store.MarkFailedAsync(new FailJobRequest(retried, "node-1", retriedClaim.LeaseToken, DateTimeOffset.UtcNow, PostgresTestData.CreateFailure()))).ShouldBeTrue();

        var superseded = Guid.NewGuid();
        await store.EnqueueAsync(PostgresTestData.CreateRequest(
          superseded,
          maxAttempts: 2,
          retryBackoffKind: RetryBackoffKind.Fixed,
          retryBaseDelay: TimeSpan.FromHours(1),
          idempotencyKey: "metrics:superseded"));
        var supersededClaim = await PostgresTestData.ClaimAsync(store);
        supersededClaim.JobId.ShouldBe(superseded);
        await store.EnqueueAsync(PostgresTestData.CreateRequest(Guid.NewGuid(), idempotencyKey: "metrics:superseded"));
        (await store.MarkFailedAsync(new FailJobRequest(superseded, "node-1", supersededClaim.LeaseToken, DateTimeOffset.UtcNow, PostgresTestData.CreateFailure()))).ShouldBeTrue();

        var canceled = Guid.NewGuid();
        await store.EnqueueAsync(PostgresTestData.CreateRequest(canceled));
        (await store.CancelAsync(new CancelJobRequest(canceled, DateTimeOffset.UtcNow))).ShouldBe(JobCancellationResult.Canceled);
        await ((IJobEventSink)store).AppendAsync(new AppendJobEventRequest(
          completed,
          JobEventKind.Log,
          AttemptNumber: 1,
          LogLevel: JobLogLevel.Information,
          Message: PostgresJobEventMessages.Completed));

        var totals = await ReadRollupTotalsAsync(context);

        totals.ShouldBe(new RollupTotals(
          Enqueued: 6,
          ClaimedStarted: 4,
          Succeeded: 1,
          Failed: 2,
          Canceled: 1,
          RetryEvents: 3));
    }

    [Theory]
    [InlineData(JobEventKind.Lifecycle, PostgresJobEventMessages.Queued, "Enqueued")]
    [InlineData(JobEventKind.Lifecycle, PostgresJobEventMessages.Completed, "Succeeded")]
    [InlineData(JobEventKind.Lifecycle, PostgresJobEventMessages.Failed, "Failed")]
    [InlineData(JobEventKind.Lifecycle, PostgresJobEventMessages.SupersededFailurePrefix + "00000000-0000-0000-0000-000000000001", "Failed")]
    [InlineData(JobEventKind.Lifecycle, PostgresJobEventMessages.Canceled, "Canceled")]
    [InlineData(JobEventKind.Lifecycle, PostgresJobEventMessages.RetryScheduled, "None")]
    [InlineData(JobEventKind.Lifecycle, PostgresJobEventMessages.Released, "None")]
    [InlineData(JobEventKind.AttemptStarted, "Attempt started", "AttemptStarted")]
    [InlineData(JobEventKind.AttemptFailed, "failed", "AttemptFailed")]
    [InlineData(JobEventKind.AttemptCompleted, "Attempt completed", "None")]
    [InlineData(JobEventKind.Log, PostgresJobEventMessages.Completed, "None")]
    [InlineData(JobEventKind.Progress, PostgresJobEventMessages.Failed, "None")]
    [InlineData(JobEventKind.CancelObserved, PostgresJobEventMessages.Canceled, "None")]
    public void JobEventMetric_KindAndMessage_ClassifiesForRollups(JobEventKind kind, string message, string expectedMetric)
      => PostgresMetricsRollups.ClassifyJobEvent(kind, message).ToString().ShouldBe(expectedMetric);

    private static async ValueTask RunRetentionPassAsync(PostgresTestContext context)
      => (await ((IJobRetentionStore)context.Store).CleanupTerminalJobsAsync(
          new JobRetentionCleanupRequest(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(-1),
            batchSize: 100)))
        .DeletedCount.ShouldBe(0);

    private static async ValueTask InsertOldRollupAsync(PostgresTestContext context)
    {
        await using var command = context.DataSource.CreateCommand(
          $"""
          insert into {context.Table("metrics_buckets")} (
              bucket_started_at_utc,
              enqueued_count)
          values (
              transaction_timestamp() - interval '8 days',
              1);

          insert into {context.Table("metrics_histogram_bins")} (
              bucket_started_at_utc,
              metric,
              bin_index,
              sample_count)
          values (
              (select bucket_started_at_utc from {context.Table("metrics_buckets")} limit 1),
              'queue_latency',
              0,
              1);
          """);
        await command.ExecuteNonQueryAsync();
    }

    private static async ValueTask<RollupTotals> ReadRollupTotalsAsync(PostgresTestContext context)
    {
        await using var command = context.DataSource.CreateCommand(
          $"""
          select
              coalesce(sum(enqueued_count), 0)::bigint,
              coalesce(sum(claimed_started_count), 0)::bigint,
              coalesce(sum(succeeded_count), 0)::bigint,
              coalesce(sum(failed_count), 0)::bigint,
              coalesce(sum(canceled_count), 0)::bigint,
              coalesce(sum(retry_event_count), 0)::bigint
          from {context.Table("metrics_buckets")};
          """);
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).ShouldBeTrue();

        return new RollupTotals(
          reader.GetInt64(0),
          reader.GetInt64(1),
          reader.GetInt64(2),
          reader.GetInt64(3),
          reader.GetInt64(4),
          reader.GetInt64(5));
    }

    private static async ValueTask<long> CountAsync(
        PostgresTestContext context,
        string tableName)
    {
        await using var command = context.DataSource.CreateCommand($"select count(*) from {context.Table(tableName)};");
        var result = await command.ExecuteScalarAsync();
        result.ShouldNotBeNull();
        return result.ShouldBeOfType<long>();
    }

    private sealed record RollupTotals(
        long Enqueued,
        long ClaimedStarted,
        long Succeeded,
        long Failed,
        long Canceled,
        long RetryEvents);
}
