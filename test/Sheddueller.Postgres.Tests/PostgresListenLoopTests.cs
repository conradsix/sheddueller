namespace Sheddueller.Postgres.Tests;

using System.Collections.Concurrent;
using System.Globalization;

using Npgsql;

using Sheddueller.Postgres.Internal;

using Shouldly;

public sealed class PostgresListenLoopTests(PostgresFixture fixture)
{
    [Fact]
    public async Task ListenLoop_QuietPastKeepAliveInterval_ProbesConnectionAndKeepsListening()
    {
        var applicationName = CreateUniqueName("sheddueller-listen-");
        var channel = CreateUniqueName("sheddueller_test_");
        await using var dataSource = NpgsqlDataSource.Create(
          new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { ApplicationName = applicationName }.ConnectionString);
        var payloads = new ConcurrentQueue<string>();
        var listeningCount = 0;
        var retryCount = 0;
        var listenLoop = new PostgresListenLoop(
          dataSource,
          channel,
          payloads.Enqueue,
          () => Interlocked.Increment(ref listeningCount),
          (_, _) => Interlocked.Increment(ref retryCount),
          keepAliveInterval: TimeSpan.FromMilliseconds(200),
          retryDelay: TimeSpan.FromMilliseconds(100));
        using var stopping = new CancellationTokenSource();
        var runTask = listenLoop.RunAsync(stopping.Token);
        await WaitUntilAsync(() => Volatile.Read(ref listeningCount) == 1);

        // Several keep-alive intervals pass with nothing to read.
        await Task.Delay(TimeSpan.FromSeconds(1));
        (await this.ReadLastQueryAsync(applicationName)).ShouldNotBeNull().ShouldStartWith("select 1");

        await this.NotifyAsync(channel, "after-quiet");
        await WaitUntilAsync(() => payloads.Contains("after-quiet"));

        Volatile.Read(ref listeningCount).ShouldBe(1);
        Volatile.Read(ref retryCount).ShouldBe(0);
        await stopping.CancelAsync();
        await runTask.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task ListenLoop_ShutdownDuringReconnectDelay_ReturnsWithoutThrowing()
    {
        // Nothing listens on port 1, so every connection attempt fails and the loop waits to reconnect.
        await using var unreachableDataSource = NpgsqlDataSource.Create(
          new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Port = 1, Timeout = 2 }.ConnectionString);
        var retryCount = 0;
        var listenLoop = new PostgresListenLoop(
          unreachableDataSource,
          CreateUniqueName("sheddueller_test_"),
          _ => { },
          () => { },
          (_, _) => Interlocked.Increment(ref retryCount),
          PostgresListenLoop.DefaultKeepAliveInterval,
          retryDelay: TimeSpan.FromMinutes(5));
        using var stopping = new CancellationTokenSource();
        var runTask = listenLoop.RunAsync(stopping.Token);
        await WaitUntilAsync(() => Volatile.Read(ref retryCount) == 1);

        await stopping.CancelAsync();

        await runTask.WaitAsync(TimeSpan.FromSeconds(10));
        runTask.IsCompletedSuccessfully.ShouldBeTrue();
    }

    [Fact]
    public async Task ListenLoop_PooledConnectionStillListening_IgnoresNotificationsSentBeforeListen()
    {
        var channel = CreateUniqueName("sheddueller_test_");

        // A single pooled connection, so the loop reuses the one an earlier listener returned to the pool.
        await using var dataSource = NpgsqlDataSource.Create(
          new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { MaxPoolSize = 1 }.ConnectionString);
        await using (var earlierListener = await dataSource.OpenConnectionAsync())
        {
            await using var listen = PostgresCommands.Create(earlierListener, transaction: null, $"listen {channel};");
            await listen.ExecuteNonQueryAsync();
        }

        // Npgsql only resets a pooled connection when it is next used, so this reaches the idle connection.
        await this.NotifyAsync(channel, "before-listen");

        var payloads = new ConcurrentQueue<string>();
        var listeningCount = 0;
        var listenLoop = new PostgresListenLoop(
          dataSource,
          channel,
          payloads.Enqueue,
          () => Interlocked.Increment(ref listeningCount),
          (_, _) => { },
          PostgresListenLoop.DefaultKeepAliveInterval,
          retryDelay: TimeSpan.FromMilliseconds(100));
        using var stopping = new CancellationTokenSource();
        var runTask = listenLoop.RunAsync(stopping.Token);
        await WaitUntilAsync(() => Volatile.Read(ref listeningCount) == 1);

        await this.NotifyAsync(channel, "after-listen");
        await WaitUntilAsync(() => payloads.Contains("after-listen"));

        payloads.ShouldBe(["after-listen"]);
        await stopping.CancelAsync();
        await runTask.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private async ValueTask<string?> ReadLastQueryAsync(string applicationName)
    {
        await using var command = fixture.DataSource.CreateCommand(
          "select query from pg_stat_activity where application_name = @application_name;");
        command.Parameters.AddWithValue("application_name", applicationName);
        return await command.ExecuteScalarAsync() as string;
    }

    private async ValueTask NotifyAsync(string channel, string payload)
    {
        await using var command = fixture.DataSource.CreateCommand("select pg_notify(@channel, @payload);");
        command.Parameters.AddWithValue("channel", channel);
        command.Parameters.AddWithValue("payload", payload);
        await command.ExecuteNonQueryAsync();
    }

    private static string CreateUniqueName(string prefix)
      => prefix + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20), timeout.Token);
        }
    }
}
