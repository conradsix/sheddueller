namespace Sheddueller.Dashboard.Internal;

using System.Globalization;

using Sheddueller.Inspection.Jobs;
using Sheddueller.Storage;

internal static class DashboardFormat
{
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

    public static string FullHandler(JobInspectionSummary job)
      => string.Concat(job.ServiceType, ".", job.MethodName);

    public static string ShortHandler(JobInspectionSummary job)
      => string.Concat(ShortTypeName(job.ServiceType), ".", job.MethodName);

    public static string InvocationHandler(JobInvocationInspection invocation)
      => string.Concat(ShortTypeName(invocation.ServiceType), ".", invocation.MethodName);

    public static string InvocationBinding(JobInvocationParameterInspection parameter)
      => parameter.Binding.Kind switch
      {
          JobMethodParameterBindingKind.Serialized => "serialized",
          JobMethodParameterBindingKind.CancellationToken => "scheduler-owned",
          JobMethodParameterBindingKind.JobContext => "Job.Context",
          JobMethodParameterBindingKind.Service => string.Create(
            CultureInfo.InvariantCulture,
            $"Job.Resolve<{ShortTypeName(parameter.Binding.ServiceType ?? parameter.ParameterType)}>()"),
          _ => parameter.Binding.Kind.ToString(),
      };

    public static string InvocationStatus(JobInvocationInspection invocation)
      => invocation.SerializedArgumentsStatus == JobSerializedArgumentsInspectionStatus.Displayable
        ? "Serialized arguments are displayable."
        : FirstNonEmpty(invocation.SerializedArgumentsStatusMessage, invocation.SerializedArgumentsStatus.ToString());

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

    public static string Nullable(string? value)
      => string.IsNullOrWhiteSpace(value) ? "-" : value;

    public static DateTimeOffset? TerminalTimestamp(JobInspectionSummary job)
      => job.FailedAtUtc ?? job.CompletedAtUtc ?? job.CanceledAtUtc;

    public static TimeSpan? RunTimeDuration(JobInspectionDetail detail)
    {
        var claimedAtUtc = detail.ClaimedAtUtc;
        var terminalAtUtc = TerminalTimestamp(detail.Summary);
        if (claimedAtUtc is null || terminalAtUtc is null)
        {
            return null;
        }

        var duration = terminalAtUtc.Value - claimedAtUtc.Value;
        return duration < TimeSpan.Zero ? null : duration;
    }

    public static string RunTime(JobInspectionDetail detail)
      => DashboardMetricsFormat.Duration(RunTimeDuration(detail));

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

    public static string ProgressWidthStyle(JobProgressSnapshot? progress)
    {
        var percent = progress?.Percent is { } value ? Math.Clamp(value, 0, 100) : 0;
        return string.Create(CultureInfo.InvariantCulture, $"width: {percent:0.##}%");
    }

    public static string FailureContext(JobInspectionSummary job)
      => FirstNonEmpty(
        job.LatestProgress?.Message,
        job.QueuePosition?.Reason,
        "Unavailable");

    public static string Disposition(JobInspectionSummary job)
      => job.State switch
      {
          JobState.Failed => FirstNonEmpty(job.LatestProgress?.Message, job.QueuePosition?.Reason, "failed"),
          JobState.Completed => "completed",
          JobState.Canceled => "canceled",
          _ => QueueKind(job.QueuePosition?.Kind, job.State),
      };

    public static string QueuePosition(JobQueuePosition? position)
      => position is null
        ? "-"
        : position.Position is null
          ? QueueKind(position.Kind, state: null)
          : string.Create(CultureInfo.InvariantCulture, $"{QueueKind(position.Kind, state: null)} #{position.Position.Value}");

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
          ? string.Concat("in ", Duration(duration))
          : string.Concat(Duration(duration), " ago");

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

    public static string UtcClock(
        DateTimeOffset value,
        bool includeMilliseconds = false)
      => value.ToUniversalTime().ToString(includeMilliseconds ? "HH:mm:ss.fff 'UTC'" : "HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);

    public static bool IsLifecycleEvent(JobEvent jobEvent)
      => jobEvent.Kind is JobEventKind.Lifecycle
        or JobEventKind.AttemptStarted
        or JobEventKind.AttemptCompleted
        or JobEventKind.AttemptFailed
        or JobEventKind.CancelRequested
        or JobEventKind.CancelObserved;

    public static bool IsLogEvent(JobEvent jobEvent)
      => jobEvent.Kind is JobEventKind.Log or JobEventKind.Progress;

    public static string TimelineTitle(JobEvent jobEvent)
      => jobEvent.Kind switch
      {
          JobEventKind.Lifecycle => string.Concat("Lifecycle: ", FirstNonEmpty(jobEvent.Message, "Event")),
          JobEventKind.AttemptStarted => string.Create(CultureInfo.InvariantCulture, $"Attempt {jobEvent.AttemptNumber} Started"),
          JobEventKind.AttemptCompleted => string.Create(CultureInfo.InvariantCulture, $"Attempt {jobEvent.AttemptNumber} Completed"),
          JobEventKind.AttemptFailed => string.Create(CultureInfo.InvariantCulture, $"Attempt {jobEvent.AttemptNumber} Failed"),
          JobEventKind.CancelRequested => "Cancellation Requested",
          JobEventKind.CancelObserved => "Cancellation Observed",
          _ => jobEvent.Kind.ToString(),
      };

    public static bool ShouldRenderTimelineMessage(JobEvent jobEvent)
      => jobEvent.Kind != JobEventKind.Lifecycle && !string.IsNullOrWhiteSpace(jobEvent.Message);

    public static string TimelineItemClass(JobEvent jobEvent)
      => jobEvent.Kind switch
      {
          JobEventKind.AttemptStarted => "job-detail-timeline-item job-detail-timeline-item--active",
          JobEventKind.AttemptCompleted => "job-detail-timeline-item job-detail-timeline-item--completed",
          JobEventKind.AttemptFailed => "job-detail-timeline-item job-detail-timeline-item--failed",
          _ => "job-detail-timeline-item",
      };

    public static string LogLevel(JobEvent jobEvent)
      => jobEvent.Kind == JobEventKind.Progress
        ? "PROGRESS"
        : jobEvent.LogLevel switch
        {
            JobLogLevel.Trace => "TRACE",
            JobLogLevel.Debug => "DEBUG",
            JobLogLevel.Information => "INFO",
            JobLogLevel.Warning => "WARN",
            JobLogLevel.Error => "ERROR",
            JobLogLevel.Critical => "CRIT",
            _ => jobEvent.Kind.ToString().ToUpperInvariant(),
        };

    public static string LogMessage(JobEvent jobEvent)
    {
        if (jobEvent.Kind == JobEventKind.Progress)
        {
            return jobEvent.ProgressPercent is { } percent
              ? string.Create(CultureInfo.InvariantCulture, $"{Math.Clamp(percent, 0, 100):0.#}% {jobEvent.Message}").Trim()
              : FirstNonEmpty(jobEvent.Message, "Progress reported");
        }

        return FirstNonEmpty(jobEvent.Message, jobEvent.Kind.ToString());
    }

    public static string LogLevelClass(JobEvent jobEvent)
    {
        var suffix = jobEvent.Kind == JobEventKind.Progress
          ? "progress"
          : jobEvent.LogLevel?.ToString().ToLowerInvariant() ?? "information";

        return string.Concat("job-detail-log-level--", suffix);
    }

    public static string FirstNonEmpty(params string?[] values)
      => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    public static string ShortTypeName(string typeName)
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

    private static string Duration(TimeSpan duration)
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
