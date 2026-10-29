namespace Sheddueller.Postgres.Tests.Operations;

using System.Globalization;

using Microsoft.Extensions.DependencyInjection;

using Sheddueller.Storage;

using Shouldly;

public sealed class CleanupConfigurationOperationTests(PostgresFixture fixture)
{
    [Fact]
    public async Task GroupCleanup_SavedConfiguration_PreservesIndependentPolicy()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        var store = context.Provider.GetRequiredService<IShedduellerCleanupConfigurationStore>();
        var groups = new ConcurrencyGroupCleanupConfiguration(false, TimeSpan.FromDays(3), TimeSpan.FromMinutes(5), 25);
        var config = CreateConfiguration() with { ConcurrencyGroups = groups };
        await store.SetCleanupConfigurationAsync(config);
        (await store.GetConcurrencyGroupCleanupConfigurationAsync(ConcurrencyGroupCleanupConfiguration.Default)).ShouldBe(groups);
        (await store.GetCleanupConfigurationAsync(CreateConfiguration())).ShouldBe(config);
    }

    [Fact]
    public async Task GetCleanupConfiguration_MissingSettings_ReturnsDefaultsWithoutWriting()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        var store = context.Provider.GetRequiredService<IShedduellerCleanupConfigurationStore>();
        var defaults = CreateConfiguration(
          jobRetentionEnabled: true,
          completedRetention: TimeSpan.FromDays(2),
          failedRetention: TimeSpan.FromDays(3),
          canceledRetention: TimeSpan.FromDays(4),
          jobRetentionCleanupInterval: TimeSpan.FromMinutes(15),
          batchSize: 500,
          jobEventRetention: TimeSpan.FromHours(12),
          jobEventCleanupInterval: TimeSpan.FromMinutes(20),
          metricsRetention: TimeSpan.FromDays(10),
          metricsCleanupInterval: TimeSpan.FromMinutes(30));

        var configuration = await store.GetCleanupConfigurationAsync(defaults);

        configuration.ShouldBe(defaults);
        (await CountSettingsAsync(context)).ShouldBe(0);
    }

    [Fact]
    public async Task GetCleanupConfiguration_DefaultsChangeBeforeAnySave_ReturnsCurrentDefaults()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        var store = context.Provider.GetRequiredService<IShedduellerCleanupConfigurationStore>();
        await store.GetCleanupConfigurationAsync(CreateConfiguration(batchSize: 100));
        var changedDefaults = CreateConfiguration(batchSize: 250, metricsRetention: TimeSpan.FromDays(3));

        var configuration = await store.GetCleanupConfigurationAsync(changedDefaults);

        configuration.ShouldBe(changedDefaults);
        (await store.GetMetricsCleanupConfigurationAsync(MetricsCleanupConfiguration.Default)).ShouldBe(MetricsCleanupConfiguration.Default);
    }

    [Fact]
    public async Task SetCleanupConfiguration_ExistingSettings_ReturnsPersistedValues()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        var store = context.Provider.GetRequiredService<IShedduellerCleanupConfigurationStore>();
        await store.GetCleanupConfigurationAsync(CreateConfiguration());
        var updated = new ShedduellerCleanupConfiguration(
          new JobRetentionCleanupConfiguration(
            Enabled: false,
            CompletedRetention: null,
            FailedRetention: TimeSpan.FromHours(48),
            CanceledRetention: null,
            CleanupInterval: TimeSpan.FromMinutes(5),
            BatchSize: 50),
          new JobEventCleanupConfiguration(TimeSpan.FromHours(6), TimeSpan.FromMinutes(7)),
          new MetricsCleanupConfiguration(TimeSpan.FromHours(8), TimeSpan.FromMinutes(9)));

        await store.SetCleanupConfigurationAsync(updated);
        var configuration = await store.GetCleanupConfigurationAsync(CreateConfiguration(
          jobRetentionEnabled: true,
          completedRetention: TimeSpan.FromDays(30),
          failedRetention: TimeSpan.FromDays(30),
          canceledRetention: TimeSpan.FromDays(30),
          jobRetentionCleanupInterval: TimeSpan.FromHours(1),
          batchSize: 1000,
          jobEventRetention: TimeSpan.FromDays(30),
          jobEventCleanupInterval: TimeSpan.FromHours(1),
          metricsRetention: TimeSpan.FromDays(30),
          metricsCleanupInterval: TimeSpan.FromHours(1)));

        configuration.ShouldBe(updated);
        (await CountSettingsAsync(context)).ShouldBe(4);
    }

    [Fact]
    public async Task GetJobRetentionCleanupConfiguration_MissingSetting_ReturnsDefaultWithoutWriting()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        var store = context.Provider.GetRequiredService<IShedduellerCleanupConfigurationStore>();
        var defaults = CreateConfiguration().JobRetention;

        var configuration = await store.GetJobRetentionCleanupConfigurationAsync(defaults);

        configuration.ShouldBe(defaults);
        (await CountSettingsAsync(context)).ShouldBe(0);
    }

    [Fact]
    public async Task SetCleanupConfiguration_InvalidValue_Throws()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        var store = context.Provider.GetRequiredService<IShedduellerCleanupConfigurationStore>();
        var configuration = CreateConfiguration(batchSize: 0);

        var exception = await Should.ThrowAsync<InvalidOperationException>(() =>
          store.SetCleanupConfigurationAsync(configuration).AsTask());

        exception.Message.ShouldContain("batch size must be positive");
    }

    private static ShedduellerCleanupConfiguration CreateConfiguration(
        bool jobRetentionEnabled = true,
        TimeSpan? completedRetention = null,
        TimeSpan? failedRetention = null,
        TimeSpan? canceledRetention = null,
        TimeSpan? jobRetentionCleanupInterval = null,
        int batchSize = 100,
        TimeSpan? jobEventRetention = null,
        TimeSpan? jobEventCleanupInterval = null,
        TimeSpan? metricsRetention = null,
        TimeSpan? metricsCleanupInterval = null)
      => new(
        new JobRetentionCleanupConfiguration(
          jobRetentionEnabled,
          completedRetention ?? TimeSpan.FromDays(1),
          failedRetention ?? TimeSpan.FromDays(7),
          canceledRetention ?? TimeSpan.FromDays(7),
          jobRetentionCleanupInterval ?? TimeSpan.FromHours(1),
          batchSize),
        new JobEventCleanupConfiguration(
          jobEventRetention ?? TimeSpan.FromDays(7),
          jobEventCleanupInterval ?? TimeSpan.FromHours(1)),
        new MetricsCleanupConfiguration(
          metricsRetention ?? TimeSpan.FromDays(7),
          metricsCleanupInterval ?? TimeSpan.FromHours(1)));

    private static async ValueTask<int> CountSettingsAsync(PostgresTestContext context)
    {
        await using var command = context.DataSource.CreateCommand(
          $"select count(*) from {context.Table("settings")};");
        var count = await command.ExecuteScalarAsync();
        count.ShouldNotBeNull();

        return Convert.ToInt32(count, CultureInfo.InvariantCulture);
    }
}
