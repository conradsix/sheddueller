namespace Sheddueller.Dashboard.Internal;

using System.Globalization;

using Sheddueller.Dashboard.Components.Ui;
using Sheddueller.Inspection.Jobs;
using Sheddueller.Storage;

/// <summary>
/// The sections of the job detail page.
/// </summary>
internal enum DashboardJobDetailTab
{
    Logs,
    Timeline,
    Invocation,
    Related,
}

/// <summary>
/// Text and links for the job detail page.
/// </summary>
internal static class DashboardJobDetailFormat
{
    public const string TabParameter = "tab";

    /// <summary>
    /// The tab named by the <c>tab</c> query value; anything unrecognised opens the logs.
    /// </summary>
    public static DashboardJobDetailTab ParseTab(string? value)
      => value?.Trim().ToLowerInvariant() switch
      {
          "timeline" => DashboardJobDetailTab.Timeline,
          "invocation" => DashboardJobDetailTab.Invocation,
          "related" => DashboardJobDetailTab.Related,
          _ => DashboardJobDetailTab.Logs,
      };

    /// <summary>
    /// Link to a tab of a job. The logs are the default, so their link carries no <c>tab</c> value.
    /// </summary>
    public static string TabHref(
        Guid jobId,
        DashboardJobDetailTab tab)
      => tab == DashboardJobDetailTab.Logs
        ? DashboardFormat.JobHref(jobId)
        : DashboardQueryString.Build(DashboardFormat.JobHref(jobId), [(TabParameter, tab.ToString().ToLowerInvariant())]);

    public static string Attempts(JobInspectionSummary job)
      => job.AttemptCount == 0
        ? string.Create(CultureInfo.InvariantCulture, $"Not started · up to {job.MaxAttempts} {(job.MaxAttempts == 1 ? "attempt" : "attempts")}")
        : string.Create(CultureInfo.InvariantCulture, $"Attempt {job.AttemptCount} of {job.MaxAttempts}");

    /// <summary>
    /// The header's attempt pill: amber once the job has been retried, red when its last allowed attempt failed.
    /// </summary>
    public static string AttemptClass(JobInspectionSummary job)
      => job switch
      {
          { State: JobState.Failed } when job.AttemptCount >= job.MaxAttempts => "job-detail-attempt job-detail-attempt--exhausted",
          { AttemptCount: > 1 } => "job-detail-attempt job-detail-attempt--retried",
          _ => "job-detail-attempt",
      };

    /// <summary>
    /// The most recent failed attempt in the loaded events, which carries the failure message.
    /// </summary>
    public static JobEvent? LatestFailure(IEnumerable<JobEvent> events)
      => events.LastOrDefault(jobEvent => jobEvent.Kind == JobEventKind.AttemptFailed);

    /// <summary>
    /// Describes the filters on the jobs list the user came from, for the back link; <see langword="null" /> when
    /// the list is unfiltered.
    /// </summary>
    public static string? JobsListContext(string jobsListHref)
    {
        var queryIndex = jobsListHref.IndexOf('?', StringComparison.Ordinal);
        if (queryIndex < 0)
        {
            return null;
        }

        var filters = DashboardJobFilterQuery.ParseQuery(jobsListHref[queryIndex..]);
        var parts = new List<string>(4);
        if (filters.SelectedStates.Count > 0)
        {
            parts.Add(string.Join(", ", filters.SelectedStates.Select(StatusBadge.StateLabel)));
        }

        AddTextFilter(parts, "handler", filters.HandlerContains);
        AddTextFilter(parts, "tag", filters.TagContains);
        AddTextFilter(parts, "group", filters.ConcurrencyGroupContains);
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    /// <summary>
    /// Headline for a queued job: why it is not running yet.
    /// </summary>
    public static string QueueTitle(JobInspectionSummary job)
      => job.QueuePosition switch
      {
          { Kind: JobQueuePositionKind.Claimable, Position: { } position }
            => string.Create(CultureInfo.InvariantCulture, $"Waiting for a worker · #{position} in the queue"),
          { Kind: JobQueuePositionKind.Claimable } => "Ready for a worker",
          { Kind: JobQueuePositionKind.Delayed } => "Scheduled to run later",
          { Kind: JobQueuePositionKind.RetryWaiting } => "Waiting to retry",
          { Kind: JobQueuePositionKind.WaitingForDependencies } => "Waiting for prerequisites",
          { Kind: JobQueuePositionKind.BlockedByConcurrency } => "Blocked by a concurrency group",
          _ => "Queued",
      };

    /// <summary>
    /// Events shown on the timeline: lifecycle changes, attempts, and cancellation.
    /// </summary>
    public static bool IsTimelineEvent(JobEvent jobEvent)
      => jobEvent.Kind is JobEventKind.Lifecycle
        or JobEventKind.AttemptStarted
        or JobEventKind.AttemptCompleted
        or JobEventKind.AttemptFailed
        or JobEventKind.CancelRequested
        or JobEventKind.CancelObserved;

    public static string TimelineTitle(JobEvent jobEvent)
      => jobEvent.Kind switch
      {
          JobEventKind.Lifecycle => DashboardFormat.FirstNonEmpty(jobEvent.Message, "Lifecycle event"),
          JobEventKind.AttemptStarted => string.Create(CultureInfo.InvariantCulture, $"Attempt {jobEvent.AttemptNumber} started"),
          JobEventKind.AttemptCompleted => string.Create(CultureInfo.InvariantCulture, $"Attempt {jobEvent.AttemptNumber} completed"),
          JobEventKind.AttemptFailed => string.Create(CultureInfo.InvariantCulture, $"Attempt {jobEvent.AttemptNumber} failed"),
          JobEventKind.CancelRequested => "Cancellation requested",
          JobEventKind.CancelObserved => "Worker stopped for cancellation",
          _ => jobEvent.Kind.ToString(),
      };

    /// <summary>
    /// Extra text under a timeline entry: the failure message of a failed attempt.
    /// </summary>
    public static string? TimelineDetail(JobEvent jobEvent)
      => jobEvent.Kind == JobEventKind.AttemptFailed && !string.IsNullOrWhiteSpace(jobEvent.Message)
        ? jobEvent.Message
        : null;

    /// <summary>
    /// CSS modifier for the entry's dot, named after the job state colours: <c>queued</c>, <c>claimed</c>,
    /// <c>completed</c>, <c>failed</c>, <c>canceled</c>, <c>waiting</c>, or <c>neutral</c>.
    /// </summary>
    public static string TimelineTone(JobEvent jobEvent)
      => jobEvent.Kind switch
      {
          JobEventKind.AttemptStarted => "claimed",
          JobEventKind.AttemptCompleted => "completed",
          JobEventKind.AttemptFailed => "failed",
          JobEventKind.CancelRequested or JobEventKind.CancelObserved => "waiting",
          JobEventKind.Lifecycle => LifecycleTone(jobEvent.Message),
          _ => "neutral",
      };

    private static string LifecycleTone(string? message)
    {
        var text = message?.Trim() ?? string.Empty;
        if (text.StartsWith("Failed", StringComparison.OrdinalIgnoreCase))
        {
            return "failed";
        }

        return text.ToLowerInvariant() switch
        {
            "queued" => "queued",
            "completed" => "completed",
            "canceled" => "canceled",
            "retry scheduled" => "waiting",
            _ => "neutral",
        };
    }

    private static void AddTextFilter(
        List<string> parts,
        string label,
        string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            parts.Add(string.Concat(label, " “", value.Trim(), "”"));
        }
    }
}
