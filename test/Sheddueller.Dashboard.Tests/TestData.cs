namespace Sheddueller.Dashboard.Tests;

using System.Globalization;

using Sheddueller.Dashboard.Internal;
using Sheddueller.Inspection.Jobs;
using Sheddueller.Inspection.Metrics;
using Sheddueller.Storage;

/// <summary>
/// Builders for dashboard test inputs. Anything a test doesn't name gets a fixed default; set what the test is about
/// with a named argument, or with a <c>with</c> expression for the init-only members.
/// </summary>
internal static class TestData
{
    public static readonly Guid JobId = Guid.Parse("7c1e9a42-5b3d-4f1e-9a07-2d6c8e41b0f3");

    public static readonly DateTimeOffset EnqueuedAtUtc = DateTimeOffset.Parse("2026-04-20T12:00:00Z", CultureInfo.InvariantCulture);

    /// <summary>
    /// A queued job on its first of three attempts, enqueued at <see cref="EnqueuedAtUtc"/>.
    /// </summary>
    public static JobInspectionSummary Job(
        Guid? jobId = null,
        JobState state = JobState.Queued,
        string serviceType = "Sheddueller.Dashboard.Tests.Worker",
        string methodName = "Run",
        int priority = 0,
        long enqueueSequence = 1,
        DateTimeOffset? enqueuedAtUtc = null,
        DateTimeOffset? notBeforeUtc = null,
        int attemptCount = 1,
        int maxAttempts = 3,
        IReadOnlyList<JobTag>? tags = null,
        IReadOnlyList<string>? concurrencyGroupKeys = null,
        string? sourceScheduleKey = null,
        JobProgressSnapshot? latestProgress = null,
        JobQueuePosition? queuePosition = null,
        DateTimeOffset? claimedAtUtc = null,
        DateTimeOffset? completedAtUtc = null,
        DateTimeOffset? failedAtUtc = null,
        DateTimeOffset? canceledAtUtc = null)
      => new(
        jobId ?? JobId,
        state,
        serviceType,
        methodName,
        priority,
        enqueueSequence,
        enqueuedAtUtc ?? EnqueuedAtUtc,
        notBeforeUtc,
        attemptCount,
        maxAttempts,
        tags ?? [],
        concurrencyGroupKeys ?? [],
        sourceScheduleKey,
        latestProgress,
        queuePosition,
        claimedAtUtc,
        completedAtUtc,
        failedAtUtc,
        canceledAtUtc);

    /// <summary>
    /// An event of <see cref="JobId"/>, by default occurring <paramref name="sequence"/> seconds after
    /// <see cref="EnqueuedAtUtc"/>. The event id is derived from the sequence, so building the same event twice gives
    /// equal records, as reading it again from the store would.
    /// </summary>
    public static JobEvent Event(
        long sequence = 1,
        JobEventKind kind = JobEventKind.Log,
        int attempt = 1,
        string? message = null,
        JobLogLevel? level = null,
        double? progressPercent = null,
        DateTimeOffset? occurredAtUtc = null)
      => new(
        Guid.Parse(string.Create(CultureInfo.InvariantCulture, $"00000000-0000-0000-0000-{sequence:D12}")),
        JobId,
        sequence,
        kind,
        occurredAtUtc ?? EnqueuedAtUtc.AddSeconds(sequence),
        attempt,
        level,
        message,
        progressPercent);

    /// <summary>
    /// A five-minute metrics window with no activity.
    /// </summary>
    public static MetricsInspectionWindow MetricsWindow(int queuedCount = 0)
      => new(
        TimeSpan.FromMinutes(5),
        queuedCount,
        ClaimedCount: 0,
        FailedCount: 0,
        CanceledCount: 0,
        OldestQueuedAge: null,
        EnqueueRatePerMinute: 0,
        ClaimRatePerMinute: 0,
        SuccessRatePerMinute: 0,
        FailureRatePerMinute: 0,
        CancellationRatePerMinute: 0,
        RetryRatePerMinute: 0,
        P50QueueLatency: null,
        P95QueueLatency: null,
        P50ExecutionDuration: null,
        P95ExecutionDuration: null,
        P95ScheduleFireLag: null,
        SaturatedConcurrencyGroupCount: 0,
        ActiveNodeCount: 0,
        StaleNodeCount: 0,
        DeadNodeCount: 0);

    /// <summary>
    /// A page's refresh coordinator that applies changes inline and doesn't wait out its throttle.
    /// </summary>
    public static DashboardLiveRefreshCoordinator RefreshCoordinator(
        bool autoRefreshEnabled = true,
        Action? onRefresh = null)
      => new(
        _ =>
        {
            onRefresh?.Invoke();
            return Task.CompletedTask;
        },
        action =>
        {
            action();
            return Task.CompletedTask;
        },
        autoRefreshEnabled: autoRefreshEnabled,
        delayAsync: (_, _) => Task.CompletedTask);

    public static DashboardShellRefreshOptions ShellRefreshOptions()
      => new(
        ShowRefreshing: true,
        LastUpdatedUtc: _ => null);
}
