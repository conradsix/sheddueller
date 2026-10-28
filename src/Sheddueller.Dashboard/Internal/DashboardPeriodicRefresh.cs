namespace Sheddueller.Dashboard.Internal;

/// <summary>
/// Calls a refresh on a fixed interval until disposed. Disposing stops the timer, cancels the token passed to a
/// refresh still running, and waits for it to finish.
/// </summary>
internal sealed class DashboardPeriodicRefresh : IAsyncDisposable
{
    private readonly CancellationTokenSource _cancellation = new();
    private readonly PeriodicTimer _timer;
    private readonly Task _loop;
    private bool _disposed;

    public DashboardPeriodicRefresh(
        TimeSpan interval,
        Func<CancellationToken, Task> refreshAsync,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(refreshAsync);

        this._timer = new PeriodicTimer(interval, timeProvider ?? TimeProvider.System);
        this._loop = this.RunAsync(refreshAsync, this._cancellation.Token);
    }

    public async ValueTask DisposeAsync()
    {
        if (this._disposed)
        {
            return;
        }

        this._disposed = true;
        await this._cancellation.CancelAsync().ConfigureAwait(false);
        this._timer.Dispose();
        await this._loop.ConfigureAwait(false);
        this._cancellation.Dispose();
    }

    private async Task RunAsync(
        Func<CancellationToken, Task> refreshAsync,
        CancellationToken cancellationToken)
    {
        try
        {
            while (await this._timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await refreshAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }
}
