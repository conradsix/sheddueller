namespace Sheddueller.Dashboard.Internal;

using System.Diagnostics.CodeAnalysis;

/// <summary>
/// Runs an action once input has paused: each <see cref="Schedule"/> restarts the wait, so a burst of typing runs
/// only the last scheduled action.
/// </summary>
internal sealed class DashboardDebouncer : IDisposable
{
    private readonly TimeSpan _delay;
    private readonly TimeProvider _timeProvider;

    [SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "The run that the source belongs to disposes it when it ends; Dispose cancels it so that run ends.")]
    private CancellationTokenSource? _pending;

    public DashboardDebouncer(
        TimeSpan delay,
        TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(delay, TimeSpan.Zero);

        this._delay = delay;
        this._timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// An action is waiting for the delay to pass.
    /// </summary>
    public bool IsPending
      => this._pending is not null;

    /// <summary>
    /// Runs <paramref name="action"/> after the delay, replacing any action still waiting.
    /// </summary>
    public void Schedule(Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        this._pending?.Cancel();

        var next = new CancellationTokenSource();
        this._pending = next;
        _ = this.RunAsync(next, action);
    }

    /// <summary>
    /// Drops the waiting action, if any. An action that has already started runs to completion.
    /// </summary>
    public void Cancel()
    {
        var pending = this._pending;
        this._pending = null;
        pending?.Cancel();
    }

    public void Dispose()
      => this.Cancel();

    private async Task RunAsync(
        CancellationTokenSource cancellationTokenSource,
        Func<Task> action)
    {
        // Stays on the caller's context (the component's renderer), so the pending action is only ever touched there.
        try
        {
            await Task.Delay(this._delay, this._timeProvider, cancellationTokenSource.Token);
            await action();
        }
        catch (OperationCanceledException) when (cancellationTokenSource.IsCancellationRequested)
        {
        }
        finally
        {
            if (ReferenceEquals(this._pending, cancellationTokenSource))
            {
                this._pending = null;
            }

            cancellationTokenSource.Dispose();
        }
    }
}
