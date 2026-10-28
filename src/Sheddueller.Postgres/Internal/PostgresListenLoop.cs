namespace Sheddueller.Postgres.Internal;

using Npgsql;

/// <summary>
/// Keeps one connection listening on a PostgreSQL notification channel until cancellation, reconnecting after failures.
/// </summary>
/// <param name="dataSource">The data source that provides the listening connection.</param>
/// <param name="channel">The channel to <c>LISTEN</c> on; must be a valid unquoted identifier.</param>
/// <param name="onNotification">Receives each notification payload. Runs on the connection's read path, so it must not block.</param>
/// <param name="onListening">Runs each time <c>LISTEN</c> succeeds on a new connection, including after a reconnect.</param>
/// <param name="onRetrying">Runs when the connection fails, with the failure and the delay before reconnecting.</param>
/// <param name="keepAliveInterval">How long the connection may stay silent before it is probed to detect a dead connection.</param>
/// <param name="retryDelay">How long to wait before reconnecting after a failure.</param>
internal sealed class PostgresListenLoop(
    NpgsqlDataSource dataSource,
    string channel,
    Action<string> onNotification,
    Action onListening,
    Action<Exception, TimeSpan> onRetrying,
    TimeSpan keepAliveInterval,
    TimeSpan retryDelay)
{
    public static readonly TimeSpan DefaultKeepAliveInterval = TimeSpan.FromSeconds(30);

    public static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Listens until <paramref name="cancellationToken"/> is canceled. Never throws; returns once canceled.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await this.ListenUntilDisconnectedAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                onRetrying(exception, retryDelay);

                // Shutdown during the delay ends the loop instead of surfacing an OperationCanceledException.
                await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }

    private async Task ListenUntilDisconnectedAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        connection.Notification += this.OnNotification;

        try
        {
            await ExecuteAsync(connection, $"listen {channel};", commandTimeout: null, cancellationToken).ConfigureAwait(false);
            onListening();

            var probeTimeoutSeconds = Math.Max(1, (int)Math.Ceiling(keepAliveInterval.TotalSeconds));
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!await connection.WaitAsync(keepAliveInterval, cancellationToken).ConfigureAwait(false))
                {
                    // A silently dropped TCP connection never completes WaitAsync, so a quiet connection is probed;
                    // the probe fails (or times out) on a dead connection, which sends the loop round to reconnect.
                    await ExecuteAsync(connection, "select 1;", probeTimeoutSeconds, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            connection.Notification -= this.OnNotification;
        }
    }

    private void OnNotification(object sender, NpgsqlNotificationEventArgs args)
      => onNotification(args.Payload);

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        string commandText,
        int? commandTimeout,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        if (commandTimeout is { } timeout)
        {
            command.CommandTimeout = timeout;
        }

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
