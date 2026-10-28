namespace Sheddueller.Postgres.Tests;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

using Sheddueller.Postgres;
using Sheddueller.Postgres.Internal;

using Shouldly;

public sealed class PostgresMigrationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Migration_FreshSchema_CreatesSchemaAndStampsVersion()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);

        (await context.ReadSchemaVersionAsync()).ShouldBe(PostgresNames.ExpectedSchemaVersion);
    }

    [Fact]
    public async Task Migration_Reapplied_IsIdempotent()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(Guid.NewGuid(), priority: 7));
        var jobData = await ReadJobDataAsync(context);
        var constraintIds = await ReadConstraintIdsAsync(context);
        var appliedAt = await ReadAppliedAtAsync(context);
        var migrator = context.Provider.GetRequiredService<IPostgresMigrator>();

        await migrator.ApplyAsync();

        (await context.ReadSchemaVersionAsync()).ShouldBe(PostgresNames.ExpectedSchemaVersion);
        (await ReadJobDataAsync(context)).ShouldBe(jobData);
        (await ReadConstraintIdsAsync(context)).ShouldBe(constraintIds);
        (await ReadAppliedAtAsync(context)).ShouldBe(appliedAt);
    }

    [Fact]
    public async Task Migration_ReappliedWithActiveJobReader_DoesNotBlock()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var readCommand = connection.CreateCommand();
        readCommand.Transaction = transaction;
#pragma warning disable CA2100 // The test-generated schema identifier is quoted by PostgresTestContext.Table.
        readCommand.CommandText = $"select job_id from {context.Table("jobs")} limit 1;";
#pragma warning restore CA2100
        await readCommand.ExecuteScalarAsync();

        var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        {
            CommandTimeout = 2,
        };
        await using var dataSource = NpgsqlDataSource.Create(connectionString.ConnectionString);
        await using var provider = PostgresTestContext.CreateProvider(dataSource, context.SchemaName);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await provider.GetRequiredService<IPostgresMigrator>().ApplyAsync(cancellation.Token);

        await transaction.RollbackAsync();
        (await context.ReadSchemaVersionAsync()).ShouldBe(PostgresNames.ExpectedSchemaVersion);
    }

    [Fact]
    public async Task Migration_Version14Schema_UpgradesAndPreservesJobs()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(Guid.NewGuid(), priority: 7));
        var jobData = await ReadJobDataAsync(context);
        await ExecuteAsync(
          context,
          $"""
          drop table {context.Table("job_dependencies")};
          create index idx_jobs_inspection_newest on {context.Table("jobs")} (enqueue_sequence desc);
          create index idx_job_events_job_sequence on {context.Table("job_events")} (job_id, event_sequence);
          update {context.Table("schema_info")} set schema_version = 14 where singleton_id = 1;
          """);
        (await IndexExistsAsync(context, "idx_jobs_inspection_newest")).ShouldBeTrue();
        (await IndexExistsAsync(context, "idx_job_events_job_sequence")).ShouldBeTrue();

        await context.Provider.GetRequiredService<IPostgresMigrator>().ApplyAsync();

        (await context.ReadSchemaVersionAsync()).ShouldBe(PostgresNames.ExpectedSchemaVersion);
        (await ReadJobDataAsync(context)).ShouldBe(jobData);
        await AssertTableExistsAsync(context, "job_dependencies");
        (await IndexExistsAsync(context, "idx_job_dependencies_prerequisite_job_id")).ShouldBeTrue();
        (await IndexExistsAsync(context, "idx_jobs_inspection_newest")).ShouldBeFalse();
        (await IndexExistsAsync(context, "idx_job_events_job_sequence")).ShouldBeFalse();
    }

    [Fact]
    public async Task Migration_NewStepOnCurrentSchema_RunsOnlyThatStep()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(Guid.NewGuid(), priority: 7));
        var jobData = await ReadJobDataAsync(context);
        var constraintIds = await ReadConstraintIdsAsync(context);

        await this.CreateMigrator(context.SchemaName, MarkerStep(1)).ApplyAsync();

        (await context.ReadSchemaVersionAsync()).ShouldBe(PostgresMigrationSteps.BaselineVersion + 1);
        await AssertTableExistsAsync(context, "step_marker_1");
        (await ReadJobDataAsync(context)).ShouldBe(jobData);
        (await ReadConstraintIdsAsync(context)).ShouldBe(constraintIds);
    }

    [Fact]
    public async Task Migration_FreshSchema_AppliesBaselineThenSteps()
    {
        await using var context = PostgresTestContext.CreateUnmigrated(fixture);

        await this.CreateMigrator(context.SchemaName, MarkerStep(1)).ApplyAsync();

        (await context.ReadSchemaVersionAsync()).ShouldBe(PostgresMigrationSteps.BaselineVersion + 1);
        await AssertTableExistsAsync(context, "jobs");
        await AssertTableExistsAsync(context, "step_marker_1");
    }

    [Fact]
    public async Task Migration_SchemaPartWayThroughSteps_RunsOnlyTheRemainingSteps()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        await this.CreateMigrator(context.SchemaName, MarkerStep(1)).ApplyAsync();

        // Re-running step 1 would fail: its table already exists.
        await this.CreateMigrator(context.SchemaName, MarkerStep(1), MarkerStep(2)).ApplyAsync();

        (await context.ReadSchemaVersionAsync()).ShouldBe(PostgresMigrationSteps.BaselineVersion + 2);
        await AssertTableExistsAsync(context, "step_marker_2");
    }

    [Fact]
    public async Task Migration_TableLockHeldThroughout_GivesUpWithoutChangingTheSchema()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        await using var blocker = await HoldJobsLockAsync(context);
        var migrator = this.CreateMigrator(
          context.SchemaName,
          new PostgresMigrationLockPolicy(TimeSpan.FromMilliseconds(100), MaxAttempts: 3, TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10)),
          AddJobsColumnStep);

        var exception = await Should.ThrowAsync<InvalidOperationException>(() => migrator.ApplyAsync().AsTask());

        exception.Message.ShouldContain("couldn't lock the tables it changes after 3 attempts");
        exception.InnerException.ShouldBeOfType<PostgresException>().SqlState.ShouldBe(PostgresErrorCodes.LockNotAvailable);
        await blocker.RollbackAsync();
        (await context.ReadSchemaVersionAsync()).ShouldBe(PostgresMigrationSteps.BaselineVersion);
        (await JobsColumnExistsAsync(context)).ShouldBeFalse();
    }

    [Fact]
    public async Task Migration_TableLockReleasedWhileRetrying_AppliesTheStep()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        var blocker = await HoldJobsLockAsync(context);
        var migrator = this.CreateMigrator(
          context.SchemaName,
          new PostgresMigrationLockPolicy(TimeSpan.FromMilliseconds(100), MaxAttempts: 100, TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(50)),
          AddJobsColumnStep);

        var migration = migrator.ApplyAsync().AsTask();
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        migration.IsCompleted.ShouldBeFalse();
        await blocker.DisposeAsync();
        await migration;

        (await context.ReadSchemaVersionAsync()).ShouldBe(PostgresMigrationSteps.BaselineVersion + 1);
        (await JobsColumnExistsAsync(context)).ShouldBeTrue();
    }

    [Fact]
    public void MigrationSteps_Versions_FollowTheBaselineUpToTheExpectedVersion()
    {
        var steps = PostgresMigrationSteps.All;

        steps.Select(step => step.Version).ShouldBe(Enumerable.Range(PostgresMigrationSteps.BaselineVersion + 1, steps.Count));
        (steps.Count == 0 ? PostgresMigrationSteps.BaselineVersion : steps[^1].Version).ShouldBe(PostgresNames.ExpectedSchemaVersion);
    }

    [Fact]
    public void MigrationSteps_GapAfterTheBaseline_IsRejected()
        => Should.Throw<ArgumentException>(() => this.CreateMigrator("sheddueller_gap", MarkerStep(2)));

    [Fact]
    public async Task Migration_ConcurrentInitializations_CreatesSchemaOnce()
    {
        var schemaName = "sheddueller_" + Guid.NewGuid().ToString("N");
        await using var provider = PostgresTestContext.CreateProvider(fixture.DataSource, schemaName);
        var migrator = provider.GetRequiredService<IPostgresMigrator>();

        await Task.WhenAll(migrator.ApplyAsync().AsTask(), migrator.ApplyAsync().AsTask());

        await using var command = fixture.DataSource.CreateCommand(
          $"select schema_version from {new PostgresNames(schemaName).SchemaInfo};");
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).ShouldBeTrue();
        (await reader.GetFieldValueAsync<int>(0)).ShouldBe(PostgresNames.ExpectedSchemaVersion);
        (await reader.ReadAsync()).ShouldBeFalse();
    }

    [Fact]
    public async Task Migration_NewerSchema_ThrowsWithoutChangingSchema()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        var newerVersion = PostgresNames.ExpectedSchemaVersion + 1;
        await using (var command = context.DataSource.CreateCommand(
          $"update {context.Table("schema_info")} set schema_version = @version where singleton_id = 1;"))
        {
            command.Parameters.AddWithValue("version", newerVersion);
            await command.ExecuteNonQueryAsync();
        }

        var appliedAt = await ReadAppliedAtAsync(context);
        var constraintIds = await ReadConstraintIdsAsync(context);
        var migrator = context.Provider.GetRequiredService<IPostgresMigrator>();

        var exception = await Should.ThrowAsync<InvalidOperationException>(() => migrator.ApplyAsync().AsTask());

        exception.Message.ShouldContain($"version {newerVersion} is newer than provider version {PostgresNames.ExpectedSchemaVersion}");
        (await context.ReadSchemaVersionAsync()).ShouldBe(newerVersion);
        (await ReadAppliedAtAsync(context)).ShouldBe(appliedAt);
        (await ReadConstraintIdsAsync(context)).ShouldBe(constraintIds);
    }

    [Fact]
    public async Task Migration_FreshSchema_CreatesIndexedHandlerSearchColumn()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);

        (await ScalarAsync<bool>(
          context,
          "select exists (select 1 from pg_extension where extname = 'pg_trgm');"))
          .ShouldBeTrue();

        (await ScalarAsync<bool>(
          context,
          """
          select exists (
              select 1
              from information_schema.columns
              where table_schema = @schema_name
                and table_name = 'jobs'
                and column_name = 'handler_search_text'
                and is_generated = 'ALWAYS'
          );
          """))
          .ShouldBeTrue();

        var indexDefinition = await ScalarAsync<string>(
          context,
          """
          select indexdef
          from pg_indexes
          where schemaname = @schema_name
            and indexname = 'idx_jobs_inspection_handler_search_trgm';
          """);

        indexDefinition.ShouldContain("USING gin");
        indexDefinition.ShouldContain("handler_search_text");
        indexDefinition.ShouldContain("gin_trgm_ops");
    }

    [Fact]
    public async Task Migration_FreshSchema_CreatesQueuedIdempotencyKeyIndex()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);

        (await ScalarAsync<bool>(
          context,
          """
          select exists (
              select 1
              from information_schema.columns
              where table_schema = @schema_name
                and table_name = 'jobs'
                and column_name = 'idempotency_key'
          );
          """))
          .ShouldBeTrue();

        var indexDefinition = await ScalarAsync<string>(
          context,
          """
          select indexdef
          from pg_indexes
          where schemaname = @schema_name
            and indexname = 'idx_jobs_queued_idempotency_key';
          """);

        indexDefinition.ShouldContain("UNIQUE INDEX");
        indexDefinition.ShouldContain("idempotency_key");
        indexDefinition.ShouldContain("state = 'Queued'");
    }

    [Fact]
    public async Task Migration_FreshSchema_CreatesClaimedJobsByNodeIndex()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);

        var indexDefinition = await ScalarAsync<string>(
          context,
          """
          select indexdef
          from pg_indexes
          where schemaname = @schema_name
            and indexname = 'idx_jobs_claimed_by_node';
          """);

        var normalized = indexDefinition.ToLowerInvariant();
        normalized.ShouldContain("claimed_by_node_id");
        normalized.ShouldContain("enqueue_sequence");
        normalized.ShouldContain("state = 'claimed'");
        normalized.ShouldContain("claimed_by_node_id is not null");
    }

    [Fact]
    public async Task Migration_FreshSchema_CreatesMetricsRollupTables()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);

        await AssertTableExistsAsync(context, "metrics_buckets");
        await AssertTableExistsAsync(context, "metrics_histogram_bins");
        await AssertTableExistsAsync(context, "metrics_rollup_state");

        (await ScalarAsync<long>(
          context,
          $"select count(*) from {context.Table("metrics_buckets")};"))
          .ShouldBe(0L);
        (await ScalarAsync<long>(
          context,
          $"select count(*) from {context.Table("metrics_histogram_bins")};"))
          .ShouldBe(0L);
        (await ScalarAsync<long>(
          context,
          $"select count(*) from {context.Table("metrics_rollup_state")};"))
          .ShouldBe(1L);

        var indexDefinition = await ScalarAsync<string>(
          context,
          """
          select indexdef
          from pg_indexes
          where schemaname = @schema_name
            and indexname = 'idx_metrics_histogram_bins_metric_bucket';
          """);

        indexDefinition.ShouldContain("metric");
        indexDefinition.ShouldContain("bucket_started_at_utc");
        indexDefinition.ShouldContain("bin_index");
    }

    [Fact]
    public async Task Migration_FreshSchema_CreatesSettingsTable()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);

        await AssertTableExistsAsync(context, "settings");

        (await ScalarAsync<bool>(
          context,
          """
          select exists (
              select 1
              from information_schema.columns
              where table_schema = @schema_name
                and table_name = 'settings'
                and column_name = 'value'
                and data_type = 'jsonb'
          );
          """))
          .ShouldBeTrue();
    }

    [Fact]
    public async Task Migration_FreshSchema_CreatesConcurrencyEffectiveLimitColumn()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);

        (await ScalarAsync<bool>(
          context,
          """
          select exists (
              select 1
              from information_schema.columns
              where table_schema = @schema_name
                and table_name = 'concurrency_groups'
                and column_name = 'effective_limit'
                and is_generated = 'ALWAYS'
          );
          """))
          .ShouldBeTrue();

        await ExecuteAsync(
          context,
          $"""
          insert into {context.Table("concurrency_groups")} (group_key, configured_limit, default_limit, in_use_count, updated_at_utc)
          values ('override', 5, 2, 0, transaction_timestamp()),
                 ('default', null, 3, 0, transaction_timestamp()),
                 ('built-in', null, null, 0, transaction_timestamp());
          """);

        (await ScalarAsync<int>(
          context,
          $"select effective_limit from {context.Table("concurrency_groups")} where group_key = 'override';"))
          .ShouldBe(5);
        (await ScalarAsync<int>(
          context,
          $"select effective_limit from {context.Table("concurrency_groups")} where group_key = 'default';"))
          .ShouldBe(3);
        (await ScalarAsync<int>(
          context,
          $"select effective_limit from {context.Table("concurrency_groups")} where group_key = 'built-in';"))
          .ShouldBe(1);
    }

    [Fact]
    public async Task Migration_FreshSchema_CreatesConcurrencyRateColumns()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);

        await ExecuteAsync(
          context,
          $"""
          insert into {context.Table("concurrency_groups")} (
              group_key,
              configured_limit,
              rate_limit_override_enabled,
              configured_rate_permit_count,
              configured_rate_period,
              default_rate_permit_count,
              default_rate_period,
              in_use_count,
              updated_at_utc)
          values
              ('override', null, true, 5, interval '1 second', 2, interval '1 minute', 0, transaction_timestamp()),
              ('unlimited', null, true, null, null, 2, interval '1 minute', 0, transaction_timestamp()),
              ('default', null, false, null, null, 3, interval '1 minute', 0, transaction_timestamp()),
              ('built-in', null, false, null, null, null, null, 0, transaction_timestamp());
          """);

        (await context.ReadConcurrencyGroupAsync("override")).ShouldNotBeNull().EffectiveRateLimit
          .ShouldBe(new ConcurrencyGroupRateLimit(5, TimeSpan.FromSeconds(1)));
        (await context.ReadConcurrencyGroupAsync("unlimited")).ShouldNotBeNull().EffectiveRateLimit.ShouldBeNull();
        (await context.ReadConcurrencyGroupAsync("default")).ShouldNotBeNull().EffectiveRateLimit
          .ShouldBe(new ConcurrencyGroupRateLimit(3, TimeSpan.FromMinutes(1)));
        (await context.ReadConcurrencyGroupAsync("built-in")).ShouldNotBeNull().EffectiveRateLimit.ShouldBeNull();
    }

    [Fact]
    public async Task Migration_FreshSchema_CreatesTagOrdinalColumnsAndIndexes()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);

        await AssertOrdinalColumnAsync(context, "job_tags");
        await AssertOrdinalColumnAsync(context, "schedule_tags");

        (await ScalarAsync<bool>(
          context,
          """
          select exists (
              select 1
              from pg_indexes
              where schemaname = @schema_name
                and indexname = 'idx_job_tags_job_id_ordinal'
                and indexdef like '%UNIQUE INDEX%'
          );
          """))
          .ShouldBeTrue();

        (await ScalarAsync<bool>(
          context,
          """
          select exists (
              select 1
              from pg_indexes
              where schemaname = @schema_name
                and indexname = 'idx_schedule_tags_schedule_key_ordinal'
                and indexdef like '%UNIQUE INDEX%'
          );
          """))
          .ShouldBeTrue();
    }

    // A step that creates an unconstrained table, so it leaves the schema's constraints alone and fails if run twice.
    private static PostgresMigrationStep MarkerStep(int stepNumber)
      => new(
        PostgresMigrationSteps.BaselineVersion + stepNumber,
        names => $"create table {names.Schema}.step_marker_{stepNumber} (id integer);");

    // Needs an ACCESS EXCLUSIVE lock on jobs, like any column change on a busy table.
    private static PostgresMigrationStep AddJobsColumnStep { get; } = new(
      PostgresMigrationSteps.BaselineVersion + 1,
      names => $"alter table {names.Jobs} add column step_note text null;");

    private PostgresMigrator CreateMigrator(string schemaName, params PostgresMigrationStep[] steps)
      => this.CreateMigrator(schemaName, PostgresMigrationLockPolicy.Default, steps);

    private PostgresMigrator CreateMigrator(string schemaName, PostgresMigrationLockPolicy lockPolicy, params PostgresMigrationStep[] steps)
      => new(
        new ShedduellerPostgresOptions { DataSource = fixture.DataSource, SchemaName = schemaName },
        NullLogger.Instance,
        steps,
        lockPolicy);

    // An open transaction that has read the jobs table, as a dashboard query or a worker would.
    private static async ValueTask<BlockingTransaction> HoldJobsLockAsync(PostgresTestContext context)
    {
        var connection = await context.DataSource.OpenConnectionAsync();
        var transaction = await connection.BeginTransactionAsync();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
#pragma warning disable CA2100 // The test-generated schema identifier is quoted by PostgresTestContext.Table.
        command.CommandText = $"select count(*) from {context.Table("jobs")};";
#pragma warning restore CA2100
        await command.ExecuteScalarAsync();
        return new BlockingTransaction(connection, transaction);
    }

    private static async ValueTask<bool> JobsColumnExistsAsync(PostgresTestContext context)
      => await ScalarAsync<bool>(
          context,
          """
          select exists (
              select 1
              from information_schema.columns
              where table_schema = @schema_name
                and table_name = 'jobs'
                and column_name = 'step_note'
          );
          """);

    private static async ValueTask<string> ReadJobDataAsync(PostgresTestContext context)
      => await ScalarAsync<string>(context, $"select row_to_json(job)::text from {context.Table("jobs")} job;");

    private static async ValueTask<DateTime> ReadAppliedAtAsync(PostgresTestContext context)
      => await ScalarAsync<DateTime>(
        context,
        $"select applied_at_utc from {context.Table("schema_info")} where singleton_id = 1;");

    private static async ValueTask<long[]> ReadConstraintIdsAsync(PostgresTestContext context)
      => await ScalarAsync<long[]>(
        context,
        """
        select array_agg(constraint_info.oid::bigint order by constraint_info.oid)
        from pg_constraint constraint_info
        join pg_namespace schema_info on schema_info.oid = constraint_info.connamespace
        where schema_info.nspname = @schema_name;
        """);

    private static async ValueTask<T> ScalarAsync<T>(
        PostgresTestContext context,
        string commandText)
    {
        await using var command = context.DataSource.CreateCommand(commandText);
        command.Parameters.AddWithValue("schema_name", context.SchemaName);
        var result = await command.ExecuteScalarAsync();
        result.ShouldNotBeNull();
        return result.ShouldBeOfType<T>();
    }

    private static async Task AssertOrdinalColumnAsync(
        PostgresTestContext context,
        string tableName)
      => (await ScalarAsync<bool>(
          context,
          """
          select exists (
              select 1
              from information_schema.columns
              where table_schema = @schema_name
                and table_name = @table_name
                and column_name = 'ordinal'
                and data_type = 'integer'
                and is_nullable = 'NO'
          );
          """,
          command => command.Parameters.AddWithValue("table_name", tableName)))
        .ShouldBeTrue();

    private static async Task AssertTableExistsAsync(
        PostgresTestContext context,
        string tableName)
      => (await ScalarAsync<bool>(
          context,
          """
          select exists (
              select 1
              from information_schema.tables
              where table_schema = @schema_name
                and table_name = @table_name
          );
          """,
          command => command.Parameters.AddWithValue("table_name", tableName)))
        .ShouldBeTrue();

    private static async ValueTask<T> ScalarAsync<T>(
        PostgresTestContext context,
        string commandText,
        Action<Npgsql.NpgsqlCommand> configure)
    {
        await using var command = context.DataSource.CreateCommand(commandText);
        command.Parameters.AddWithValue("schema_name", context.SchemaName);
        configure(command);
        var result = await command.ExecuteScalarAsync();
        result.ShouldNotBeNull();
        return result.ShouldBeOfType<T>();
    }

    private static async ValueTask ExecuteAsync(
        PostgresTestContext context,
        string commandText)
    {
        await using var command = context.DataSource.CreateCommand(commandText);
        await command.ExecuteNonQueryAsync();
    }

    private static async ValueTask<bool> IndexExistsAsync(
        PostgresTestContext context,
        string indexName)
      => await ScalarAsync<bool>(
          context,
          """
          select exists (
              select 1
              from pg_indexes
              where schemaname = @schema_name
                and indexname = @index_name
          );
          """,
          command => command.Parameters.AddWithValue("index_name", indexName));

    private sealed class BlockingTransaction(NpgsqlConnection connection, NpgsqlTransaction transaction) : IAsyncDisposable
    {
        public async ValueTask RollbackAsync()
          => await transaction.RollbackAsync();

        public async ValueTask DisposeAsync()
        {
            await transaction.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
