namespace Sheddueller.Dashboard.Components.Pages;

using System.Diagnostics.CodeAnalysis;
using System.Globalization;

using Microsoft.AspNetCore.Components;

using Sheddueller.Dashboard.Components.Ui;
using Sheddueller.Dashboard.Internal;
using Sheddueller.Inspection.Jobs;
using Sheddueller.Storage;

/// <content>
/// Loading and refreshing the job, applying its live events, the header and state callouts, and cancelling. Each
/// tab's own state is in JobDetail.Logs.cs, JobDetail.Events.cs, JobDetail.Invocation.cs, and JobDetail.Related.cs.
/// </content>
public partial class JobDetail
{
    private const int MenuGroupLimit = 3;

    private JobInspectionDetail? _detail;
    private Guid? _loadedJobId;
    private string? _error;
    private bool _isLoading;
    private bool _isCancelRunning;
    private bool _cancelDialogOpen;

    [Parameter]
    public Guid JobId { get; set; }

    /// <summary>
    /// The open section; the logs when absent.
    /// </summary>
    [SupplyParameterFromQuery(Name = DashboardJobDetailFormat.TabParameter)]
    public string? Tab { get; set; }

    [Inject]
    private DashboardNavigationState NavigationState { get; set; } = null!;

    private string JobsListHref
      => this.NavigationState.JobsListHref;

    private string? JobsListContext
      => DashboardJobDetailFormat.JobsListContext(this.JobsListHref);

    private DashboardJobDetailTab ActiveTab
      => DashboardJobDetailFormat.ParseTab(this.Tab);

    private string CancelDialogDescription
      => this._detail?.Summary.State == JobState.Claimed
        ? "The running job is asked to stop and ends at its next cancellation check. You can't undo this."
        : "The job ends as canceled straight away and won't run. You can't undo this.";

    protected override void OnInitialized()
    {
        this.InitializeLiveRefresh(
          this.RefreshDetailAsync,
          showRefreshing: true,
          lastUpdatedUtc: nowUtc => this.LiveRefresh.LastUpdatedUtc ?? (this._detail is null ? null : nowUtc),
          queuedRefreshAsync: this.RefreshSummaryAsync);
        this.SubscribeToJobEvents(this.LiveUpdates, this.OnJobEventPublishedAsync);
    }

    protected override async Task OnParametersSetAsync()
    {
        if (this._loadedJobId == this.JobId)
        {
            return;
        }

        this._loadedJobId = this.JobId;
        this._detail = null;
        this._eventLog.Clear();
        this._relatedJobs.Clear();
        this._relatedError = null;
        this._relatedStale = false;
        this._isLoadingRelated = false;
        this._isLoadingMoreEvents = false;
        this._error = null;
        this._isCancelRunning = false;
        this._cancelDialogOpen = false;
        this.LiveRefresh.ClearError();
        this._logsPaused = false;
        this._logFilter = new DashboardJobLogFilter();

        await this.LoadAsync();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        _ = firstRender;

        await this.FlushToastsAsync();
        await this.ScrollToLatestLogAsync();

        if (this.NeedsRelatedJobs())
        {
            await this.LoadRelatedJobsAsync();
        }
    }

    private void OpenCancelDialog()
      => this._cancelDialogOpen = true;

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A failed cancel is shown as a toast; it must not escape the event handler and end the circuit.")]
    private async Task CancelJobAsync()
    {
        if (this._isCancelRunning || this._detail?.Summary is not { } job)
        {
            return;
        }

        // The job can move on while the dialog is open.
        if (!DashboardJobActions.IsCancellable(job))
        {
            this.ShowToast("The job can't be canceled now", "It has finished, or a cancellation was already requested.");
            return;
        }

        this._isCancelRunning = true;

        try
        {
            var result = await this.JobManager.CancelAsync(this.JobId);
            var (title, description) = DashboardJobActions.DescribeCancellation([result]);
            this.ShowToast(
              title,
              description,
              result is JobCancellationResult.Canceled or JobCancellationResult.CancellationRequested ? AlertTone.Success : AlertTone.Neutral);
            await this.LiveRefresh.RefreshNowAsync();
        }
        catch (Exception exception)
        {
            this.ShowToast("Couldn't cancel the job", exception.Message, AlertTone.Destructive);
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

        if (jobEvent.JobId != this.JobId)
        {
            return;
        }

        await this.ApplyPageStateAsync(() =>
        {
            if (jobEvent.JobId != this.JobId)
            {
                return;
            }

            // A paused log stream takes no appends; the summary still reflects the live job.
            if (!this._logsPaused)
            {
                _ = this._eventLog.Append(jobEvent);
                this._scrollToLatestLog |= this._followLogs;
            }

            this.ApplyLiveEventToDetail(jobEvent);
            this.LiveRefresh.MarkLiveActivity(jobEvent.OccurredAtUtc);
        });
        this.LiveRefresh.QueueRefresh();
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A failed load is shown on the page; it must not escape the event handler and end the circuit.")]
    private async Task LoadAsync()
    {
        var jobId = this.JobId;
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
            var detail = await this.Reader.GetJobAsync(jobId);
            var events = detail is null
              ? []
              : await this.ReadEventPageAsync(jobId, afterEventSequence: null, CancellationToken.None);

            await this.ApplyPageStateAsync(() =>
            {
                if (jobId != this.JobId)
                {
                    return;
                }

                this._detail = detail;
                if (detail is not null)
                {
                    _ = this._eventLog.AppendPage(events);
                }

                this._isLoading = false;
                this.LiveRefresh.MarkUpdated();
            });
        }
        catch (Exception exception)
        {
            await this.ApplyPageStateAsync(() =>
            {
                if (jobId == this.JobId)
                {
                    this._error = exception.Message;
                    this._isLoading = false;
                }
            });
        }
    }

    // Explicit refresh (manual refresh, resume, auto-refresh re-enabled, after cancel): the job summary plus any
    // durable events not yet loaded, read incrementally after the loaded run.
    private Task RefreshDetailAsync(CancellationToken cancellationToken)
      => this.RefreshAsync(catchUpEvents: true, cancellationToken);

    // Live-event refresh: the pushed event is already in the log, so only the job summary is re-read.
    private Task RefreshSummaryAsync(CancellationToken cancellationToken)
      => this.RefreshAsync(catchUpEvents: false, cancellationToken);

    private async Task RefreshAsync(
        bool catchUpEvents,
        CancellationToken cancellationToken)
    {
        var jobId = this.JobId;
        long? readThroughSequence = null;
        var readEvents = false;
        await this.InvokeAsync(() =>
        {
            readThroughSequence = this._eventLog.ReadThroughSequence;
            readEvents = (catchUpEvents || this._detail is null)
              && !this._logsPaused
              && !this._eventLog.HasMoreToRead;
        });

        var detail = await this.Reader.GetJobAsync(jobId, cancellationToken);
        var events = detail is not null && readEvents
          ? await this.ReadEventPageAsync(jobId, readThroughSequence, cancellationToken)
          : [];

        await this.ApplyPageStateAsync(() =>
        {
            if (jobId != this.JobId)
            {
                return;
            }

            this._detail = detail;
            if (detail is not null && readEvents && !this._logsPaused)
            {
                _ = this._eventLog.AppendPage(events);
            }

            // An explicit refresh also re-reads the related jobs' states the next time they are shown.
            if (catchUpEvents)
            {
                this._relatedStale = true;
            }
        });
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

    private string TabHref(DashboardJobDetailTab tab)
      => DashboardJobDetailFormat.TabHref(this.JobId, tab);

    private IReadOnlyList<TabItem<DashboardJobDetailTab>> TabItems(
        int logLineCount,
        int lifecycleEventCount)
    {
        // Counts are of loaded events, so they are left off while more events remain to be loaded.
        var exact = !this.HasMoreEvents;
        var relatedCount = this._detail is null ? 0 : RelatedSections(this._detail).Sum(section => section.JobIds.Count);
        return
        [
            new(DashboardJobDetailTab.Logs, "Logs", exact ? logLineCount : null, Href: this.TabHref(DashboardJobDetailTab.Logs)),
            new(DashboardJobDetailTab.Timeline, "Timeline", exact ? lifecycleEventCount : null, Href: this.TabHref(DashboardJobDetailTab.Timeline)),
            new(DashboardJobDetailTab.Invocation, "Invocation", Href: this.TabHref(DashboardJobDetailTab.Invocation)),
            new(DashboardJobDetailTab.Related, "Related", relatedCount, Href: this.TabHref(DashboardJobDetailTab.Related)),
        ];
    }

    private static string FailedTitle(JobInspectionSummary job)
      => job.AttemptCount > 1
        ? string.Create(CultureInfo.InvariantCulture, $"Failed after {job.AttemptCount} attempts")
        : "Failed";

    private static string CanceledText(JobInspectionDetail detail)
      => detail.ClaimedAtUtc is null
        ? "The job was canceled before it started."
        : detail.Summary.CancellationObservedAtUtc is not null
          ? "The job was canceled while running and stopped at a cancellation check."
          : "The job was canceled while running.";

    private static AlertTone QueuedTone(JobInspectionSummary job)
      => job.QueuePosition?.Kind switch
      {
          JobQueuePositionKind.Claimable => AlertTone.Info,
          JobQueuePositionKind.RetryWaiting or JobQueuePositionKind.BlockedByConcurrency => AlertTone.Warning,
          _ => AlertTone.Neutral,
      };

    private static string QueuedIcon(JobInspectionSummary job)
      => job.QueuePosition?.Kind switch
      {
          JobQueuePositionKind.Delayed => "calendar-clock",
          JobQueuePositionKind.RetryWaiting => "rotate-ccw",
          JobQueuePositionKind.WaitingForDependencies => "git-fork",
          JobQueuePositionKind.BlockedByConcurrency => "layers",
          _ => "hourglass",
      };

    private static string QueuedText(
        JobInspectionSummary job,
        JobEvent? latestFailure)
    {
        var reason = job.QueuePosition?.Reason;
        return job.QueuePosition?.Kind switch
        {
            JobQueuePositionKind.RetryWaiting when latestFailure?.Message is { Length: > 0 } message
              => string.Create(CultureInfo.InvariantCulture, $"Attempt {latestFailure.AttemptNumber} failed: {message}"),
            _ when !string.IsNullOrWhiteSpace(reason) => reason,
            JobQueuePositionKind.Claimable => "The next free worker picks it up.",
            JobQueuePositionKind.Delayed => "The job isn't claimable until its not-before time.",
            JobQueuePositionKind.RetryWaiting => "The last attempt failed; the job runs again after the retry delay.",
            JobQueuePositionKind.WaitingForDependencies => "The job runs once all of its prerequisites have finished.",
            JobQueuePositionKind.BlockedByConcurrency => "Another job holds the only slot in one of its concurrency groups.",
            _ => "The job is waiting to be claimed.",
        };
    }
}
