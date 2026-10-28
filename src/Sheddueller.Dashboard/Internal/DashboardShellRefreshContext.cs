namespace Sheddueller.Dashboard.Internal;

/// <summary>
/// Shell-level refresh state, owned by the layout for the lifetime of the circuit. Pages register their refresh
/// coordinator; the user's auto-refresh choice is remembered here so it survives navigation between pages.
/// </summary>
internal sealed class DashboardShellRefreshContext
{
    private readonly Lock _gate = new();
    private DashboardShellRefreshRegistration? _current;
    private bool _autoRefreshEnabled = true;

    public event Action? Changed;

    public DashboardShellRefreshRegistration? Current
    {
        get
        {
            lock (this._gate)
            {
                return this._current;
            }
        }
    }

    /// <summary>
    /// The user's auto-refresh preference, applied to every page that registers.
    /// </summary>
    public bool AutoRefreshEnabled
    {
        get
        {
            lock (this._gate)
            {
                return this._autoRefreshEnabled;
            }
        }
    }

    public IDisposable Register(
        DashboardLiveRefreshCoordinator coordinator,
        DashboardShellRefreshOptions options)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(options);

        var registration = new DashboardShellRefreshRegistration(this, coordinator, options);
        bool autoRefreshEnabled;
        lock (this._gate)
        {
            this._current = registration;
            autoRefreshEnabled = this._autoRefreshEnabled;
        }

        coordinator.ApplyAutoRefreshPreference(autoRefreshEnabled);
        this.NotifyChanged();
        return registration;
    }

    public async Task ToggleCurrentAutoRefreshAsync()
    {
        var current = this.Current;
        bool enabled;
        lock (this._gate)
        {
            enabled = !(current?.Coordinator.AutoRefreshEnabled ?? this._autoRefreshEnabled);
            this._autoRefreshEnabled = enabled;
        }

        if (current is not null)
        {
            await current.Coordinator.SetAutoRefreshEnabledAsync(enabled).ConfigureAwait(false);
        }

        this.NotifyChanged();
    }

    /// <summary>
    /// Manual "refresh now" for the current page, regardless of the auto-refresh preference.
    /// </summary>
    public async Task RefreshCurrentAsync()
    {
        var current = this.Current;
        if (current is null)
        {
            return;
        }

        await current.Coordinator.RefreshNowAsync().ConfigureAwait(false);
        this.NotifyChanged();
    }

    public void NotifyChanged()
      => this.Changed?.Invoke();

    internal void Unregister(DashboardShellRefreshRegistration registration)
    {
        lock (this._gate)
        {
            if (!ReferenceEquals(this._current, registration))
            {
                return;
            }

            this._current = null;
        }

        this.NotifyChanged();
    }
}

internal sealed record DashboardShellRefreshOptions(
    bool ShowRefreshing,
    Func<DateTimeOffset, DateTimeOffset?> LastUpdatedUtc);

internal sealed class DashboardShellRefreshRegistration(
    DashboardShellRefreshContext owner,
    DashboardLiveRefreshCoordinator coordinator,
    DashboardShellRefreshOptions options) : IDisposable
{
    public DashboardLiveRefreshCoordinator Coordinator { get; } = coordinator;

    public bool ShowRefreshing { get; } = options.ShowRefreshing;

    public DateTimeOffset? GetLastUpdatedUtc(DateTimeOffset nowUtc)
      => options.LastUpdatedUtc(nowUtc);

    public void Dispose()
      => owner.Unregister(this);
}
