namespace Sheddueller.Dashboard.Internal;

using Sheddueller.Inspection.Jobs;
using Sheddueller.Storage;

/// <summary>
/// How long a job waited and ran, for the job page's timing tiles. Read from the job and its loaded events, since a
/// finished job no longer carries its claim.
/// </summary>
internal sealed record DashboardJobTiming(
    JobState State,
    DateTimeOffset EnqueuedAtUtc,
    DateTimeOffset? FirstStartedAtUtc,
    DateTimeOffset? LastStartedAtUtc,
    DateTimeOffset? LastEndedAtUtc)
{
    public static DashboardJobTiming From(JobInspectionDetail detail, IEnumerable<JobEvent> events)
    {
        ArgumentNullException.ThrowIfNull(detail);
        ArgumentNullException.ThrowIfNull(events);

        DateTimeOffset? firstStarted = null;
        DateTimeOffset? lastStarted = null;
        DateTimeOffset? lastFailed = null;
        foreach (var jobEvent in events)
        {
            if (jobEvent.Kind == JobEventKind.AttemptStarted)
            {
                firstStarted = Earliest(firstStarted, jobEvent.OccurredAtUtc);
                lastStarted = Latest(lastStarted, jobEvent.OccurredAtUtc);
            }
            else if (jobEvent.Kind == JobEventKind.AttemptFailed)
            {
                lastFailed = Latest(lastFailed, jobEvent.OccurredAtUtc);
            }
        }

        var summary = detail.Summary;
        return new DashboardJobTiming(
          summary.State,
          summary.EnqueuedAtUtc,
          firstStarted ?? detail.ClaimedAtUtc,
          detail.ClaimedAtUtc ?? lastStarted,
          DashboardFormat.TerminalTimestamp(summary) ?? (summary.State == JobState.Queued ? lastFailed ?? summary.FailedAtUtc : null));
    }

    public bool IsRunning
      => this.State == JobState.Claimed;

    /// <summary>
    /// Queued and never started, so the wait is still going.
    /// </summary>
    public bool IsWaiting
      => this.State == JobState.Queued && this.FirstStartedAtUtc is null;

    /// <summary>
    /// Time from enqueue to the first attempt, or to the end for a job that finished without starting.
    /// </summary>
    public TimeSpan? Waited
      => (this.FirstStartedAtUtc ?? (this.IsTerminal ? this.LastEndedAtUtc : null)) is { } waitEndedAtUtc
        ? NonNegative(waitEndedAtUtc - this.EnqueuedAtUtc)
        : null;

    /// <summary>
    /// How long the latest attempt ran, once it has ended.
    /// </summary>
    public TimeSpan? RunTime
      => !this.IsRunning && this.LastStartedAtUtc is { } startedAtUtc && this.LastEndedAtUtc is { } endedAtUtc
        ? NonNegative(endedAtUtc - startedAtUtc)
        : null;

    public string LastRunLabel
      => this.State switch
      {
          JobState.Completed => "Completed",
          JobState.Failed => "Failed",
          JobState.Canceled => "Canceled",
          JobState.Claimed => "Started",
          _ => this.LastEndedAtUtc is null ? "Last run" : "Last attempt",
      };

    /// <summary>
    /// When the job finished, started its running attempt, or last failed; <see langword="null" /> if it never ran.
    /// </summary>
    public DateTimeOffset? LastRunAtUtc
      => this.IsRunning ? this.LastStartedAtUtc : this.LastEndedAtUtc;

    private bool IsTerminal
      => this.State is JobState.Completed or JobState.Failed or JobState.Canceled;

    private static TimeSpan? NonNegative(TimeSpan duration)
      => duration < TimeSpan.Zero ? null : duration;

    private static DateTimeOffset Earliest(DateTimeOffset? current, DateTimeOffset candidate)
      => current is { } value && value <= candidate ? value : candidate;

    private static DateTimeOffset Latest(DateTimeOffset? current, DateTimeOffset candidate)
      => current is { } value && value >= candidate ? value : candidate;
}
