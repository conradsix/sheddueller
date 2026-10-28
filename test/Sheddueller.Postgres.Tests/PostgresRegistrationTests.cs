namespace Sheddueller.Postgres.Tests;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;

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

using Shouldly;

public sealed class PostgresRegistrationTests
{
    private const string TestConnectionString = "Host=127.0.0.1;Port=1;Username=postgres;Password=postgres;Database=sheddueller;Timeout=1";

    [Fact]
    public async Task UsePostgres_ConnectionString_RegistersProviderServices()
    {
        var services = new ServiceCollection();
        services.AddSheddueller(builder => builder.UsePostgres(TestConnectionString));

        await using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<ShedduellerPostgresOptions>().SchemaName.ShouldBe("sheddueller");
        provider.GetRequiredService<PostgresOperationContext>().Options.ShouldBeSameAs(provider.GetRequiredService<ShedduellerPostgresOptions>());
        provider.GetRequiredService<IJobStore>().ShouldBeSameAs(provider.GetRequiredService<PostgresJobStore>());
        provider.GetRequiredService<IJobEventSink>().ShouldBeSameAs(provider.GetRequiredService<PostgresJobStore>());
        provider.GetRequiredService<IJobInspectionReader>().ShouldBeSameAs(provider.GetRequiredService<PostgresInspectionReader>());
        provider.GetRequiredService<IScheduleInspectionReader>().ShouldBeSameAs(provider.GetRequiredService<PostgresInspectionReader>());
        provider.GetRequiredService<IConcurrencyGroupInspectionReader>().ShouldBeSameAs(provider.GetRequiredService<PostgresInspectionReader>());
        provider.GetRequiredService<INodeInspectionReader>().ShouldBeSameAs(provider.GetRequiredService<PostgresInspectionReader>());
        provider.GetRequiredService<IMetricsInspectionReader>().ShouldBeSameAs(provider.GetRequiredService<PostgresInspectionReader>());
        provider.GetRequiredService<IJobRetentionStore>().ShouldBeSameAs(provider.GetRequiredService<PostgresRetentionStore>());
        provider.GetRequiredService<IJobEventRetentionStore>().ShouldBeSameAs(provider.GetRequiredService<PostgresRetentionStore>());
        provider.GetRequiredService<IShedduellerCleanupConfigurationStore>().ShouldBeSameAs(provider.GetRequiredService<PostgresRetentionStore>());
        provider.GetRequiredService<IPostgresMigrator>().ShouldBeOfType<PostgresMigrator>();
        provider.GetRequiredService<IShedduellerWakeSignal>().ShouldBeOfType<PostgresWakeSignal>();
    }

    [Fact]
    public async Task UsePostgres_ServiceProviderFactory_CanReadRegisteredServices()
    {
        var schemaName = "sheddueller_" + Guid.NewGuid().ToString("N");
        var services = new ServiceCollection();
        services.AddSingleton(new PostgresRegistrationSettings(TestConnectionString, schemaName));
        services.AddSheddueller(builder => builder.UsePostgres(
          serviceProvider => serviceProvider.GetRequiredService<PostgresRegistrationSettings>().ConnectionString,
          (serviceProvider, options) =>
          {
              options.SchemaName = serviceProvider.GetRequiredService<PostgresRegistrationSettings>().SchemaName;
          }));

        await using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<ShedduellerPostgresOptions>().SchemaName.ShouldBe(schemaName);
    }

    [Fact]
    public async Task UsePostgres_ConfigureDataSourceBuilder_IsInvoked()
    {
        var configured = false;
        var services = new ServiceCollection();
        services.AddSheddueller(builder => builder.UsePostgres(
          TestConnectionString,
          options =>
          {
              options.ConfigureDataSourceBuilder = _ => configured = true;
          }));

        await using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<ShedduellerPostgresOptions>().ShouldNotBeNull();
        configured.ShouldBeTrue();
    }

    [Fact]
    public async Task UsePostgres_ConnectionString_DisposesOwnedDataSourceWithProvider()
    {
        var services = new ServiceCollection();
        services.AddSheddueller(builder => builder.UsePostgres(TestConnectionString));
        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<ShedduellerPostgresOptions>();

        await provider.DisposeAsync();

        await Should.ThrowAsync<ObjectDisposedException>(async () =>
        {
            await using var connection = await options.DataSource.OpenConnectionAsync();
        });
    }

    [Fact]
    public async Task UsePostgres_CallerOwnedDataSource_IsNotDisposedWithProvider()
    {
        await using var dataSource = NpgsqlDataSource.Create(TestConnectionString);
        var services = new ServiceCollection();
        services.AddSheddueller(builder => builder.UsePostgres(options =>
        {
            options.DataSource = dataSource;
        }));
        var provider = services.BuildServiceProvider();

        provider.GetRequiredService<ShedduellerPostgresOptions>().DataSource.ShouldBeSameAs(dataSource);

        await provider.DisposeAsync();

        await Should.ThrowAsync<NpgsqlException>(async () =>
        {
            await using var connection = await dataSource.OpenConnectionAsync();
        });
    }

    private sealed record PostgresRegistrationSettings(
        string ConnectionString,
        string SchemaName);
}
