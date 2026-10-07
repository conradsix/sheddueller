namespace Sheddueller.Dashboard.Components.Pages;

using System.Diagnostics.CodeAnalysis;
using System.Globalization;

using Microsoft.AspNetCore.Components;

using Sheddueller;
using Sheddueller.Dashboard.Internal;
using Sheddueller.Inspection.Schedules;

[SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Dashboard operation failures are displayed without terminating the rendering circuit.")]
[SuppressMessage("Naming", "CA1724:Type names should not match namespaces", Justification = "Preserve the existing public Razor component name.")]
public partial class Schedules
{
    private static readonly TimeSpan PageRefreshInterval = TimeSpan.FromSeconds(5);
    private const int DefaultPageSize = 25;
    internal const string ScheduleNotFoundActionFailureMessage = "Schedule action failed: Schedule was not found.";

    private readonly DashboardScheduleFilters _filters = new();
    private readonly List<ScheduleInspectionSummary> _schedules = [];
    private ScheduleInspectionPage? _page;
    private string? _loadError;
    private string? _actionMessage;
    private Guid? _triggeredJobId;
    private ScheduleActionSeverity _actionSeverity = ScheduleActionSeverity.Success;
    private bool _isLoading;
    private bool _isLoadingMore;
    private bool _isActionRunning;

    private string ActionAlertClass
      => this._actionSeverity switch
      {
          ScheduleActionSeverity.Success => "schedules-inline-alert schedules-inline-alert--success",
          ScheduleActionSeverity.Warning => "schedules-inline-alert schedules-inline-alert--warning",
          _ => "schedules-inline-alert schedules-inline-alert--error",
      };

    private string ActionAlertIcon
      => this._actionSeverity switch
      {
          ScheduleActionSeverity.Success => "check_circle",
          ScheduleActionSeverity.Warning => "info",
          _ => "warning",
      };

    private bool IsActionDisabled
      => this._isActionRunning || this._isLoading || this._isLoadingMore;

    private string EmptyText
      => this.HasAppliedFilters
        ? "No schedules matched the current filters."
        : "No recurring schedules are registered.";

    private bool HasAppliedFilters
      => this._filters.HasAppliedFilters;

    private string ResultRangeText
    {
        get
        {
            var loadedCount = this._schedules.Count;
            var totalCount = this._page?.TotalCount ?? loadedCount;
            var moreText = this._page?.ContinuationToken is null ? string.Empty : " with more available";
            var filterText = this.HasAppliedFilters ? " matching filters" : string.Empty;

            return loadedCount == 0
              ? string.Create(CultureInfo.InvariantCulture, $"Showing 0 of {DashboardFormat.Count(totalCount)} schedules{filterText}")
              : string.Create(CultureInfo.InvariantCulture, $"Showing 1-{DashboardFormat.Count(loadedCount)} of {DashboardFormat.Count(totalCount)} schedules{filterText}{moreText}");
        }
    }

    protected override async Task OnInitializedAsync()
    {
        this.InitializeLiveRefresh(
          this.RefreshCurrentQueryAsync,
          showRefreshing: false,
          lastUpdatedUtc: nowUtc => this.LiveRefresh.LastUpdatedUtc ?? (this._page is null ? null : nowUtc));

        await this.LoadAsync();
        this.StartPeriodicRefresh(PageRefreshInterval);
    }

    private async Task SetScheduleKeyFilterAsync(string value)
    {
        if (string.Equals(this._filters.ScheduleKey, value, StringComparison.Ordinal))
        {
            return;
        }

        this._filters.ScheduleKey = value;
        this.ClearActionAlert();

        await this.LoadAsync();
    }

    private async Task SetPauseStateFilterAsync(ChangeEventArgs eventArgs)
    {
        var value = eventArgs.Value?.ToString() ?? string.Empty;
        if (string.Equals(this._filters.PauseState, value, StringComparison.Ordinal))
        {
            return;
        }

        this._filters.PauseState = value;
        this.ClearActionAlert();

        await this.LoadAsync();
    }

    private async Task LoadAsync()
    {
        this._loadError = null;
        this.LiveRefresh.ClearError();
        this._isLoading = true;

        try
        {
            var page = await this.Reader.SearchSchedulesAsync(this.CreateQuery(DefaultPageSize, continuationToken: null));
            this.ReplaceSchedules(page);
            this.LiveRefresh.MarkUpdated();
        }
        catch (Exception exception)
        {
            this._loadError = exception.Message;
        }
        finally
        {
            this._isLoading = false;
            this.NotifyShellRefreshChanged();
        }
    }

    private async Task LoadMoreAsync()
    {
        if (this._page?.ContinuationToken is null || this._isLoadingMore)
        {
            return;
        }

        this._loadError = null;
        this._isLoadingMore = true;

        try
        {
            var page = await this.Reader.SearchSchedulesAsync(this.CreateQuery(DefaultPageSize, this._page.ContinuationToken));
            this._schedules.AddRange(page.Schedules);
            this._page = page;
            this.LiveRefresh.MarkUpdated();
        }
        catch (Exception exception)
        {
            this._loadError = exception.Message;
        }
        finally
        {
            this._isLoadingMore = false;
            this.NotifyShellRefreshChanged();
        }
    }

    private async Task PauseOrResumeAsync(ScheduleInspectionSummary schedule)
    {
        if (this._isActionRunning)
        {
            return;
        }

        this._isActionRunning = true;
        this.ClearActionAlert();

        try
        {
            var successVerb = schedule.IsPaused ? "resumed" : "paused";
            var succeeded = schedule.IsPaused
              ? await this.ScheduleManager.ResumeAsync(schedule.ScheduleKey)
              : await this.ScheduleManager.PauseAsync(schedule.ScheduleKey);

            if (succeeded)
            {
                this.SetActionSuccess(string.Create(CultureInfo.InvariantCulture, $"Schedule {schedule.ScheduleKey} {successVerb}."));
                await this.LiveRefresh.RefreshNowAsync();
                return;
            }

            this.SetActionFailure("Schedule action failed: Schedule was not found.");
        }
        catch (Exception exception)
        {
            this.SetActionFailure(string.Create(CultureInfo.InvariantCulture, $"Schedule action failed: {exception.Message}"));
        }
        finally
        {
            this._isActionRunning = false;
        }
    }

    private async Task TriggerNowAsync(ScheduleInspectionSummary schedule)
    {
        if (this._isActionRunning)
        {
            return;
        }

        this._isActionRunning = true;
        this.ClearActionAlert();

        try
        {
            var result = await this.ScheduleManager.TriggerAsync(schedule.ScheduleKey);
            switch (result.Status)
            {
                case RecurringScheduleTriggerStatus.Enqueued:
                    this.SetActionSuccess(CreateTriggerSuccessMessage(schedule.ScheduleKey), result.JobId);
                    await this.LiveRefresh.RefreshNowAsync();
                    break;
                case RecurringScheduleTriggerStatus.SkippedActiveOccurrence:
                    this.SetActionWarning(CreateTriggerSkippedMessage(schedule.ScheduleKey));
                    break;
                case RecurringScheduleTriggerStatus.NotFound:
                    this.SetActionFailure(ScheduleNotFoundActionFailureMessage);
                    break;
                default:
                    this.SetActionFailure(string.Create(
                      CultureInfo.InvariantCulture,
                      $"Schedule action failed: Trigger returned unsupported status {result.Status}."));
                    break;
            }
        }
        catch (Exception exception)
        {
            this.SetActionFailure(string.Create(CultureInfo.InvariantCulture, $"Schedule action failed: {exception.Message}"));
        }
        finally
        {
            this._isActionRunning = false;
        }
    }

    private async Task RefreshCurrentQueryAsync(CancellationToken cancellationToken)
    {
        var visiblePageSize = Math.Max(DefaultPageSize, this._schedules.Count);
        var page = await this.Reader.SearchSchedulesAsync(this.CreateQuery(visiblePageSize, continuationToken: null),
cancellationToken);

        await this.ApplyPageStateAsync(() => this.ReplaceSchedules(page));
    }

    private ScheduleInspectionQuery CreateQuery(
        int pageSize,
        string? continuationToken)
      => this._filters.ToQuery(pageSize, continuationToken);

    private void ReplaceSchedules(ScheduleInspectionPage page)
    {
        this._schedules.Clear();
        this._schedules.AddRange(page.Schedules);
        this._page = page;
        this._loadError = null;
    }

    private void SetActionSuccess(string message, Guid? triggeredJobId = null)
    {
        this._actionSeverity = ScheduleActionSeverity.Success;
        this._actionMessage = message;
        this._triggeredJobId = triggeredJobId;
    }

    private void SetActionWarning(string message)
    {
        this._actionSeverity = ScheduleActionSeverity.Warning;
        this._actionMessage = message;
        this._triggeredJobId = null;
    }

    private void SetActionFailure(string message)
    {
        this._actionSeverity = ScheduleActionSeverity.Error;
        this._actionMessage = message;
        this._triggeredJobId = null;
    }

    private void ClearActionAlert()
    {
        this._actionMessage = null;
        this._triggeredJobId = null;
    }

    private static string FullHandler(ScheduleInspectionSummary schedule)
      => string.Concat(schedule.ServiceType, ".", schedule.MethodName);

    public static string ShortHandler(ScheduleInspectionSummary job)
        => string.Concat(ShortTypeName(job.ServiceType), ".", job.MethodName);

    private static string ShortTypeName(string typeName)
    {
        var typeDelimiterIndex = typeName.IndexOf(',', StringComparison.Ordinal);
        if (typeDelimiterIndex >= 0)
        {
            typeName = typeName[..typeDelimiterIndex];
        }

        var separatorIndex = typeName.LastIndexOf('.');
        return separatorIndex < 0 || separatorIndex == typeName.Length - 1
          ? typeName
          : typeName[(separatorIndex + 1)..];
    }

    private static string ScheduleRowClass(ScheduleInspectionSummary schedule)
      => schedule.IsPaused ? "schedules-row--paused" : string.Empty;

    private static string ScheduleStateText(ScheduleInspectionSummary schedule)
      => schedule.IsPaused ? "Paused" : "Active";

    private static string ScheduleStateClass(ScheduleInspectionSummary schedule)
      => schedule.IsPaused
        ? "schedules-state schedules-state--paused"
        : "schedules-state schedules-state--active";

    private static string NextFireEmptyText(ScheduleInspectionSummary schedule)
      => schedule.IsPaused ? "Paused" : "Not scheduled";

    private static string OverlapClass(ScheduleInspectionSummary schedule)
      => schedule.OverlapMode == RecurringOverlapMode.Allow
        ? "schedules-overlap schedules-overlap--allow"
        : "schedules-overlap";

    private static string PauseResumeIcon(ScheduleInspectionSummary schedule)
      => schedule.IsPaused ? "play_circle" : "pause";

    private static string PauseResumeTitle(ScheduleInspectionSummary schedule)
      => schedule.IsPaused ? "Resume Schedule" : "Pause Schedule";

    private static string PauseResumeLabel(ScheduleInspectionSummary schedule)
      => string.Create(CultureInfo.InvariantCulture, $"{PauseResumeTitle(schedule)} {schedule.ScheduleKey}");

    private static string TriggerLabel(ScheduleInspectionSummary schedule)
      => string.Create(CultureInfo.InvariantCulture, $"Trigger Now {schedule.ScheduleKey}");

    internal static string CreateTriggerSuccessMessage(string scheduleKey)
      => string.Create(CultureInfo.InvariantCulture, $"Schedule {scheduleKey} triggered as job");

    internal static string CreateTriggerSkippedMessage(string scheduleKey)
      => string.Create(CultureInfo.InvariantCulture, $"Schedule {scheduleKey} already has an active occurrence.");

    private static string JobHref(Guid jobId)
      => string.Create(CultureInfo.InvariantCulture, $"jobs/{jobId:D}");

    private enum ScheduleActionSeverity
    {
        Success,
        Warning,
        Error,
    }
}
