namespace Sheddueller.Dashboard.Internal;

using System.Diagnostics.CodeAnalysis;

/// <summary>
/// Circuit-scoped clock shared by relative timestamps. One timer per circuit replaces a timer per timestamp;
/// the timer only runs while something is subscribed to <see cref="Ticked"/>.
/// </summary>
internal sealed class DashboardClock : IDisposable
{
    public static readonly TimeSpan DefaultTickInterval = TimeSpan.FromSeconds(15);

    private readonly Lock _gate = new();
    private readonly TimeProvider _timeProvider;
    private readonly List<Action> _handlers = [];
    private ITimer? _timer;
    private bool _disposed;

    public DashboardClock(TimeProvider timeProvider)
      : this(timeProvider, DefaultTickInterval)
    {
    }

    public DashboardClock(
        TimeProvider timeProvider,
        TimeSpan tickInterval)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(tickInterval, TimeSpan.Zero);

        this._timeProvider = timeProvider;
        this.TickInterval = tickInterval;
    }

    public event Action? Ticked
    {
        add
        {
            if (value is null)
            {
                return;
            }

            lock (this._gate)
            {
                if (this._disposed)
                {
                    return;
                }

                this._handlers.Add(value);
                this._timer ??= this._timeProvider.CreateTimer(
                  static state => ((DashboardClock)state!).OnTick(),
                  this,
                  this.TickInterval,
                  this.TickInterval);
            }
        }

        remove
        {
            if (value is null)
            {
                return;
            }

            ITimer? timerToDispose = null;
            lock (this._gate)
            {
                _ = this._handlers.Remove(value);
                if (this._handlers.Count == 0)
                {
                    timerToDispose = this._timer;
                    this._timer = null;
                }
            }

            timerToDispose?.Dispose();
        }
    }

    public TimeSpan TickInterval { get; }

    public DateTimeOffset UtcNow
      => this._timeProvider.GetUtcNow();

    public int SubscriberCount
    {
        get
        {
            lock (this._gate)
            {
                return this._handlers.Count;
            }
        }
    }

    public bool IsTimerRunning
    {
        get
        {
            lock (this._gate)
            {
                return this._timer is not null;
            }
        }
    }

    public void Dispose()
    {
        ITimer? timerToDispose;
        lock (this._gate)
        {
            if (this._disposed)
            {
                return;
            }

            this._disposed = true;
            this._handlers.Clear();
            timerToDispose = this._timer;
            this._timer = null;
        }

        timerToDispose?.Dispose();
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A failing timestamp subscriber must not stop other subscribers or crash the timer thread.")]
    private void OnTick()
    {
        Action[] handlers;
        lock (this._gate)
        {
            if (this._disposed || this._handlers.Count == 0)
            {
                return;
            }

            handlers = [.. this._handlers];
        }

        foreach (var handler in handlers)
        {
            try
            {
                handler();
            }
            catch (Exception)
            {
            }
        }
    }
}
