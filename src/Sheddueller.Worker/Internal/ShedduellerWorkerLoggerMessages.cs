namespace Sheddueller.Worker.Internal;

using Microsoft.Extensions.Logging;

internal static partial class ShedduellerWorkerLoggerMessages
{
    private const int EventIdStart = 1100;

    [LoggerMessage(
        EventIdStart + 0,
        LogLevel.Information,
        "Sheddueller worker node {NodeId} started.")]
    public static partial void WorkerStarted(
        this ILogger logger,
        string nodeId);

    [LoggerMessage(
        EventIdStart + 1,
        LogLevel.Information,
        "Sheddueller worker node {NodeId} stopped.")]
    public static partial void WorkerStopped(
        this ILogger logger,
        string nodeId);

    [LoggerMessage(
        EventIdStart + 2,
        LogLevel.Error,
        "Sheddueller worker node {NodeId} stopped unexpectedly.")]
    public static partial void WorkerFailed(
        this ILogger logger,
        Exception exception,
        string nodeId);

    [LoggerMessage(
        EventIdStart + 3,
        LogLevel.Error,
        "Sheddueller worker node {NodeId} failed to poll for jobs; retrying in {RetryDelayMs:D} ms.")]
    public static partial void WorkerLoopFailed(
        this ILogger logger,
        Exception exception,
        string nodeId,
        long retryDelayMs);

    [LoggerMessage(
        EventIdStart + 10,
        LogLevel.Debug,
        "Claimed job {JobId} for attempt {AttemptNumber} on node {NodeId}.")]
    public static partial void JobClaimed(
        this ILogger logger,
        Guid jobId,
        int attemptNumber,
        string nodeId);

    [LoggerMessage(
        EventIdStart + 20,
        LogLevel.Debug,
        "Completed job {JobId} attempt {AttemptNumber} on node {NodeId}.")]
    public static partial void JobCompleted(
        this ILogger logger,
        Guid jobId,
        int attemptNumber,
        string nodeId);

    [LoggerMessage(
        EventIdStart + 21,
        LogLevel.Error,
        "Job {JobId} attempt {AttemptNumber} failed on node {NodeId}.")]
    public static partial void JobFailed(
        this ILogger logger,
        Exception exception,
        Guid jobId,
        int attemptNumber,
        string nodeId);

    [LoggerMessage(
        EventIdStart + 22,
        LogLevel.Error,
        "Could not record outcome {Outcome} for job {JobId} attempt {AttemptNumber} on node {NodeId}; the job is recovered when its lease expires.")]
    public static partial void JobOutcomeRecordFailed(
        this ILogger logger,
        Exception exception,
        string outcome,
        Guid jobId,
        int attemptNumber,
        string nodeId);

    [LoggerMessage(
        EventIdStart + 30,
        LogLevel.Information,
        "Cancellation was observed for job {JobId} attempt {AttemptNumber} on node {NodeId}.")]
    public static partial void JobCancellationObserved(
        this ILogger logger,
        Guid jobId,
        int attemptNumber,
        string nodeId);

    [LoggerMessage(
        EventIdStart + 31,
        LogLevel.Warning,
        "Released job {JobId} attempt {AttemptNumber} on node {NodeId} before completion.")]
    public static partial void JobReleased(
        this ILogger logger,
        Guid jobId,
        int attemptNumber,
        string nodeId);

    [LoggerMessage(
        EventIdStart + 32,
        LogLevel.Warning,
        "Lost lease for job {JobId} attempt {AttemptNumber} on node {NodeId}.")]
    public static partial void JobLeaseRenewalLost(
        this ILogger logger,
        Guid jobId,
        int attemptNumber,
        string nodeId);

    [LoggerMessage(
        EventIdStart + 33,
        LogLevel.Information,
        "Cancellation was requested for job {JobId} attempt {AttemptNumber} on node {NodeId}.")]
    public static partial void JobCancellationRequestedObserved(
        this ILogger logger,
        Guid jobId,
        int attemptNumber,
        string nodeId);

    [LoggerMessage(
        EventIdStart + 34,
        LogLevel.Warning,
        "Heartbeat task failed for job {JobId} attempt {AttemptNumber} on node {NodeId}.")]
    public static partial void JobHeartbeatFailed(
        this ILogger logger,
        Exception exception,
        Guid jobId,
        int attemptNumber,
        string nodeId);

    [LoggerMessage(
        EventIdStart + 35,
        LogLevel.Warning,
        "Heartbeat for job {JobId} attempt {AttemptNumber} on node {NodeId} failed; retrying until its lease expires at {LeaseExpiresAtUtc:O}.")]
    public static partial void JobHeartbeatRetrying(
        this ILogger logger,
        Exception exception,
        Guid jobId,
        int attemptNumber,
        string nodeId,
        DateTimeOffset leaseExpiresAtUtc);

    [LoggerMessage(
        EventIdStart + 36,
        LogLevel.Warning,
        "Lease for job {JobId} attempt {AttemptNumber} on node {NodeId} expired at {LeaseExpiresAtUtc:O} without a successful renewal; cancelling the job because another node may now claim it.")]
    public static partial void JobLeasePresumedLost(
        this ILogger logger,
        Exception exception,
        Guid jobId,
        int attemptNumber,
        string nodeId,
        DateTimeOffset leaseExpiresAtUtc);

    [LoggerMessage(
        EventIdStart + 40,
        LogLevel.Information,
        "Worker node {NodeId} recovered {RecoveredCount} expired leases and materialized {MaterializedCount} recurring schedule occurrences.")]
    public static partial void WorkerPeriodicStoreWorkCompleted(
        this ILogger logger,
        string nodeId,
        int recoveredCount,
        int materializedCount);

    [LoggerMessage(
        EventIdStart + 50,
        LogLevel.Debug,
        "Worker job retention cleanup skipped because no retention store is registered.")]
    public static partial void WorkerJobRetentionStoreMissing(
        this ILogger logger);

    [LoggerMessage(
        EventIdStart + 51,
        LogLevel.Information,
        "Worker job retention cleanup deleted {DeletedCount} terminal jobs.")]
    public static partial void WorkerJobRetentionCleaned(
        this ILogger logger,
        int deletedCount);

    [LoggerMessage(
        EventIdStart + 52,
        LogLevel.Warning,
        "Worker job retention cleanup failed.")]
    public static partial void WorkerJobRetentionCleanupFailed(
        this ILogger logger,
        Exception exception);
}
