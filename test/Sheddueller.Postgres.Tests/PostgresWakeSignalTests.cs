namespace Sheddueller.Postgres.Tests;

using System.Globalization;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using Sheddueller.Runtime;
using Sheddueller.Storage;

using Shouldly;

public sealed class PostgresWakeSignalTests(PostgresFixture fixture)
{
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task WakeSignal_PostgresNotify_WakesWaitingProvider()
    {
        await using var firstContext = await PostgresTestContext.CreateMigratedAsync(fixture);
        await using var secondProvider = PostgresTestContext.CreateProvider(fixture.DataSource, firstContext.SchemaName);
        var wakeSignal = firstContext.Provider.GetRequiredService<IShedduellerWakeSignal>();
        await WaitForListenerAsync(wakeSignal);
        var waitTask = WaitForSignalAsync(wakeSignal);

        await secondProvider.GetRequiredService<IJobStore>().EnqueueAsync(PostgresTestData.CreateRequest(Guid.NewGuid()));

        await waitTask;
    }

    [Fact]
    public async Task WakeSignal_PostgresNotifyFromBatch_WakesWaitingProvider()
    {
        await using var firstContext = await PostgresTestContext.CreateMigratedAsync(fixture);
        await using var secondProvider = PostgresTestContext.CreateProvider(fixture.DataSource, firstContext.SchemaName);
        var wakeSignal = firstContext.Provider.GetRequiredService<IShedduellerWakeSignal>();
        await WaitForListenerAsync(wakeSignal);
        var waitTask = WaitForSignalAsync(wakeSignal);

        await secondProvider.GetRequiredService<IJobStore>().EnqueueManyAsync([
          PostgresTestData.CreateRequest(Guid.NewGuid()),
          PostgresTestData.CreateRequest(Guid.NewGuid()),
        ]);

        await waitTask;
    }

    [Fact]
    public async Task WakeSignal_ListenerConnectionTerminated_ReconnectsAndWakesWaiters()
    {
        await using var context = await PostgresTestContext.CreateMigratedAsync(fixture);
        var applicationName = "sheddueller-wake-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        await using var listenerDataSource = NpgsqlDataSource.Create(
          new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { ApplicationName = applicationName }.ConnectionString);
        await using var provider = PostgresTestContext.CreateProvider(listenerDataSource, context.SchemaName);
        var wakeSignal = provider.GetRequiredService<IShedduellerWakeSignal>();
        await WaitForListenerAsync(wakeSignal);

        (await TerminateBackendsAsync(applicationName)).ShouldBe(1);

        // NOTIFYs sent while the listener was disconnected are lost, so reconnecting wakes waiters to re-check for work.
        await WaitForSignalAsync(wakeSignal);

        // The new connection is listening again.
        var waitTask = WaitForSignalAsync(wakeSignal);
        await context.Store.EnqueueAsync(PostgresTestData.CreateRequest(Guid.NewGuid()));
        await waitTask;
    }

    /// <summary>
    /// Waits for the signal raised once the listener's LISTEN succeeds, so later waits only see notifications.
    /// </summary>
    private static Task WaitForListenerAsync(IShedduellerWakeSignal wakeSignal)
      => WaitForSignalAsync(wakeSignal);

    private static async Task WaitForSignalAsync(IShedduellerWakeSignal wakeSignal)
    {
        // An infinite wait cancelled by a timeout fails the test instead of returning as if signalled.
        using var timeout = new CancellationTokenSource(SignalTimeout);
        await wakeSignal.WaitAsync(Timeout.InfiniteTimeSpan, timeout.Token);
    }

    private async Task<int> TerminateBackendsAsync(string applicationName)
    {
        // The select list runs only for rows that pass the filter, so only the named application's backends are terminated.
        await using var command = fixture.DataSource.CreateCommand(
          """
          select pg_terminate_backend(pid)
          from pg_stat_activity
          where application_name = @application_name;
          """);
        command.Parameters.AddWithValue("application_name", applicationName);

        var terminated = 0;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (reader.GetBoolean(0))
            {
                terminated++;
            }
        }

        return terminated;
    }
}
