// spell-checker: ignore wakeup

namespace Sheddueller.Postgres.Internal;

using Microsoft.Extensions.Logging;

using Sheddueller.Runtime;

internal sealed class PostgresWakeSignal(
    ShedduellerPostgresOptions options,
    ILogger<PostgresWakeSignal> logger) : IShedduellerWakeSignal, IDisposable, IAsyncDisposable
{
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _disposeTokenSource = new();
    private readonly Lock _listenerLock = new();
    private readonly ShedduellerPostgresOptions _options = options;
    private Task? _listenTask;
    private bool _disposed;
    private int _signaled;

    public void Notify()
      => this.Signal();

    public async ValueTask WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        this.EnsureListening();
        if (await this._signal.WaitAsync(timeout, cancellationToken).ConfigureAwait(false))
        {
            Volatile.Write(ref this._signaled, 0);
        }
    }

    public void Dispose()
    {
        if (!this.TryStopListening(out var listenTask))
        {
            return;
        }

        listenTask?.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing).GetAwaiter().GetResult();
        this.ReleaseResources();
    }

    public async ValueTask DisposeAsync()
    {
        if (!this.TryStopListening(out var listenTask))
        {
            return;
        }

        if (listenTask is not null)
        {
            await listenTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        this.ReleaseResources();
    }

    private void EnsureListening()
    {
        lock (this._listenerLock)
        {
            if (this._listenTask is not null || this._disposed)
            {
                return;
            }

            var listenLoop = new PostgresListenLoop(
              this._options.DataSource,
              PostgresNames.WakeupChannel,
              this.OnNotification,
              this.OnListening,
              this.OnListenerRetrying,
              PostgresListenLoop.DefaultKeepAliveInterval,
              PostgresListenLoop.DefaultRetryDelay);
            var stoppingToken = this._disposeTokenSource.Token;
            this._listenTask = Task.Run(() => listenLoop.RunAsync(stoppingToken), CancellationToken.None);
        }
    }

    private bool TryStopListening(out Task? listenTask)
    {
        lock (this._listenerLock)
        {
            listenTask = this._listenTask;
            if (this._disposed)
            {
                return false;
            }

            this._disposed = true;
        }

        this._disposeTokenSource.Cancel();
        return true;
    }

    private void ReleaseResources()
    {
        this._signal.Dispose();
        this._disposeTokenSource.Dispose();
    }

    private void OnNotification(string payload)
    {
        if (string.Equals(payload, this._options.SchemaName, StringComparison.Ordinal))
        {
            this.Signal();
        }
    }

    private void OnListening()
    {
        logger.PostgresWakeListenerStarted(this._options.SchemaName);

        // NOTIFYs sent while no connection was listening (before the first LISTEN, or during an outage) are lost,
        // so wake waiting workers to re-check the store for work they may have missed.
        this.Signal();
    }

    private void OnListenerRetrying(Exception exception, TimeSpan retryDelay)
      => logger.PostgresWakeListenerRetrying(exception, this._options.SchemaName, (long)retryDelay.TotalMilliseconds);

    private void Signal()
    {
        if (Interlocked.Exchange(ref this._signaled, 1) == 0)
        {
            try
            {
                this._signal.Release();
            }
            catch (ObjectDisposedException)
            {
                // Disposal can race with callers notifying during host shutdown.
            }
        }
    }
}
