#pragma warning disable IDE0130

namespace Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.DependencyInjection.Extensions;

using Npgsql;

using Sheddueller;
using Sheddueller.Inspection.ConcurrencyGroups;
using Sheddueller.Inspection.Jobs;
using Sheddueller.Inspection.Metrics;
using Sheddueller.Inspection.Nodes;
using Sheddueller.Inspection.Schedules;
using Sheddueller.Postgres;
using Sheddueller.Postgres.Internal;
using Sheddueller.Postgres.Internal.Operations;
using Sheddueller.Runtime;
using Sheddueller.Storage;

/// <summary>
/// Registration extensions for the PostgreSQL Sheddueller provider.
/// </summary>
public static class ShedduellerPostgresBuilderExtensions
{
    /// <summary>
    /// Uses the PostgreSQL job store provider with a Sheddueller-owned data source.
    /// </summary>
    /// <param name="builder">The Sheddueller builder being configured.</param>
    /// <param name="connectionString">The PostgreSQL connection string.</param>
    /// <param name="configure">An optional callback for configuring the provider-owned data source.</param>
    /// <returns>The same builder for chained configuration.</returns>
    /// <remarks>
    /// The created <see cref="NpgsqlDataSource"/> is owned by dependency injection and disposed with the
    /// service provider. Apply provider migrations explicitly before starting workers against a new schema.
    /// </remarks>
    public static ShedduellerBuilder UsePostgres(
        this ShedduellerBuilder builder,
        string connectionString,
        Action<ShedduellerPostgresDataSourceOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        return builder.UsePostgres(
          _ => connectionString,
          (_, options) => configure?.Invoke(options));
    }

    /// <summary>
    /// Uses the PostgreSQL job store provider with a Sheddueller-owned data source.
    /// </summary>
    /// <param name="builder">The Sheddueller builder being configured.</param>
    /// <param name="connectionStringFactory">A callback that resolves the PostgreSQL connection string from the service provider.</param>
    /// <param name="configure">An optional callback for configuring the provider-owned data source.</param>
    /// <returns>The same builder for chained configuration.</returns>
    /// <remarks>
    /// The created <see cref="NpgsqlDataSource"/> is owned by dependency injection and disposed with the
    /// service provider. Apply provider migrations explicitly before starting workers against a new schema.
    /// </remarks>
    public static ShedduellerBuilder UsePostgres(
        this ShedduellerBuilder builder,
        Func<IServiceProvider, string> connectionStringFactory,
        Action<IServiceProvider, ShedduellerPostgresDataSourceOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(connectionStringFactory);

        builder.Services.Replace(ServiceDescriptor.Singleton(serviceProvider =>
        {
            var options = new ShedduellerPostgresDataSourceOptions();
            configure?.Invoke(serviceProvider, options);
            ValidateDataSourceOptions(options);

            var connectionString = connectionStringFactory(serviceProvider);
            ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

            var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
            options.ConfigureDataSourceBuilder?.Invoke(dataSourceBuilder);

            return new OwnedPostgresDataSource(dataSourceBuilder.Build(), options.SchemaName);
        }));
        builder.Services.Replace(ServiceDescriptor.Singleton(serviceProvider =>
        {
            var dataSource = serviceProvider.GetRequiredService<OwnedPostgresDataSource>();
            var options = new ShedduellerPostgresOptions
            {
                DataSource = dataSource.DataSource,
                SchemaName = dataSource.SchemaName,
            };
            PostgresOptionsValidator.Validate(options);
            return options;
        }));
        RegisterProviderServices(builder.Services);

        return builder;
    }

    /// <summary>
    /// Uses the PostgreSQL job store provider.
    /// </summary>
    /// <param name="builder">The Sheddueller builder being configured.</param>
    /// <param name="configure">The PostgreSQL provider options callback.</param>
    /// <returns>The same builder for chained configuration.</returns>
    /// <remarks>
    /// The configured <see cref="ShedduellerPostgresOptions.DataSource"/> is caller-owned. Apply provider
    /// migrations explicitly with <see cref="IPostgresMigrator"/> before starting workers against a new schema.
    /// </remarks>
    public static ShedduellerBuilder UsePostgres(
        this ShedduellerBuilder builder,
        Action<ShedduellerPostgresOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new ShedduellerPostgresOptions { DataSource = null! };
        configure(options);
        PostgresOptionsValidator.Validate(options);

        builder.Services.RemoveAll<OwnedPostgresDataSource>();
        builder.Services.Replace(ServiceDescriptor.Singleton(options));
        RegisterProviderServices(builder.Services);

        return builder;
    }

    private static void ValidateDataSourceOptions(ShedduellerPostgresDataSourceOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.SchemaName))
        {
            throw new InvalidOperationException("ShedduellerPostgresDataSourceOptions.SchemaName must be a non-empty PostgreSQL schema name.");
        }

        if (options.SchemaName.Contains('\0', StringComparison.Ordinal))
        {
            throw new InvalidOperationException("ShedduellerPostgresDataSourceOptions.SchemaName cannot contain null characters.");
        }
    }

    private static void RegisterProviderServices(IServiceCollection services)
    {
        services.Replace(ServiceDescriptor.Singleton<PostgresOperationContext, PostgresOperationContext>());

        services.Replace(ServiceDescriptor.Singleton<PostgresJobStore, PostgresJobStore>());
        services.Replace(ServiceDescriptor.Singleton<IJobStore>(serviceProvider => serviceProvider.GetRequiredService<PostgresJobStore>()));
        services.Replace(ServiceDescriptor.Singleton<IJobEventSink>(serviceProvider => serviceProvider.GetRequiredService<PostgresJobStore>()));

        services.Replace(ServiceDescriptor.Singleton<PostgresInspectionReader, PostgresInspectionReader>());
        services.Replace(ServiceDescriptor.Singleton<IJobInspectionReader>(serviceProvider => serviceProvider.GetRequiredService<PostgresInspectionReader>()));
        services.Replace(ServiceDescriptor.Singleton<IScheduleInspectionReader>(serviceProvider => serviceProvider.GetRequiredService<PostgresInspectionReader>()));
        services.Replace(ServiceDescriptor.Singleton<IConcurrencyGroupInspectionReader>(serviceProvider => serviceProvider.GetRequiredService<PostgresInspectionReader>()));
        services.Replace(ServiceDescriptor.Singleton<INodeInspectionReader>(serviceProvider => serviceProvider.GetRequiredService<PostgresInspectionReader>()));
        services.Replace(ServiceDescriptor.Singleton<IMetricsInspectionReader>(serviceProvider => serviceProvider.GetRequiredService<PostgresInspectionReader>()));

        services.Replace(ServiceDescriptor.Singleton<PostgresRetentionStore, PostgresRetentionStore>());
        services.Replace(ServiceDescriptor.Singleton<IJobEventRetentionStore>(serviceProvider => serviceProvider.GetRequiredService<PostgresRetentionStore>()));
        services.Replace(ServiceDescriptor.Singleton<IJobRetentionStore>(serviceProvider => serviceProvider.GetRequiredService<PostgresRetentionStore>()));
        services.Replace(ServiceDescriptor.Singleton<IShedduellerCleanupConfigurationStore>(serviceProvider => serviceProvider.GetRequiredService<PostgresRetentionStore>()));

        services.Replace(ServiceDescriptor.Singleton<IPostgresMigrator, PostgresMigrator>());
        services.Replace(ServiceDescriptor.Singleton<IShedduellerWakeSignal, PostgresWakeSignal>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IShedduellerStartupValidator, PostgresStartupValidator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IShedduellerJobEventListener, PostgresJobEventListener>());
    }
}
