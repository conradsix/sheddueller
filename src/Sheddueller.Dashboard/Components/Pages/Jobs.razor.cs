namespace Sheddueller.Dashboard.Components.Pages;

using System.Diagnostics.CodeAnalysis;
using System.Globalization;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

using Sheddueller;
using Sheddueller.Dashboard;
using Sheddueller.Dashboard.Internal;
using Sheddueller.Inspection.Jobs;
using Sheddueller.Storage;

[SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Dashboard operation failures are displayed without terminating the rendering circuit.")]
[SuppressMessage("Naming", "CA1724:Type names should not match namespaces", Justification = "Preserve the existing public Razor component name.")]
public partial class Jobs
{
    private static readonly TimeSpan FilterDebounce = TimeSpan.FromMilliseconds(300);
    private const int DefaultPageSize = 25;
    private const string ClearQueuedConfirmationWord = "delete";
    private const string ClearQueuedPrompt = "This will cancel all queued, delayed, and retry-waiting jobs while preserving historical records.\n\nType delete to continue.";

    private readonly DashboardJobFilters _filters = new();
    private DashboardJobListController _query = null!;
    private DashboardJobViewController _viewController = null!;
    private readonly DashboardJobColumnController _columnController = new();
    private DotNetObjectReference<Jobs>? _columnResizeReceiver;
    private ElementReference _columnsButton;
    private CancellationTokenSource? _filterDebounceCts;
    private string? _actionMessage;
    private string? _pendingFilterNavigationUri;
    private JobsActionSeverity _actionSeverity = JobsActionSeverity.Success;
    private bool _isClearQueuedRunning;
    private bool _isColumnEditorOpen;
    private bool _hasExplicitInitialQuery;
    private bool _columnResizingRegistered;
    private bool _viewStorageLoaded;

    private IReadOnlyList<DashboardResolvedJobView> SharedViews
      => [.. this._viewController.Views.Where(view => view.Source == DashboardJobViewSource.Shared)];

    private IReadOnlyList<DashboardResolvedJobView> PersonalViews
      => [.. this._viewController.Views.Where(view => view.Source == DashboardJobViewSource.Personal)];

    private DashboardResolvedJobView? CurrentView
      => this._viewController.FindView(this._viewController.SelectedKey);

    private bool CanSaveCurrentView
      => this._viewController.IsDirty && this.CurrentView?.Source == DashboardJobViewSource.Personal;

    private bool IsCurrentViewPreferred
      => string.Equals(this._viewController.SelectedKey, this._viewController.PreferredKey, StringComparison.Ordinal);

    private bool CanSetPreferredView
      => !this._viewController.IsDirty && !this.IsCurrentViewPreferred;

    private string CurrentViewStatus
    {
        get
        {
            var name = this.CurrentView?.Name ?? "Custom";
            if (this._viewController.IsDirty)
            {
                return string.Concat(name, " · Unsaved changes");
            }

            return this.IsCurrentViewPreferred && this.CurrentView?.Source != DashboardJobViewSource.BuiltIn
              ? string.Concat(name, " · Default view")
              : name;
        }
    }

    private IReadOnlyList<ShedduellerDashboardJobColumnKind> HiddenBuiltInColumns
      =>
      [
          .. Enum.GetValues<ShedduellerDashboardJobColumnKind>()
            .Where(kind => kind != ShedduellerDashboardJobColumnKind.Tag)
            .Where(kind => this._columnController.Columns.All(column => column.Kind != kind)),
      ];

    private IReadOnlyList<string> ObservedTagNames
      =>
      [
          .. this._query.Jobs.SelectMany(job => job.Tags)
            .Select(tag => tag.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase),
      ];

    private string TableStyle
    {
        get
        {
            var width = DashboardJobColumnWidths.Total(this._columnController.Columns);
            return string.Create(CultureInfo.InvariantCulture, $"width: {width}px; min-width: {width}px");
        }
    }

    private string HeaderText
      => this._query.Page is null ? "Reading scheduler job records." : this.ResultRangeText;

    private string EmptyText
      => this._filters.HasAppliedFilters
        ? "No jobs matched the current filters."
        : "No jobs are available.";

    private string ActionAlertClass
      => this._actionSeverity switch
      {
          JobsActionSeverity.Success => "jobs-inline-alert jobs-inline-alert--success",
          JobsActionSeverity.Warning => "jobs-inline-alert jobs-inline-alert--warning",
          _ => "jobs-inline-alert jobs-inline-alert--error",
      };

    private string ActionAlertIcon
      => this._actionSeverity switch
      {
          JobsActionSeverity.Success => "check_circle",
          JobsActionSeverity.Warning => "info",
          _ => "warning",
      };

    private bool IsClearQueuedDisabled
      => this._isClearQueuedRunning || this._query.IsLoading || this._query.IsLoadingMore;

    private string ClearQueuedButtonIcon
      => this._isClearQueuedRunning ? "progress_activity" : "cancel";

    private string ClearQueuedButtonText
      => this._isClearQueuedRunning ? "Clearing Queued" : "Clear Queued";

    private static string ClearQueuedButtonTitle
      => "Cancel all queued, delayed, and retry-waiting jobs. Type delete to confirm.";

    private string ResultRangeText
    {
        get
        {
            var loadedCount = this._query.Jobs.Count;
            var totalCount = this._query.Page?.TotalCount ?? loadedCount;
            var filterText = this._filters.HasAppliedFilters ? " matching filters" : string.Empty;
            var moreText = this._query.Page?.ContinuationToken is null ? string.Empty : " with more available";

            return loadedCount == 0
              ? string.Create(CultureInfo.InvariantCulture, $"Showing 0 of {DashboardFormat.Count(totalCount)} jobs{filterText}")
              : string.Create(CultureInfo.InvariantCulture, $"Showing 1-{DashboardFormat.Count(loadedCount)} of {DashboardFormat.Count(totalCount)} jobs{filterText}{moreText}");
        }
    }

    protected override async Task OnInitializedAsync()
    {
        this._query = new DashboardJobListController(
          this.Reader, DefaultPageSize,
          this.ClearQueryRefreshError,
          this.MarkQueryUpdated,
          this.NotifyShellRefreshChanged,
          this.ApplyPageStateAsync);
        this.Navigation.LocationChanged += this.OnLocationChanged;
        this._viewController = new DashboardJobViewController(this.DashboardOptions);
        this.ReplaceColumns(this._viewController.Initialize());
        this._hasExplicitInitialQuery = DashboardJobViews.HasRecognizedQuery(this.Navigation.Uri);
        _ = this._filters.ReplaceWith(this._hasExplicitInitialQuery
          ? DashboardJobFilterQuery.ParseUri(this.Navigation.Uri)
          : DashboardJobViews.CreateFilters(this.CurrentView!.Definition));
        this._viewController.IsDirty = this._hasExplicitInitialQuery;
        this.InitializeLiveRefresh(
          this.RefreshCurrentQueryAsync,
          showRefreshing: true,
          lastUpdatedUtc: nowUtc => this.LiveRefresh.LastUpdatedUtc ?? (this._query.Page is null ? null : nowUtc));
        this.LiveUpdates.JobEventPublished += this.OnJobEventPublishedAsync;
        await this.LoadAsync();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!this._columnResizingRegistered)
        {
            this._columnResizeReceiver ??= DotNetObjectReference.Create(this);
            await this.JsRuntime.InvokeVoidAsync(
              "shedduellerJobViews.registerTableColumnResizing",
              this._columnResizeReceiver);
            this._columnResizingRegistered = true;
        }

        if (firstRender && !this._viewStorageLoaded)
        {
            this._viewStorageLoaded = true;
            await this.LoadStoredViewsAsync();
        }
    }

    private protected override async ValueTask DisposePageAsync()
    {
        this.Navigation.LocationChanged -= this.OnLocationChanged;
        this.LiveUpdates.JobEventPublished -= this.OnJobEventPublishedAsync;
        this.CancelPendingFilterLoad();
        if (this._columnResizingRegistered)
        {
            try
            {
                await this.JsRuntime.InvokeVoidAsync("shedduellerJobViews.unregisterTableColumnResizing");
            }
            catch (JSDisconnectedException)
            {
            }
        }

        this._columnResizeReceiver?.Dispose();
    }

    private async Task LoadStoredViewsAsync()
    {
        try
        {
            var payload = await this.JsRuntime.InvokeAsync<DashboardJobViewStoragePayload?>(
              "shedduellerJobViews.load",
              this.ViewStorageKey());
            if (payload is null)
            {
                return;
            }

            if (payload.Version != DashboardJobViewStoragePayload.CurrentVersion)
            {
                this.SetActionWarning("Saved job views use an unsupported format and were ignored.");
                return;
            }

            var invalidViewFound = false;
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var names = this._viewController.Views.Select(view => view.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var storedView in payload.Views ?? [])
            {
                if (storedView is null
                    || !Guid.TryParseExact(storedView.Id, "D", out _)
                    || !ids.Add(storedView.Id)
                    || !DashboardJobViews.IsValid(storedView.View)
                    || !names.Add(storedView.View.Name.Trim()))
                {
                    invalidViewFound = true;
                    continue;
                }

                storedView.View = storedView.View with
                {
                    Name = storedView.View.Name.Trim(),
                };
                this._viewController.StoredViews.Add(storedView);
            }

            this._viewController.RebuildPersonalViews();
            if (!string.IsNullOrWhiteSpace(payload.PreferredViewKey)
                && this._viewController.FindView(payload.PreferredViewKey) is { } preferredView)
            {
                this._viewController.PreferredKey = preferredView.Key;
                this._viewController.SelectedKey = preferredView.Key;
                this.ReplaceColumns(DashboardJobViews.GetColumns(preferredView.Definition));

                if (this._hasExplicitInitialQuery)
                {
                    this._viewController.IsDirty = true;
                }
                else
                {
                    _ = this._filters.ReplaceWith(DashboardJobViews.CreateFilters(preferredView.Definition));
                    this._viewController.IsDirty = false;
                    this.NavigateToCurrentFilters();
                    await this.LoadAsync();
                }
            }

            if (invalidViewFound)
            {
                this.SetActionWarning("One or more invalid saved job views were ignored.");
            }

            this.StateHasChanged();
        }
        catch (JSException)
        {
            this.SetActionWarning("Personal job views are unavailable because browser storage could not be read.");
            this.StateHasChanged();
        }
        catch (InvalidOperationException)
        {
            this.SetActionWarning("Personal job views are unavailable in this rendering mode.");
            this.StateHasChanged();
        }
    }

    private async Task SetViewAsync(ChangeEventArgs eventArgs)
    {
        if (eventArgs.Value is not string key || this._viewController.FindView(key) is not { } nextView)
        {
            return;
        }

        if (this._viewController.IsDirty
            && !await this.JsRuntime.InvokeAsync<bool>("confirm", "Discard unsaved changes to the current job view?"))
        {
            this.StateHasChanged();
            return;
        }

        await this.ActivateViewAsync(nextView, applyQuery: true);
    }

    private async Task ActivateViewAsync(
        DashboardResolvedJobView view,
        bool applyQuery)
    {
        this.CancelPendingFilterLoad();
        this._viewController.SelectedKey = view.Key;
        this.ReplaceColumns(DashboardJobViews.GetColumns(view.Definition));
        this._viewController.IsDirty = false;
        this.ClearActionAlert();

        if (applyQuery)
        {
            _ = this._filters.ReplaceWith(DashboardJobViews.CreateFilters(view.Definition));
            this.NavigateToCurrentFilters();
            await this.LoadAsync();
        }
    }

    private async Task SaveCurrentViewAsync()
    {
        if (!this.CanSaveCurrentView || this._viewController.FindStoredView(this._viewController.SelectedKey) is not { } storedView)
        {
            return;
        }

        storedView.View = DashboardJobViews.CreateView(storedView.View.Name, this._filters, this._columnController.Columns);
        this._viewController.RebuildPersonalViews();
        this._viewController.IsDirty = false;
        await this.PersistStoredViewsAsync("Saved job view.");
    }

    private async Task SaveViewAsAsync()
    {
        var suggestedName = this.CurrentView?.Source == DashboardJobViewSource.BuiltIn
          ? "My View"
          : string.Concat(this.CurrentView?.Name ?? "Job View", " Copy");
        var name = await this.JsRuntime.InvokeAsync<string?>("prompt", "Save job view as:", suggestedName);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var normalizedName = name.Trim();
        if (!this._viewController.IsViewNameAvailable(normalizedName, exceptKey: null))
        {
            this.SetActionError("A job view with that name already exists.");
            return;
        }

        var id = Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture);
        var storedView = new DashboardStoredJobView
        {
            Id = id,
            View = DashboardJobViews.CreateView(normalizedName, this._filters, this._columnController.Columns),
        };
        this._viewController.StoredViews.Add(storedView);
        this._viewController.RebuildPersonalViews();
        this._viewController.SelectedKey = DashboardJobViews.PersonalKey(id);
        this._viewController.IsDirty = false;
        await this.PersistStoredViewsAsync("Saved personal job view.");
    }

    private async Task RenameCurrentViewAsync()
    {
        if (this._viewController.FindStoredView(this._viewController.SelectedKey) is not { } storedView)
        {
            return;
        }

        var name = await this.JsRuntime.InvokeAsync<string?>("prompt", "Rename job view:", storedView.View.Name);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var normalizedName = name.Trim();
        if (!this._viewController.IsViewNameAvailable(normalizedName, this._viewController.SelectedKey))
        {
            this.SetActionError("A job view with that name already exists.");
            return;
        }

        storedView.View = storedView.View with
        {
            Name = normalizedName,
        };
        this._viewController.RebuildPersonalViews();
        await this.PersistStoredViewsAsync("Renamed personal job view.");
    }

    private async Task DeleteCurrentViewAsync()
    {
        if (this._viewController.FindStoredView(this._viewController.SelectedKey) is not { } storedView
            || !await this.JsRuntime.InvokeAsync<bool>("confirm", string.Concat("Delete job view ‘", storedView.View.Name, "’?")))
        {
            return;
        }

        var deletedKey = DashboardJobViews.PersonalKey(storedView.Id);
        _ = this._viewController.StoredViews.Remove(storedView);
        this._viewController.RebuildPersonalViews();
        if (string.Equals(this._viewController.PreferredKey, deletedKey, StringComparison.Ordinal))
        {
            this._viewController.PreferredKey = this._viewController.ConfiguredDefaultView().Key;
        }

        await this.ActivateViewAsync(this._viewController.FindView(this._viewController.PreferredKey) ?? this._viewController.ConfiguredDefaultView(), applyQuery: true);
        await this.PersistStoredViewsAsync("Deleted personal job view.");
    }

    private async Task SetPreferredViewAsync()
    {
        if (!this.CanSetPreferredView || this.CurrentView is null)
        {
            return;
        }

        this._viewController.PreferredKey = this._viewController.SelectedKey;
        await this.PersistStoredViewsAsync("Set the default job view.");
    }

    private async Task PersistStoredViewsAsync(string successMessage)
    {
        try
        {
            await this.JsRuntime.InvokeVoidAsync(
              "shedduellerJobViews.save",
              this.ViewStorageKey(),
              new DashboardJobViewStoragePayload
              {
                  PreferredViewKey = this._viewController.PreferredKey,
                  Views = [.. this._viewController.StoredViews],
              });
            this.SetActionSuccess(successMessage);
        }
        catch (JSException)
        {
            this.SetActionWarning("The view is available for this session, but browser storage could not be updated.");
        }
    }

    private void OpenColumnEditor()
      => this._isColumnEditorOpen = true;

    private async Task CloseColumnEditorAsync()
    {
        this._isColumnEditorOpen = false;
        await this._columnsButton.FocusAsync();
    }

    private void SetBuiltInColumnVisible((ShedduellerDashboardJobColumnKind Kind, bool Visible) change)
    {
        if (DashboardJobColumnEditor.SetBuiltInVisibility(this._columnController.Columns, change.Kind, change.Visible))
        {
            this.MarkViewDirty();
        }
    }

    private void MoveColumn((int FromIndex, int ToIndex) move)
    {
        if (DashboardJobColumnEditor.Move(this._columnController.Columns, move.FromIndex, move.ToIndex))
        {
            this.MarkViewDirty();
        }
    }

    private void RemoveColumn(int index)
    {
        if (!DashboardJobColumnEditor.RemoveTag(this._columnController.Columns, index))
        {
            return;
        }

        this.MarkViewDirty();
    }

    private void AddTagColumn(ShedduellerDashboardJobColumn column)
    {
        if (!DashboardJobColumnEditor.TryAddTag(
            this._columnController.Columns,
            column.TagName ?? string.Empty,
            column.Heading,
            out var error))
        {
            this.SetActionError(error!);
            return;
        }

        this.ClearActionAlert();
        this.MarkViewDirty();
    }

    private void EditTagColumn((int Index, ShedduellerDashboardJobColumn Column) change)
    {
        if (!DashboardJobColumnEditor.TryUpdateTag(
            this._columnController.Columns,
            change.Index,
            change.Column.TagName ?? string.Empty,
            change.Column.Heading,
            out var error))
        {
            this.SetActionError(error!);
            return;
        }

        this.ClearActionAlert();
        this.MarkViewDirty();
    }

    [JSInvokable]
    public Task SetColumnWidthFromResizeAsync(
        int columnIndex,
        int width)
      => this.InvokeAsync(() =>
      {
          this.SetColumnWidth(columnIndex, width);
          this.StateHasChanged();
      });

    private Task HandleColumnWidthKeyAsync(int columnIndex, KeyboardEventArgs eventArgs)
    {
        if (this._columnController.ResizeByKeyboard(columnIndex, eventArgs.Key, eventArgs.ShiftKey))
        {
            this.MarkViewDirty();
        }

        return Task.CompletedTask;
    }

    private async Task AutoFitColumnAsync(int columnIndex)
    {
        var widths = await this.MeasureAutoFitWidthsAsync();
        if (columnIndex >= 0 && columnIndex < widths.Count)
        {
            this.SetColumnWidth(columnIndex, widths[columnIndex]);
        }
    }

    private async Task AutoFitAllColumnsAsync()
    {
        var widths = await this.MeasureAutoFitWidthsAsync();
        if (this._columnController.ApplyMeasuredWidths(widths))
        {
            this.MarkViewDirty();
        }
    }

    private async Task<IReadOnlyList<int>> MeasureAutoFitWidthsAsync()
    {
        var minimums = this._columnController.Columns
          .Select(column => DashboardJobColumnWidths.AutoFitMinimum(column.Kind))
          .ToArray();
        var maximums = this._columnController.Columns
          .Select(column => DashboardJobColumnWidths.AutoFitMaximum(column.Kind))
          .ToArray();
        return await this.JsRuntime.InvokeAsync<int[]>(
          "shedduellerJobViews.measureJobColumnWidths",
          minimums,
          maximums);
    }

    private void ResetColumnWidth(int columnIndex)
    {
        if (DashboardJobColumnWidths.Set(this._columnController.Columns, columnIndex, width: null))
        {
            this.MarkViewDirty();
        }
    }

    private void ResetColumnWidths()
    {
        if (DashboardJobColumnWidths.Reset(this._columnController.Columns))
        {
            this.MarkViewDirty();
        }
    }

    private void SetColumnWidth(
        int columnIndex,
        int width)
    {
        if (DashboardJobColumnWidths.Set(this._columnController.Columns, columnIndex, width))
        {
            this.MarkViewDirty();
        }
    }

    private void ResetColumns()
    {
        this.ReplaceColumns(DashboardJobViews.BuiltInColumns);
        this.MarkViewDirty();
    }

    private void ReplaceColumns(IReadOnlyList<ShedduellerDashboardJobColumn> columns)
      => this._columnController.Replace(columns);

    private string ViewStorageKey()
      => string.Concat(
        "sheddueller.dashboard.jobViews.v1:",
        new Uri(this.Navigation.BaseUri, UriKind.Absolute).AbsolutePath.TrimEnd('/'));

    private void MarkViewDirty()
      => this._viewController.IsDirty = true;

    private static string ColumnKey(ShedduellerDashboardJobColumn column)
      => column.Kind == ShedduellerDashboardJobColumnKind.Tag
        ? string.Concat("tag:", column.TagName)
        : column.Kind.ToString();

    private static int ColumnWidth(ShedduellerDashboardJobColumn column)
      => DashboardJobColumnWidths.Get(column);

    private static string ColumnWidthStyle(ShedduellerDashboardJobColumn column)
      => string.Create(CultureInfo.InvariantCulture, $"width: {ColumnWidth(column)}px");

    private static string? ColumnHeaderClass(ShedduellerDashboardJobColumn column)
      => column.Kind is ShedduellerDashboardJobColumnKind.Priority or ShedduellerDashboardJobColumnKind.Attempts
        ? "jobs-table__number"
        : null;

    private IReadOnlyList<JobTag> ResidualTags(JobInspectionSummary job)
      => DashboardJobViews.GetResidualTags(job.Tags, this._columnController.Columns);

    private static IReadOnlyList<string> TagValues(
        JobInspectionSummary job,
        ShedduellerDashboardJobColumn column)
      => [.. DashboardJobViews.GetTagValues(job.Tags, column.TagName!).Select(tag => tag.Value)];

    private static string TagValuesTitle(IReadOnlyList<string> values)
      => values.Count == 0 ? "No value" : string.Join(", ", values);

    private string TagValueFilterHref(
        ShedduellerDashboardJobColumn column,
        string value)
      => DashboardJobFilterQuery.WithTagHref(this._filters, string.Concat(column.TagName, ":", value));

    private async Task SetStateFilterAsync(
        JobState state,
        ChangeEventArgs eventArgs)
    {
        if (!this._filters.SetStateSelected(state, eventArgs.Value is bool selected && selected))
        {
            return;
        }

        this.MarkViewDirty();
        this.CancelPendingFilterLoad();
        this.NavigateToCurrentFilters();
        await this.LoadAsync();
    }

    private Task SetHandlerContainsAsync(string value)
      => this.SetTextFilterAsync(value, this._filters.SetHandlerContains);

    private Task SetTagContainsAsync(string value)
      => this.SetTextFilterAsync(value, this._filters.SetTagContains);

    private Task SetConcurrencyGroupContainsAsync(string value)
      => this.SetTextFilterAsync(value, this._filters.SetConcurrencyGroupContains);

    private async Task SetSortAsync(ChangeEventArgs eventArgs)
    {
        if (eventArgs.Value is not string value
            || !Enum.TryParse<JobInspectionSort>(value, ignoreCase: true, out var sort)
            || !Enum.IsDefined(sort)
            || !this._filters.SetSort(sort))
        {
            return;
        }

        this.CancelPendingFilterLoad();
        this.MarkViewDirty();
        this.NavigateToCurrentFilters();
        await this.LoadAsync();
    }

    private Task SetTextFilterAsync(
        string value,
        Func<string, bool> setFilter)
    {
        if (setFilter(value))
        {
            this.MarkViewDirty();
            this.ScheduleDebouncedLoad();
        }

        return Task.CompletedTask;
    }

    private async Task ClearAllAsync()
    {
        if (!this._filters.HasAppliedFilters)
        {
            return;
        }

        this.CancelPendingFilterLoad();
        this._filters.Clear();
        this.MarkViewDirty();
        this.NavigateToCurrentFilters();
        await this.LoadAsync();
    }

    private async Task ClearQueuedJobsAsync()
    {
        if (this._isClearQueuedRunning)
        {
            return;
        }

        var confirmation = await this.JsRuntime.InvokeAsync<string?>("prompt", ClearQueuedPrompt);
        if (!string.Equals(confirmation?.Trim(), ClearQueuedConfirmationWord, StringComparison.Ordinal))
        {
            return;
        }

        this._isClearQueuedRunning = true;
        this.ClearActionAlert();

        try
        {
            var canceledCount = await this.JobManager.CancelQueuedJobsAsync();
            if (canceledCount == 0)
            {
                this.SetActionWarning("No queued jobs were available to cancel.");
            }
            else
            {
                this.SetActionSuccess(CreateClearQueuedSuccessMessage(canceledCount));
            }

            await this.LiveRefresh.RefreshNowAsync();
        }
        catch (Exception exception)
        {
            this.SetActionError(string.Create(CultureInfo.InvariantCulture, $"Clear queued jobs failed: {exception.Message}"));
        }
        finally
        {
            this._isClearQueuedRunning = false;
        }
    }

    private async ValueTask OnJobEventPublishedAsync(
        JobEvent jobEvent,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;

        await this.ApplyPageStateAsync(() => this.LiveRefresh.MarkLiveActivity(jobEvent.OccurredAtUtc));
        this.LiveRefresh.QueueRefresh();
    }

    private Task LoadAsync()
      => this._query.LoadAsync(this.CreateQuery(DefaultPageSize, continuationToken: null));

    private void ClearQueryRefreshError()
      => this.LiveRefresh.ClearError();

    private void MarkQueryUpdated()
      => this.LiveRefresh.MarkUpdated();

    private Task LoadMoreAsync()
      => this._query.LoadMoreAsync();

    private Task RefreshCurrentQueryAsync(CancellationToken cancellationToken)
      => this._query.RefreshAsync(cancellationToken);

    private void ScheduleDebouncedLoad()
    {
        var previous = this._filterDebounceCts;
        previous?.Cancel();

        var next = new CancellationTokenSource();
        this._filterDebounceCts = next;
        _ = this.RunDebouncedLoadAsync(next);
    }

    private async Task RunDebouncedLoadAsync(CancellationTokenSource cancellationTokenSource)
    {
        try
        {
            await Task.Delay(FilterDebounce, cancellationTokenSource.Token);
            await this.InvokeAsync(async () =>
            {
                this.NavigateToCurrentFilters();
                await this.LoadAsync();
            });
        }
        catch (OperationCanceledException) when (cancellationTokenSource.IsCancellationRequested)
        {
        }
        finally
        {
            if (ReferenceEquals(this._filterDebounceCts, cancellationTokenSource))
            {
                this._filterDebounceCts = null;
            }

            cancellationTokenSource.Dispose();
        }
    }

    private void CancelPendingFilterLoad()
    {
        var cancellationTokenSource = this._filterDebounceCts;
        this._filterDebounceCts = null;
        cancellationTokenSource?.Cancel();
    }

    private void OnLocationChanged(
        object? sender,
        LocationChangedEventArgs eventArgs)
    {
        _ = sender;

        if (!this.IsJobsListLocation(eventArgs.Location))
        {
            return;
        }

        if (string.Equals(NormalizeAbsoluteUri(eventArgs.Location), this._pendingFilterNavigationUri, StringComparison.Ordinal))
        {
            this._pendingFilterNavigationUri = null;
            return;
        }

        this._pendingFilterNavigationUri = null;
        _ = this.ApplyFilterLocationAsync(eventArgs.Location);
    }

    private async Task ApplyFilterLocationAsync(string location)
    {
        var nextFilters = DashboardJobFilterQuery.ParseUri(location);

        await this.InvokeAsync(async () =>
        {
            if (!this._filters.ReplaceWith(nextFilters))
            {
                return;
            }

            this.MarkViewDirty();
            this.CancelPendingFilterLoad();
            await this.LoadAsync();
            this.StateHasChanged();
        });
    }

    private void NavigateToCurrentFilters()
    {
        var href = DashboardJobFilterQuery.ToHref(this._filters);
        if (string.Equals(this.Navigation.ToBaseRelativePath(this.Navigation.Uri), href, StringComparison.Ordinal))
        {
            return;
        }

        this._pendingFilterNavigationUri = this.Navigation.ToAbsoluteUri(href).AbsoluteUri;
        this.Navigation.NavigateTo(href, replace: true);
    }

    private bool IsJobsListLocation(string location)
    {
        var relativePath = this.Navigation.ToBaseRelativePath(location);
        var queryIndex = relativePath.IndexOf('?', StringComparison.Ordinal);
        var path = queryIndex < 0 ? relativePath : relativePath[..queryIndex];
        return string.Equals(path.TrimEnd('/'), "jobs", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeAbsoluteUri(string uri)
      => new Uri(uri, UriKind.Absolute).AbsoluteUri;

    private JobInspectionQuery CreateQuery(
        int pageSize,
        string? continuationToken)
      => this._filters.ToQuery(pageSize, continuationToken);

    private void SetActionSuccess(string message)
    {
        this._actionSeverity = JobsActionSeverity.Success;
        this._actionMessage = message;
    }

    private void SetActionWarning(string message)
    {
        this._actionSeverity = JobsActionSeverity.Warning;
        this._actionMessage = message;
    }

    private void SetActionError(string message)
    {
        this._actionSeverity = JobsActionSeverity.Error;
        this._actionMessage = message;
    }

    private void ClearActionAlert()
      => this._actionMessage = null;

    internal static string CreateClearQueuedSuccessMessage(int canceledCount)
      => string.Create(CultureInfo.InvariantCulture, $"Canceled {DashboardFormat.Count(canceledCount)} queued job(s).");

    private static string StatusFilterClass(JobState state)
      => string.Concat("jobs-status-option jobs-status-option--", DashboardFormat.StateCssModifier(state));

    private static string QueuePositionText(JobInspectionSummary job)
      => job.QueuePosition switch
      {
          { Kind: JobQueuePositionKind.Claimable, Position: { } position } => string.Create(CultureInfo.InvariantCulture, $"#{position}"),
          { Kind: JobQueuePositionKind.Claimable } => "Ready",
          { Kind: JobQueuePositionKind.Claimed } => "Running",
          { Kind: JobQueuePositionKind.WaitingForDependencies } => "Dependencies",
          { Kind: JobQueuePositionKind.BlockedByConcurrency } => "Blocked",
          { Kind: JobQueuePositionKind.RetryWaiting } => "Retry",
          { Kind: JobQueuePositionKind.Delayed } => "Delayed",
          _ => "-",
      };

    private static string QueuePositionTitle(JobInspectionSummary job)
      => job.QueuePosition?.Reason ?? QueuePositionText(job);

    private static string QueuePositionClass(JobInspectionSummary job)
      => string.Concat("jobs-queue jobs-queue--", job.QueuePosition?.Kind switch
      {
          JobQueuePositionKind.Claimable => "claimable",
          JobQueuePositionKind.Claimed => "claimed",
          JobQueuePositionKind.WaitingForDependencies => "waiting",
          JobQueuePositionKind.BlockedByConcurrency => "blocked",
          JobQueuePositionKind.RetryWaiting => "waiting",
          JobQueuePositionKind.Delayed => "delayed",
          _ => "empty",
      });

    private static string GetDispositionClass(JobInspectionSummary job)
      => job.State == JobState.Failed ? "jobs-disposition--error" : "jobs-disposition--muted";

    private string StateFilterHref(JobState state)
      => DashboardJobFilterQuery.WithStateHref(this._filters, state);

    private static string StateFilterTitle(JobState state)
      => string.Concat("Filter jobs by state ", state);

    private string HandlerFilterHref(JobInspectionSummary job)
      => DashboardJobFilterQuery.WithHandlerHref(this._filters, DashboardFormat.ShortHandler(job));

    private static string HandlerFilterTitle(JobInspectionSummary job)
      => string.Concat("Filter jobs by handler ", DashboardFormat.ShortHandler(job));

    private string TagFilterHref(string tag)
      => DashboardJobFilterQuery.WithTagHref(this._filters, tag);

    private string GroupFilterHref(string group)
      => DashboardJobFilterQuery.WithGroupHref(this._filters, group);

    private enum JobsActionSeverity
    {
        Success,
        Warning,
        Error,
    }
}
