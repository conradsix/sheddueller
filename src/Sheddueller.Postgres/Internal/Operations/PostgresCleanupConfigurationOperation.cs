namespace Sheddueller.Postgres.Internal.Operations;

using System.Text.Json;

using Npgsql;

using NpgsqlTypes;

using Sheddueller;

internal static class PostgresCleanupConfigurationOperation
{
    private const string JobRetentionKey = "cleanup.job_retention";
    private const string JobEventsKey = "cleanup.job_events";
    private const string MetricsKey = "cleanup.metrics";

    // Reads never write: a setting that hasn't been persisted reads as the caller's default, and only SetAsync stores
    // settings.
    public static async ValueTask<ShedduellerCleanupConfiguration> GetAsync(
        PostgresOperationContext context,
        ShedduellerCleanupConfiguration defaultConfiguration,
        CancellationToken cancellationToken)
    {
        Validate(defaultConfiguration);

        var documents = await ReadDocumentsAsync(context, [JobRetentionKey, JobEventsKey, MetricsKey], cancellationToken).ConfigureAwait(false);
        return new ShedduellerCleanupConfiguration(
          ToJobRetention(documents, defaultConfiguration.JobRetention),
          ToJobEvents(documents, defaultConfiguration.JobEvents),
          ToMetrics(documents, defaultConfiguration.Metrics));
    }

    public static async ValueTask<JobRetentionCleanupConfiguration> GetJobRetentionAsync(
        PostgresOperationContext context,
        JobRetentionCleanupConfiguration defaultConfiguration,
        CancellationToken cancellationToken)
    {
        Validate(defaultConfiguration);

        var documents = await ReadDocumentsAsync(context, [JobRetentionKey], cancellationToken).ConfigureAwait(false);
        return ToJobRetention(documents, defaultConfiguration);
    }

    public static async ValueTask<JobEventCleanupConfiguration> GetJobEventsAsync(
        PostgresOperationContext context,
        JobEventCleanupConfiguration defaultConfiguration,
        CancellationToken cancellationToken)
    {
        Validate(defaultConfiguration);

        var documents = await ReadDocumentsAsync(context, [JobEventsKey], cancellationToken).ConfigureAwait(false);
        return ToJobEvents(documents, defaultConfiguration);
    }

    public static async ValueTask<MetricsCleanupConfiguration> GetMetricsAsync(
        PostgresOperationContext context,
        MetricsCleanupConfiguration defaultConfiguration,
        CancellationToken cancellationToken)
    {
        Validate(defaultConfiguration);

        var documents = await ReadDocumentsAsync(context, [MetricsKey], cancellationToken).ConfigureAwait(false);
        return ToMetrics(documents, defaultConfiguration);
    }

    public static async ValueTask SetAsync(
        PostgresOperationContext context,
        ShedduellerCleanupConfiguration configuration,
        CancellationToken cancellationToken)
    {
        Validate(configuration);

        await using var connection = await context.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await UpsertAsync(context, connection, transaction, JobRetentionKey, ToDocument(configuration.JobRetention), cancellationToken).ConfigureAwait(false);
        await UpsertAsync(context, connection, transaction, JobEventsKey, ToDocument(configuration.JobEvents), cancellationToken).ConfigureAwait(false);
        await UpsertAsync(context, connection, transaction, MetricsKey, ToDocument(configuration.Metrics), cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static JobRetentionCleanupConfiguration ToJobRetention(
        IReadOnlyDictionary<string, string> documents,
        JobRetentionCleanupConfiguration defaultConfiguration)
    {
        if (!documents.TryGetValue(JobRetentionKey, out var json))
        {
            return defaultConfiguration;
        }

        var configuration = Deserialize<JobRetentionSettingsDocument>(JobRetentionKey, json).ToConfiguration();
        Validate(configuration);

        return configuration;
    }

    private static JobEventCleanupConfiguration ToJobEvents(
        IReadOnlyDictionary<string, string> documents,
        JobEventCleanupConfiguration defaultConfiguration)
    {
        if (!documents.TryGetValue(JobEventsKey, out var json))
        {
            return defaultConfiguration;
        }

        var configuration = Deserialize<JobEventSettingsDocument>(JobEventsKey, json).ToConfiguration();
        Validate(configuration);

        return configuration;
    }

    private static MetricsCleanupConfiguration ToMetrics(
        IReadOnlyDictionary<string, string> documents,
        MetricsCleanupConfiguration defaultConfiguration)
    {
        if (!documents.TryGetValue(MetricsKey, out var json))
        {
            return defaultConfiguration;
        }

        var configuration = Deserialize<MetricsSettingsDocument>(MetricsKey, json).ToConfiguration();
        Validate(configuration);

        return configuration;
    }

    private static async ValueTask UpsertAsync<TDocument>(
        PostgresOperationContext context,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string key,
        TDocument document,
        CancellationToken cancellationToken)
      => await PostgresOperationContext.ExecuteCountAsync(
        connection,
        transaction,
        $"""
        insert into {context.Names.Settings} (setting_key, value, updated_at_utc)
        values (@setting_key, @value, transaction_timestamp())
        on conflict (setting_key) do update
        set value = excluded.value,
            updated_at_utc = excluded.updated_at_utc;
        """,
        command => AddSettingParameters(command, key, document),
        cancellationToken)
        .ConfigureAwait(false);

    private static async ValueTask<IReadOnlyDictionary<string, string>> ReadDocumentsAsync(
        PostgresOperationContext context,
        string[] keys,
        CancellationToken cancellationToken)
    {
        await using var connection = await context.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = PostgresCommands.Create(
          connection,
          transaction: null,
          $"""
          select setting_key, value::text
          from {context.Names.Settings}
          where setting_key = any(@setting_keys);
          """);
        command.Parameters.AddWithValue("setting_keys", keys);

        var documents = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            documents.Add(reader.GetString(0), reader.GetString(1));
        }

        return documents;
    }

    private static TDocument Deserialize<TDocument>(
        string key,
        string json)
    {
        try
        {
            return JsonSerializer.Deserialize<TDocument>(json)
              ?? throw new InvalidOperationException($"PostgreSQL cleanup setting '{key}' was empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"PostgreSQL cleanup setting '{key}' is not valid JSON.", exception);
        }
    }

    private static void AddSettingParameters<TDocument>(
        NpgsqlCommand command,
        string key,
        TDocument document)
    {
        command.Parameters.AddWithValue("setting_key", key);
        command.Parameters.Add("value", NpgsqlDbType.Jsonb).Value = JsonSerializer.Serialize(document);
    }

    private static JobRetentionSettingsDocument ToDocument(JobRetentionCleanupConfiguration configuration)
      => new(
        configuration.Enabled,
        configuration.CompletedRetention?.Ticks,
        configuration.FailedRetention?.Ticks,
        configuration.CanceledRetention?.Ticks,
        configuration.CleanupInterval.Ticks,
        configuration.BatchSize);

    private static JobEventSettingsDocument ToDocument(JobEventCleanupConfiguration configuration)
      => new(configuration.Retention.Ticks, configuration.CleanupInterval.Ticks);

    private static MetricsSettingsDocument ToDocument(MetricsCleanupConfiguration configuration)
      => new(configuration.Retention.Ticks, configuration.CleanupInterval.Ticks);

    private static void Validate(ShedduellerCleanupConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        Validate(configuration.JobRetention);
        Validate(configuration.JobEvents);
        Validate(configuration.Metrics);
    }

    private static void Validate(JobRetentionCleanupConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (configuration.CompletedRetention is { } completedRetention && completedRetention <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("Job retention completed retention must be positive or null.");
        }

        if (configuration.FailedRetention is { } failedRetention && failedRetention <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("Job retention failed retention must be positive or null.");
        }

        if (configuration.CanceledRetention is { } canceledRetention && canceledRetention <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("Job retention canceled retention must be positive or null.");
        }

        if (configuration.CleanupInterval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("Job retention cleanup interval must be positive.");
        }

        if (configuration.BatchSize <= 0)
        {
            throw new InvalidOperationException("Job retention batch size must be positive.");
        }
    }

    private static void Validate(JobEventCleanupConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (configuration.Retention <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("Job event retention must be positive.");
        }

        if (configuration.CleanupInterval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("Job event cleanup interval must be positive.");
        }
    }

    private static void Validate(MetricsCleanupConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (configuration.Retention <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("Metrics retention must be positive.");
        }

        if (configuration.CleanupInterval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("Metrics cleanup interval must be positive.");
        }
    }

    private sealed record JobRetentionSettingsDocument(
        bool Enabled,
        long? CompletedRetentionTicks,
        long? FailedRetentionTicks,
        long? CanceledRetentionTicks,
        long CleanupIntervalTicks,
        int BatchSize)
    {
        public JobRetentionCleanupConfiguration ToConfiguration()
          => new(
            this.Enabled,
            ToNullableTimeSpan(this.CompletedRetentionTicks),
            ToNullableTimeSpan(this.FailedRetentionTicks),
            ToNullableTimeSpan(this.CanceledRetentionTicks),
            TimeSpan.FromTicks(this.CleanupIntervalTicks),
            this.BatchSize);
    }

    private sealed record JobEventSettingsDocument(
        long RetentionTicks,
        long CleanupIntervalTicks)
    {
        public JobEventCleanupConfiguration ToConfiguration()
          => new(TimeSpan.FromTicks(this.RetentionTicks), TimeSpan.FromTicks(this.CleanupIntervalTicks));
    }

    private sealed record MetricsSettingsDocument(
        long RetentionTicks,
        long CleanupIntervalTicks)
    {
        public MetricsCleanupConfiguration ToConfiguration()
          => new(TimeSpan.FromTicks(this.RetentionTicks), TimeSpan.FromTicks(this.CleanupIntervalTicks));
    }

    private static TimeSpan? ToNullableTimeSpan(long? ticks)
      => ticks is { } value ? TimeSpan.FromTicks(value) : null;
}
