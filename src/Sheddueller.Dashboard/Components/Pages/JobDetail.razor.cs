namespace Sheddueller.Dashboard.Components.Pages;

using System.Diagnostics.CodeAnalysis;
using System.Globalization;

using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

using Sheddueller;
using Sheddueller.Dashboard.Internal;
using Sheddueller.Inspection.Jobs;
using Sheddueller.Storage;

[SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Dashboard operation failures are displayed without terminating the rendering circuit.")]
public partial class JobDetail
{
    private const int EventPageSize = 500;

    private readonly List<JobEvent> _events = [];
    private ElementReference _invocationCodeElement;
    private JobInspectionDetail? _detail;
    private Guid? _loadedJobId;
    private string? _error;
    private string? _actionMessage;
    private string? _highlightedInvocationCall;
    private JobDetailActionAlertKind _actionAlertKind;
    private bool _isLoading;
    private bool _isCancelRunning;
    private bool _logsPaused;
    private bool _showProgressLogs;

    [Parameter]
    public Guid JobId { get; set; }

    private string ActionAlertClass
      => this._actionAlertKind switch
      {
          JobDetailActionAlertKind.Success => "job-detail-inline-alert job-detail-inline-alert--success",
          JobDetailActionAlertKind.Warning => "job-detail-inline-alert job-detail-inline-alert--warning",
          _ => "job-detail-inline-alert job-detail-inline-alert--error",
      };

    private string ActionAlertIcon
      => this._actionAlertKind switch
      {
          JobDetailActionAlertKind.Success => "check_circle",
          JobDetailActionAlertKind.Warning => "warning",
          _ => "error",
      };

    private bool IsCancelButtonDisabled
      => this._isCancelRunning || !this.CanCancelCurrentJob();

    private string CancelButtonClass
      => this.CanCancelCurrentJob()
        ? "job-detail-button job-detail-button--danger job-detail-cancel-action"
        : "job-detail-button job-detail-button--secondary job-detail-cancel-action";

    private string CancelButtonIcon
      => this._isCancelRunning
        ? "progress_activity"
        : this._detail?.Summary is { State: JobState.Claimed, CancellationRequestedAtUtc: not null }
          ? "pending"
          : "cancel";

    private string CancelButtonText
      => this._isCancelRunning
        ? "Canceling"
        : this._detail?.Summary is { State: JobState.Claimed, CancellationRequestedAtUtc: not null }
          ? "Cancellation Requested"
          : "Cancel Job";

    private string CancelButtonTitle
      => this.GetCancelButtonTitle();

    private string CancelButtonAriaLabel
      => string.Create(CultureInfo.InvariantCulture, $"{this.CancelButtonText}: {this.CancelButtonTitle}");

    protected override void OnInitialized()
    {
        this.InitializeLiveRefresh(
          this.RefreshDetailAsync,
          showRefreshing: true,
          lastUpdatedUtc: nowUtc => this.LiveRefresh.LastUpdatedUtc ?? (this._detail is null ? null : nowUtc));
        this.LiveUpdates.JobEventPublished += this.OnJobEventPublishedAsync;
    }

    protected override async Task OnParametersSetAsync()
    {
        if (this._loadedJobId == this.JobId)
        {
            return;
        }

        this._loadedJobId = this.JobId;
        this._detail = null;
        this._events.Clear();
        this._highlightedInvocationCall = null;
        this._error = null;
        this.ClearActionAlert();
        this._isCancelRunning = false;
        this.LiveRefresh.ClearError();
        this._logsPaused = false;
        this._showProgressLogs = false;

        await this.LoadAsync();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        _ = firstRender;

        if (this._detail?.Invocation is not { } invocation ||
            string.Equals(this._highlightedInvocationCall, invocation.ReconstructedCall, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            await this.JsRuntime.InvokeVoidAsync(
                "ShedduellerDashboard.highlightCode",
                this._invocationCodeElement);
            this._highlightedInvocationCall = invocation.ReconstructedCall;
        }
        catch (JSDisconnectedException)
        {
        }
        catch (JSException)
        {
        }
    }

    private protected override ValueTask DisposePageAsync()
    {
        this.LiveUpdates.JobEventPublished -= this.OnJobEventPublishedAsync;
        return ValueTask.CompletedTask;
    }

    private void ToggleLogsPaused()
      => this._logsPaused = !this._logsPaused;

    private void ToggleProgressLogs(ChangeEventArgs eventArgs)
      => this._showProgressLogs = eventArgs.Value is true;

    private async Task CancelJobAsync()
    {
        if (this._isCancelRunning || !this.CanCancelCurrentJob())
        {
            return;
        }

        this._isCancelRunning = true;
        this.ClearActionAlert();

        try
        {
            var result = await this.JobManager.CancelAsync(this.JobId);

            this.SetCancelActionResult(result);
            await this.LiveRefresh.RefreshNowAsync();
        }
        catch (Exception exception)
        {
            this.SetActionError(string.Create(CultureInfo.InvariantCulture, $"Cancel failed: {exception.Message}"));
        }
        finally
        {
            this._isCancelRunning = false;
        }
    }

    private async ValueTask OnJobEventPublishedAsync(
        JobEvent jobEvent,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;

        if (jobEvent.JobId != this.JobId || this._logsPaused)
        {
            return;
        }

        await this.ApplyPageStateAsync(() =>
        {
            this.AppendOrReplaceEvent(jobEvent);
            this.ApplyLiveEventToDetail(jobEvent);
            this.LiveRefresh.MarkLiveActivity(jobEvent.OccurredAtUtc);
        });
        this.LiveRefresh.QueueRefresh();
    }

    private async Task LoadAsync()
    {
        await this.ApplyPageStateAsync(() =>
        {
            if (this._detail is null)
            {
                this._isLoading = true;
            }

            this._error = null;
            this.LiveRefresh.ClearError();
        });

        try
        {
            var (detail, events) = await this.ReadDetailAsync(CancellationToken.None);

            await this.ApplyPageStateAsync(() =>
            {
                this.ApplyLoadedDetail(detail, events);
                this._isLoading = false;
                this.LiveRefresh.MarkUpdated();
            });
        }
        catch (Exception exception)
        {
            await this.ApplyPageStateAsync(() =>
            {
                this._error = exception.Message;
                this._isLoading = false;
            });
        }
    }

    private async Task RefreshDetailAsync(CancellationToken cancellationToken)
    {
        var (detail, events) = await this.ReadDetailAsync(cancellationToken);

        await this.ApplyPageStateAsync(() => this.ApplyLoadedDetail(detail, events));
    }

    private async Task<(JobInspectionDetail? Detail, IReadOnlyList<JobEvent> Events)> ReadDetailAsync(CancellationToken cancellationToken)
    {
        var detail = await this.Reader.GetJobAsync(this.JobId, cancellationToken);
        var events = new List<JobEvent>();
        if (detail is not null)
        {
            await foreach (var jobEvent in this.Reader.ReadEventsAsync(
                this.JobId,
                new JobEventReadOptions(Limit: EventPageSize),
                cancellationToken)
              )
            {
                events.Add(jobEvent);
            }
        }

        return (detail, events);
    }

    private void ApplyLoadedDetail(
        JobInspectionDetail? detail,
        IReadOnlyList<JobEvent> events)
    {
        this._detail = detail;
        this._events.Clear();
        this._events.AddRange(events.OrderBy(static jobEvent => jobEvent.EventSequence));
    }

    private void AppendOrReplaceEvent(JobEvent jobEvent)
    {
        var index = this._events.FindIndex(candidate =>
          candidate.EventId == jobEvent.EventId || candidate.EventSequence == jobEvent.EventSequence);

        if (index >= 0)
        {
            this._events[index] = jobEvent;
        }
        else
        {
            this._events.Add(jobEvent);
        }

        this._events.Sort(static (left, right) => left.EventSequence.CompareTo(right.EventSequence));
    }

    private void ApplyLiveEventToDetail(JobEvent jobEvent)
    {
        if (this._detail is null)
        {
            return;
        }

        if (jobEvent.Kind == JobEventKind.Progress)
        {
            this._detail = this._detail with
            {
                Summary = this._detail.Summary with
                {
                    LatestProgress = new JobProgressSnapshot(
                      jobEvent.ProgressPercent,
                      jobEvent.Message,
                      jobEvent.OccurredAtUtc),
                },
            };
            return;
        }

        if (jobEvent.Kind == JobEventKind.CancelRequested)
        {
            this._detail = this._detail with
            {
                Summary = this._detail.Summary with
                {
                    CancellationRequestedAtUtc = jobEvent.OccurredAtUtc,
                },
            };
        }
    }

    private bool CanCancelCurrentJob()
      => this._detail?.Summary is { } job
        && (job.State == JobState.Queued
          || (job.State == JobState.Claimed && job.CancellationRequestedAtUtc is null));

    private string GetCancelButtonTitle()
    {
        if (this._isCancelRunning)
        {
            return "Cancel request is in progress.";
        }

        if (this._isLoading && this._detail is null)
        {
            return "Job detail is loading before cancellation can be evaluated.";
        }

        if (this._error is not null)
        {
            return "Job detail failed to load, so cancellation state cannot be evaluated.";
        }

        if (this._detail is null)
        {
            return "No job was found with this ID.";
        }

        var job = this._detail.Summary;
        return job.State switch
        {
            JobState.Queued => "Cancel this queued job before it is claimed.",
            JobState.Claimed when job.CancellationRequestedAtUtc is { } requestedAtUtc
              => string.Create(
                CultureInfo.InvariantCulture,
                $"Cancellation was already requested at {DashboardFormat.Utc(requestedAtUtc)}; the worker will observe it on heartbeat."),
            JobState.Claimed => "Request cooperative cancellation for this running job.",
            JobState.Completed => FormatTerminalCancelReason("completed", job.CompletedAtUtc),
            JobState.Failed => FormatTerminalCancelReason("failed", job.FailedAtUtc),
            JobState.Canceled => job.CanceledAtUtc is null
              ? "This job is already canceled."
              : string.Create(CultureInfo.InvariantCulture, $"This job was canceled at {DashboardFormat.Utc(job.CanceledAtUtc)} and cannot be canceled again."),
            _ => "This job cannot be canceled in its current state.",
        };
    }

    private void SetCancelActionResult(JobCancellationResult result)
    {
        var jobId = DashboardFormat.JobId(this.JobId);
        switch (result)
        {
            case JobCancellationResult.Canceled:
                this.SetActionSuccess(string.Create(CultureInfo.InvariantCulture, $"Job {jobId} canceled."));
                break;
            case JobCancellationResult.CancellationRequested:
                this.SetActionSuccess(string.Create(CultureInfo.InvariantCulture, $"Cancellation requested for job {jobId}."));
                break;
            case JobCancellationResult.AlreadyFinished:
                this.SetActionWarning(string.Create(CultureInfo.InvariantCulture, $"Job {jobId} already finished."));
                break;
            case JobCancellationResult.NotFound:
                this.SetActionWarning(string.Create(CultureInfo.InvariantCulture, $"Job {jobId} was not found."));
                break;
            default:
                this.SetActionWarning(string.Create(CultureInfo.InvariantCulture, $"Cancel returned {result} for job {jobId}."));
                break;
        }
    }

    private void SetActionSuccess(string message)
    {
        this._actionAlertKind = JobDetailActionAlertKind.Success;
        this._actionMessage = message;
    }

    private void SetActionWarning(string message)
    {
        this._actionAlertKind = JobDetailActionAlertKind.Warning;
        this._actionMessage = message;
    }

    private void SetActionError(string message)
    {
        this._actionAlertKind = JobDetailActionAlertKind.Error;
        this._actionMessage = message;
    }

    private void ClearActionAlert()
      => this._actionMessage = null;

    private IReadOnlyList<JobEvent> GetLifecycleEvents()
      => [.. this._events.Where(DashboardFormat.IsLifecycleEvent)];

    private IReadOnlyList<JobEvent> GetLogEvents()
      => [.. this._events
        .Where(DashboardFormat.IsLogEvent)
        .Where(jobEvent => this._showProgressLogs || jobEvent.Kind != JobEventKind.Progress)];

    private string GetLogEmptyMessage()
      => this._showProgressLogs
        ? "No log or progress events have been recorded."
        : "No log events have been recorded. Progress events are hidden.";

    private static DateTimeOffset GetStartedAt(JobInspectionDetail detail)
      => detail.ClaimedAtUtc ?? detail.Summary.EnqueuedAtUtc;

    private static string FormatTerminalCancelReason(
        string terminalState,
        DateTimeOffset? timestamp)
      => timestamp is null
        ? string.Create(CultureInfo.InvariantCulture, $"This job {terminalState} and cannot be canceled.")
        : string.Create(CultureInfo.InvariantCulture, $"This job {terminalState} at {DashboardFormat.Utc(timestamp)} and cannot be canceled.");

    private static string GetDispositionClass(JobInspectionSummary job)
      => job.State == JobState.Failed ? "job-detail-disposition--error" : string.Empty;

    private static string GetLogRowClass(
        JobEvent jobEvent,
        IReadOnlyList<JobEvent> logEvents)
      => logEvents.Count > 0
        && logEvents[^1].EventSequence == jobEvent.EventSequence
        && jobEvent.Kind == JobEventKind.Progress
          ? "job-detail-log-row--current"
          : string.Empty;

    private static string StateFilterHref(JobState state)
      => DashboardJobFilterQuery.StateHref(state);

    private static string StateFilterTitle(JobState state)
      => string.Concat("Filter jobs by state ", state);

    private static string HandlerFilterHref(JobInspectionSummary job)
      => DashboardJobFilterQuery.HandlerHref(DashboardFormat.ShortHandler(job));

    private static string HandlerFilterTitle(JobInspectionSummary job)
      => string.Concat("Filter jobs by handler ", DashboardFormat.ShortHandler(job));

    private static string TagFilterHref(string tag)
      => DashboardJobFilterQuery.TagHref(tag);

    private static string GroupFilterHref(string group)
      => DashboardJobFilterQuery.GroupHref(group);

    private enum JobDetailActionAlertKind
    {
        Success,
        Warning,
        Error,
    }
}
