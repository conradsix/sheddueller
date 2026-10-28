namespace Sheddueller.Dashboard.Components.Pages;

using System.Globalization;

using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

using Sheddueller.Dashboard.Components.Ui;
using Sheddueller.Dashboard.Internal;
using Sheddueller.Inspection.Jobs;
using Sheddueller.Storage;

/// <content>
/// The Logs tab: filtering by level, attempt, and text, following and pausing live lines, and downloading.
/// </content>
public partial class JobDetail
{
    private DashboardJobLogFilter _logFilter = new();
    private bool _logsPaused;
    private bool _followLogs = true;
    private bool _scrollToLatestLog;
    private ElementReference _logEnd;
    private bool _showLogFields = true;

    private IReadOnlyList<JobEvent> AllLogLines
      => [.. this._eventLog.Events.Where(DashboardJobLog.IsLogLine)];

    private IReadOnlyList<int> LogAttempts
      => DashboardJobLog.Attempts(this._eventLog.Events);

    // "Follow new lines" keeps the newest log line in view as live events arrive; the page is the scroll region.
    private void SetFollowLogs(bool follow)
    {
        this._followLogs = follow;
        this._scrollToLatestLog = follow;
    }

    private async Task ScrollToLatestLogAsync()
    {
        if (!this._scrollToLatestLog)
        {
            return;
        }

        this._scrollToLatestLog = false;
        if (this.ActiveTab != DashboardJobDetailTab.Logs)
        {
            return;
        }

        await this.JsRuntime.TryInvokeVoidAsync("sheddueller.scrollIntoView", this._logEnd);
    }

    // Paused means no live appends and no list replacement. Resuming catches up on anything missed while paused.
    private Task ToggleLogsPaused()
    {
        this._logsPaused = !this._logsPaused;
        return this._logsPaused ? Task.CompletedTask : this.LiveRefresh.RefreshNowAsync();
    }

    private void SetLogLevel(DashboardLogLevelFilter level)
      => this._logFilter = this._logFilter with { Level = level };

    private void SetLogAttempt(int? attempt)
      => this._logFilter = this._logFilter with { Attempt = attempt };

    private void SetLogSearch(string search)
      => this._logFilter = this._logFilter with { Search = search };

    private void ToggleProgressLines()
      => this._logFilter = this._logFilter with { IncludeProgress = !this._logFilter.IncludeProgress };

    private void ToggleLogFields()
      => this._showLogFields = !this._showLogFields;

    private void ClearLogFilters()
      => this._logFilter = new DashboardJobLogFilter(IncludeProgress: this._logFilter.IncludeProgress);

    // From the failure callout: the failed attempt's error lines (or all its lines when it logged no errors).
    private void ShowAttemptLogs(int attempt, bool errorsOnly)
    {
        this._logFilter = this._logFilter with
        {
            Level = errorsOnly ? DashboardLogLevelFilter.Errors : DashboardLogLevelFilter.All,
            Attempt = this.LogAttempts.Count > 1 ? attempt : null,
            Search = null,
        };

        if (this.ActiveTab != DashboardJobDetailTab.Logs)
        {
            this.Navigation.NavigateTo(this.TabHref(DashboardJobDetailTab.Logs));
        }
    }

    private async Task DownloadLogAsync(IReadOnlyList<JobEvent> lines)
    {
        try
        {
            await this.JsRuntime.DownloadAsync(DashboardJobLog.FileName(this.JobId), DashboardJobLog.ToText(lines));
        }
        catch (JSException exception)
        {
            this.ShowToast("Couldn't download the log", exception.Message, AlertTone.Destructive);
        }
    }

    private IReadOnlyList<TabItem<DashboardLogLevelFilter>> LevelTabItems(IReadOnlyList<JobEvent> logLines)
      =>
      [
          new(DashboardLogLevelFilter.All, "All", DashboardJobLog.Count(logLines, this._logFilter with { Level = DashboardLogLevelFilter.All })),
          new(DashboardLogLevelFilter.Warnings, "Warnings", DashboardJobLog.Count(logLines, this._logFilter with { Level = DashboardLogLevelFilter.Warnings })),
          new(DashboardLogLevelFilter.Errors, "Errors", DashboardJobLog.Count(logLines, this._logFilter with { Level = DashboardLogLevelFilter.Errors }), Attention: true),
      ];

    // A queued or running job can still write lines, so following and pausing apply.
    private static bool IsLive(JobInspectionSummary job)
      => job.State is JobState.Queued or JobState.Claimed;

    private static string LogFooterText(
        int visibleCount,
        int totalCount)
      => visibleCount == totalCount
        ? string.Create(CultureInfo.InvariantCulture, $"{DashboardFormat.Count(totalCount)} {(totalCount == 1 ? "line" : "lines")}")
        : string.Create(CultureInfo.InvariantCulture, $"Showing {DashboardFormat.Count(visibleCount)} of {DashboardFormat.Count(totalCount)} lines");
}
