namespace Sheddueller.Dashboard.Internal;

using System.Globalization;

using Sheddueller.Inspection.Jobs;
using Sheddueller.Inspection.Schedules;
using Sheddueller.Storage;

internal static class DashboardFormat
{
    private const int ShortJobIdLength = 8;

    public static string Count(int count)
      => count.ToString("N0", CultureInfo.InvariantCulture);

    public static string Count(long count)
      => count.ToString("N0", CultureInfo.InvariantCulture);

    public static string JobHref(Guid jobId)
      => string.Create(CultureInfo.InvariantCulture, $"jobs/{jobId:D}");

    public static string JobHref(JobInspectionSummary job)
      => JobHref(job.JobId);

    public static string JobId(Guid jobId)
      => jobId.ToString("D");

    /// <summary>
    /// The first block of a job ID, enough to tell jobs apart in a list; the full ID goes in its title.
    /// </summary>
    public static string ShortJobId(Guid jobId)
      => ShortJobId(JobId(jobId));

    /// <summary>
    /// The first block of a job ID as written in a URL; shorter text is returned unchanged.
    /// </summary>
    public static string ShortJobId(string jobId)
      => jobId.Length > ShortJobIdLength ? jobId[..ShortJobIdLength] : jobId;

    public static string FullHandler(JobInspectionSummary job)
      => FullHandler(job.ServiceType, job.MethodName);

    public static string FullHandler(ScheduleInspectionSummary schedule)
      => FullHandler(schedule.ServiceType, schedule.MethodName);

    public static string FullHandler(
        string serviceType,
        string methodName)
      => string.Concat(serviceType, ".", methodName);

    public static string ShortHandler(JobInspectionSummary job)
      => ShortHandler(job.ServiceType, job.MethodName);

    public static string ShortHandler(ScheduleInspectionSummary schedule)
      => ShortHandler(schedule.ServiceType, schedule.MethodName);

    /// <summary>
    /// The handler without namespace or assembly, as the jobs list filters by it: <c>RateLoader.LoadRates</c>.
    /// </summary>
    public static string ShortHandler(
        string serviceType,
        string methodName)
      => string.Concat(DashboardTypeName.Short(serviceType), ".", methodName);

    public static string Attempts(JobInspectionSummary job, bool compact = false)
      => compact
        ? string.Create(CultureInfo.InvariantCulture, $"{job.AttemptCount}/{job.MaxAttempts}")
        : string.Create(CultureInfo.InvariantCulture, $"{job.AttemptCount} / {job.MaxAttempts}");

    public static string Tag(JobTag tag)
      => string.Concat(tag.Name, ":", tag.Value);

    public static IReadOnlyList<string> Tags(IReadOnlyList<JobTag> tags)
      => [.. tags.Select(Tag)];

    public static string TagsTitle(IReadOnlyList<JobTag> tags)
      => tags.Count == 0 ? "No tags" : string.Join(", ", tags.Select(Tag));

    public static string GroupKeysTitle(IReadOnlyList<string> groupKeys)
      => groupKeys.Count == 0 ? "No group keys" : string.Join(", ", groupKeys);

    /// <summary>
    /// When the job reached its final state. A job that failed an attempt and then completed keeps its failure time,
    /// so the state decides which timestamp counts.
    /// </summary>
    public static DateTimeOffset? TerminalTimestamp(JobInspectionSummary job)
      => job.State switch
      {
          JobState.Completed => job.CompletedAtUtc,
          JobState.Failed => job.FailedAtUtc,
          JobState.Canceled => job.CanceledAtUtc,
          _ => null,
      };

    public static string ProgressText(
        JobProgressSnapshot? progress,
        string missingText = "No progress reported",
        string reportedText = "Progress reported")
    {
        if (progress is null)
        {
            return missingText;
        }

        if (progress.Percent is null)
        {
            return string.IsNullOrWhiteSpace(progress.Message) ? reportedText : progress.Message;
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{Math.Clamp(progress.Percent.Value, 0, 100):0.#}% {progress.Message}")
          .Trim();
    }

    public static string ProgressPercent(JobProgressSnapshot? progress)
      => progress?.Percent is { } percent
        ? string.Create(CultureInfo.InvariantCulture, $"{Math.Clamp(percent, 0, 100):0.#}%")
        : "N/A";

    /// <summary>
    /// The failure reason recorded with the job (its last progress message, or why it is queued), or
    /// <see langword="null" /> when there is none to show.
    /// </summary>
    public static string? FailureReason(JobInspectionSummary job)
      => new[] { job.LatestProgress?.Message, job.QueuePosition?.Reason }.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));

    public static string Disposition(JobInspectionSummary job)
      => job.State switch
      {
          JobState.Failed => FailureReason(job) ?? "failed",
          JobState.Completed => "completed",
          JobState.Canceled => "canceled",
          _ => QueueKind(job.QueuePosition?.Kind, job.State),
      };

    public static string DispositionClass(JobInspectionSummary job)
      => job.State == JobState.Failed ? "jobs-disposition--error" : "jobs-disposition--muted";

    public static string QueueKind(
        JobQueuePositionKind? kind,
        JobState? state)
      => kind switch
      {
          JobQueuePositionKind.Claimable => "claimable",
          JobQueuePositionKind.Delayed => "delayed",
          JobQueuePositionKind.RetryWaiting => "retry_waiting",
          JobQueuePositionKind.WaitingForDependencies => "waiting_for_dependencies",
          JobQueuePositionKind.BlockedByConcurrency => "blocked_by_concurrency",
          JobQueuePositionKind.Claimed => "running_active",
          JobQueuePositionKind.Terminal => "terminal",
          JobQueuePositionKind.Canceled => "canceled",
          JobQueuePositionKind.NotFound => "not_found",
          _ => state?.ToString().ToLowerInvariant() ?? "-",
      };

    /// <summary>
    /// The jobs list's queue cell: the claim position of a ready job, otherwise what it is waiting for.
    /// </summary>
    public static string QueuePositionText(JobInspectionSummary job)
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

    public static string QueuePositionTitle(JobInspectionSummary job)
      => job.QueuePosition?.Reason ?? QueuePositionText(job);

    public static string QueuePositionClass(JobInspectionSummary job)
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

    public static string StateCssModifier(JobState state)
      => state switch
      {
          JobState.Queued => "queued",
          JobState.Claimed => "claimed",
          JobState.Completed => "completed",
          JobState.Failed => "failed",
          JobState.Canceled => "canceled",
          _ => "queued",
      };

    public static string Relative(
        DateTimeOffset? value,
        DateTimeOffset nowUtc,
        string? prefix = null,
        string emptyText = "-",
        bool allowFuture = true)
    {
        if (value is null)
        {
            if (string.IsNullOrWhiteSpace(prefix))
            {
                return emptyText;
            }

            return emptyText == "-"
              ? string.Concat(prefix, " not set")
              : string.Concat(prefix, " ", emptyText);
        }

        var targetUtc = value.Value.ToUniversalTime();
        var delta = targetUtc - nowUtc.ToUniversalTime();
        if (!allowFuture && delta > TimeSpan.Zero)
        {
            delta = TimeSpan.Zero;
        }

        var future = delta > TimeSpan.Zero;
        var duration = future ? delta : -delta;
        var relative = future
          ? string.Concat("in ", Elapsed(duration))
          : string.Concat(Elapsed(duration), " ago");

        if (Math.Abs(delta.TotalSeconds) < 5)
        {
            relative = "just now";
        }

        return string.IsNullOrWhiteSpace(prefix)
          ? relative
          : string.Concat(prefix, " ", relative);
    }

    public static string Utc(DateTimeOffset? value)
      => value?.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture) ?? string.Empty;

    public static string UtcMinute(DateTimeOffset? value)
      => value?.ToUniversalTime().ToString("HH:mm 'UTC'", CultureInfo.InvariantCulture) ?? string.Empty;

    public static string FirstNonEmpty(params string?[] values)
      => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    /// <summary>
    /// A coarse duration for times that keep counting, such as how long a job has been waiting: <c>&lt;1m</c>,
    /// <c>42m</c>, <c>3h</c>, <c>2d</c>.
    /// </summary>
    public static string Elapsed(TimeSpan duration)
    {
        if (duration < TimeSpan.FromMinutes(1))
        {
            return "<1m";
        }

        if (duration < TimeSpan.FromHours(1))
        {
            return string.Create(CultureInfo.InvariantCulture, $"{Math.Floor(duration.TotalMinutes):0}m");
        }

        if (duration < TimeSpan.FromDays(1))
        {
            return string.Create(CultureInfo.InvariantCulture, $"{Math.Floor(duration.TotalHours):0}h");
        }

        return string.Create(CultureInfo.InvariantCulture, $"{Math.Floor(duration.TotalDays):0}d");
    }
}
