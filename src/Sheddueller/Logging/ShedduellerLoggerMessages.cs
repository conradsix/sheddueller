namespace Sheddueller.Logging;

using Microsoft.Extensions.Logging;

internal static partial class ShedduellerLoggerMessages
{
    private const int EventIdStart = 1000;

    [LoggerMessage(
        EventIdStart + 0,
        LogLevel.Debug,
        "Enqueued job {JobId} with sequence {EnqueueSequence}.")]
    public static partial void JobEnqueued(
        this ILogger logger,
        Guid jobId,
        long enqueueSequence);

    [LoggerMessage(
        EventIdStart + 1,
        LogLevel.Debug,
        "Reused existing idempotent job {JobId} with sequence {EnqueueSequence}.")]
    public static partial void JobEnqueueDeduplicated(
        this ILogger logger,
        Guid jobId,
        long enqueueSequence);

    [LoggerMessage(
        EventIdStart + 2,
        LogLevel.Debug,
        "Submitted {SubmittedCount} jobs and enqueued {EnqueuedCount} new jobs.")]
    public static partial void JobsBatchEnqueued(
        this ILogger logger,
        int submittedCount,
        int enqueuedCount);

    [LoggerMessage(
        EventIdStart + 10,
        LogLevel.Debug,
        "Cancel request for job {JobId} returned {Result}.")]
    public static partial void JobCancellationRequested(
        this ILogger logger,
        Guid jobId,
        string result);

    [LoggerMessage(
        EventIdStart + 11,
        LogLevel.Debug,
        "Canceled {CanceledCount} queued jobs.")]
    public static partial void QueuedJobsCanceled(
        this ILogger logger,
        int canceledCount);

    [LoggerMessage(
        EventIdStart + 20,
        LogLevel.Debug,
        "Recurring schedule {ScheduleKey} upsert returned {Result}.")]
    public static partial void RecurringScheduleUpserted(
        this ILogger logger,
        string scheduleKey,
        string result);

    [LoggerMessage(
        EventIdStart + 21,
        LogLevel.Debug,
        "Recurring schedule {ScheduleKey} trigger returned {Status}.")]
    public static partial void RecurringScheduleTriggered(
        this ILogger logger,
        string scheduleKey,
        string status);

    [LoggerMessage(
        EventIdStart + 22,
        LogLevel.Debug,
        "Recurring schedule {ScheduleKey} delete returned {Deleted}.")]
    public static partial void RecurringScheduleDeleted(
        this ILogger logger,
        string scheduleKey,
        bool deleted);

    [LoggerMessage(
        EventIdStart + 23,
        LogLevel.Debug,
        "Recurring schedule {ScheduleKey} pause returned {Paused}.")]
    public static partial void RecurringSchedulePaused(
        this ILogger logger,
        string scheduleKey,
        bool paused);

    [LoggerMessage(
        EventIdStart + 24,
        LogLevel.Debug,
        "Recurring schedule {ScheduleKey} resume returned {Resumed}.")]
    public static partial void RecurringScheduleResumed(
        this ILogger logger,
        string scheduleKey,
        bool resumed);

    [LoggerMessage(
        EventIdStart + 30,
        LogLevel.Debug,
        "Set concurrency group {GroupKey} limit to {Limit}.")]
    public static partial void ConcurrencyGroupLimitSet(
        this ILogger logger,
        string groupKey,
        int limit);

    [LoggerMessage(
        EventIdStart + 31,
        LogLevel.Debug,
        "Set concurrency group {GroupKey} default limit to {Limit}.")]
    public static partial void ConcurrencyGroupDefaultLimitSet(
        this ILogger logger,
        string groupKey,
        int limit);

    [LoggerMessage(
        EventIdStart + 32,
        LogLevel.Debug,
        "Cleared concurrency group {GroupKey} limit override.")]
    public static partial void ConcurrencyGroupLimitOverrideCleared(
        this ILogger logger,
        string groupKey);

    [LoggerMessage(
        EventIdStart + 33,
        LogLevel.Debug,
        "Set concurrency group {GroupKey} rate limit to {PermitCount} starts per {Period}.")]
    public static partial void ConcurrencyGroupRateLimitSet(
        this ILogger logger,
        string groupKey,
        int permitCount,
        TimeSpan period);

    [LoggerMessage(
        EventIdStart + 34,
        LogLevel.Debug,
        "Set concurrency group {GroupKey} default rate limit to {PermitCount} starts per {Period}.")]
    public static partial void ConcurrencyGroupDefaultRateLimitSet(
        this ILogger logger,
        string groupKey,
        int permitCount,
        TimeSpan period);

    [LoggerMessage(
        EventIdStart + 35,
        LogLevel.Debug,
        "Cleared concurrency group {GroupKey} default rate limit.")]
    public static partial void ConcurrencyGroupDefaultRateLimitCleared(
        this ILogger logger,
        string groupKey);

    [LoggerMessage(
        EventIdStart + 36,
        LogLevel.Debug,
        "Set concurrency group {GroupKey} live rate override to unlimited.")]
    public static partial void ConcurrencyGroupUnlimitedRateLimitSet(
        this ILogger logger,
        string groupKey);

    [LoggerMessage(
        EventIdStart + 37,
        LogLevel.Debug,
        "Cleared concurrency group {GroupKey} rate-limit override.")]
    public static partial void ConcurrencyGroupRateLimitOverrideCleared(
        this ILogger logger,
        string groupKey);

    [LoggerMessage(
        EventIdStart + 40,
        LogLevel.Warning,
        "Failed to append durable job event for job {JobId}.")]
    public static partial void JobEventAppendFailed(
        this ILogger logger,
        Exception exception,
        Guid jobId);
}
