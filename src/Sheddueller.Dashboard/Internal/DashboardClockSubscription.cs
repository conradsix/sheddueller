namespace Sheddueller.Dashboard.Internal;

/// <summary>
/// Keeps one component's "now" in step with the circuit's <see cref="DashboardClock"/>, for text that counts with
/// time such as "5m ago". The time is read when the component's parameters are set; once started, each clock tick
/// reads it again and re-renders the component, until the subscription is disposed.
/// </summary>
internal sealed class DashboardClockSubscription : IDisposable
{
    private readonly DashboardClock _clock;
    private readonly Func<Action, Task> _invokeAsync;
    private readonly Action _stateHasChanged;
    private bool _subscribed;
    private bool _disposed;

    /// <param name="clock">The circuit clock.</param>
    /// <param name="invokeAsync">The component's <c>InvokeAsync</c>, so ticks update it on its renderer.</param>
    /// <param name="stateHasChanged">The component's <c>StateHasChanged</c>.</param>
    public DashboardClockSubscription(
        DashboardClock clock,
        Func<Action, Task> invokeAsync,
        Action stateHasChanged)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(invokeAsync);
        ArgumentNullException.ThrowIfNull(stateHasChanged);

        this._clock = clock;
        this._invokeAsync = invokeAsync;
        this._stateHasChanged = stateHasChanged;
        this.NowUtc = clock.UtcNow;
    }

    public DateTimeOffset NowUtc { get; private set; }

    /// <summary>
    /// Reads the clock again; call when the component's parameters are set.
    /// </summary>
    public void Refresh()
      => this.NowUtc = this._clock.UtcNow;

    /// <summary>
    /// Starts following the clock; call after the component's first render. One clock timer serves every
    /// subscribed component on the page.
    /// </summary>
    public void Start()
    {
        if (this._subscribed || this._disposed)
        {
            return;
        }

        this._clock.Ticked += this.OnTicked;
        this._subscribed = true;
    }

    public void Dispose()
    {
        this._disposed = true;
        if (this._subscribed)
        {
            this._clock.Ticked -= this.OnTicked;
            this._subscribed = false;
        }
    }

    private void OnTicked()
      => _ = this._invokeAsync(() =>
      {
          if (!this._disposed)
          {
              this.NowUtc = this._clock.UtcNow;
              this._stateHasChanged();
          }
      });
}
