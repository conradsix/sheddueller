namespace Sheddueller.Dashboard.Components;

using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

using Sheddueller.Dashboard.Components.Ui;
using Sheddueller.Dashboard.Internal;
using Sheddueller.Storage;

public abstract class DashboardPageComponent : ComponentBase, IAsyncDisposable
{
    private readonly List<(string Title, string? Description, AlertTone Tone)> _pendingToasts = [];
    private Action? _unsubscribeJobEvents;
    private IDisposable? _shellRefreshRegistration;
    private DashboardLocationTracker? _locationTracker;
    private DashboardPeriodicRefresh? _periodicRefresh;
    private bool _disposed;

    [CascadingParameter(Name = "DashboardShellRefresh")]
    public object? ShellRefreshValue { get; set; }

    private protected DashboardLiveRefreshCoordinator LiveRefresh { get; private set; } = null!;

    [Inject]
    private IJSRuntime ToastJsRuntime { get; set; } = null!;

    private DashboardShellRefreshContext? ShellRefresh
      => this.ShellRefreshValue as DashboardShellRefreshContext;

    public async ValueTask DisposeAsync()
    {
        if (this._disposed)
        {
            return;
        }

        this._disposed = true;
        this._unsubscribeJobEvents?.Invoke();
        await this.DisposePageAsync().ConfigureAwait(false);
        this._locationTracker?.Dispose();
        this._shellRefreshRegistration?.Dispose();
        this.LiveRefresh?.Dispose();
        if (this._periodicRefresh is not null)
        {
            await this._periodicRefresh.DisposeAsync().ConfigureAwait(false);
        }

        GC.SuppressFinalize(this);
    }

    private protected virtual ValueTask DisposePageAsync()
      => ValueTask.CompletedTask;

    private protected void InitializeLiveRefresh(
        Func<CancellationToken, Task> refreshAsync,
        bool showRefreshing,
        Func<DateTimeOffset, DateTimeOffset?> lastUpdatedUtc,
        Func<CancellationToken, Task>? queuedRefreshAsync = null)
    {
        this.LiveRefresh = new DashboardLiveRefreshCoordinator(
          refreshAsync,
          this.ApplyPageStateAsync,
          autoRefreshEnabled: this.ShellRefresh?.AutoRefreshEnabled ?? true,
          queuedRefreshAsync: queuedRefreshAsync);
        this._shellRefreshRegistration = this.ShellRefresh?.Register(
          this.LiveRefresh,
          new DashboardShellRefreshOptions(showRefreshing, lastUpdatedUtc));
    }

    /// <summary>
    /// Passes live job events to <paramref name="onJobEvent"/> until the page is disposed. The handler runs on the
    /// publishing thread, so it must marshal page state changes onto the renderer (<see cref="ApplyPageStateAsync"/>).
    /// </summary>
    private protected void SubscribeToJobEvents(
        DashboardLiveUpdateStream liveUpdates,
        Func<JobEvent, CancellationToken, ValueTask> onJobEvent)
    {
        liveUpdates.JobEventPublished += onJobEvent;
        this._unsubscribeJobEvents += () => liveUpdates.JobEventPublished -= onJobEvent;
    }

    /// <summary>
    /// Refreshes the page on live job events until it is disposed: through the throttled queued refresh, and only
    /// while auto-refresh is on. Call after <see cref="InitializeLiveRefresh"/>.
    /// </summary>
    private protected void RefreshOnJobEvents(DashboardLiveUpdateStream liveUpdates)
      => this.SubscribeToJobEvents(
        liveUpdates,
        (jobEvent, _) =>
        {
            this.LiveRefresh.QueueLiveRefresh(jobEvent.OccurredAtUtc);
            return ValueTask.CompletedTask;
        });

    /// <summary>
    /// Queues a toast for the next <see cref="FlushToastsAsync"/>. Action results are shown once the page has
    /// rendered, because JS interop is only available after render.
    /// </summary>
    private protected void ShowToast(string title, string? description = null, AlertTone tone = AlertTone.Neutral)
      => this._pendingToasts.Add((title, description, tone));

    /// <summary>
    /// Shows queued toasts; call from <c>OnAfterRenderAsync</c>. A toast that can't be shown (script error or a
    /// disconnected circuit) is dropped.
    /// </summary>
    private protected async Task FlushToastsAsync()
    {
        if (this._pendingToasts.Count == 0)
        {
            return;
        }

        var toasts = this._pendingToasts.ToArray();
        this._pendingToasts.Clear();
        foreach (var (title, description, tone) in toasts)
        {
            try
            {
                await this.ToastJsRuntime.ToastAsync(title, description, tone);
            }
            catch (JSDisconnectedException)
            {
                return;
            }
            catch (JSException)
            {
            }
        }
    }

    /// <summary>
    /// Keeps the page's state in its URL. <paramref name="applyLocationAsync"/> runs on the renderer when the URL
    /// for <paramref name="path"/> changes without the page writing it (Back, Forward, or a link to the same page).
    /// </summary>
    private protected void TrackLocation(
        NavigationManager navigation,
        string path,
        Func<string, Task> applyLocationAsync)
      => this._locationTracker = new DashboardLocationTracker(
        navigation,
        path,
        location => _ = this.InvokeAsync(async () =>
        {
            if (this._disposed)
            {
                return;
            }

            await applyLocationAsync(location);
            if (!this._disposed)
            {
                this.StateHasChanged();
            }
        }));

    /// <summary>
    /// Writes page state to the URL: push (<paramref name="replace"/> = <see langword="false"/>) for discrete
    /// changes so Back steps through them, replace for typing.
    /// </summary>
    private protected void NavigateToLocation(
        string href,
        bool replace)
    {
        if (!this._disposed)
        {
            _ = this._locationTracker?.NavigateTo(href, replace);
        }
    }

    /// <summary>
    /// Refreshes the page every <paramref name="interval"/> while auto-refresh is on, until it is disposed. Call
    /// after <see cref="InitializeLiveRefresh"/>.
    /// </summary>
    private protected void StartPeriodicRefresh(TimeSpan interval)
      => this._periodicRefresh = new DashboardPeriodicRefresh(
        interval,
        async _ =>
        {
            if (this.LiveRefresh.AutoRefreshEnabled)
            {
                await this.LiveRefresh.RefreshNowAsync().ConfigureAwait(false);
            }
        });

    private protected async Task ApplyPageStateAsync(Action action)
    {
        if (this._disposed)
        {
            return;
        }

        await this.InvokeAsync(() =>
        {
            if (!this._disposed)
            {
                action();
                this.NotifyShellRefreshChanged();
                this.StateHasChanged();
            }
        }).ConfigureAwait(false);
    }

    private protected void NotifyShellRefreshChanged()
      => this.ShellRefresh?.NotifyChanged();
}
