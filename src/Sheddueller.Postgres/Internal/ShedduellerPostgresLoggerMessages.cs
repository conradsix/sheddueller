namespace Microsoft.Extensions.Logging;

internal static partial class ShedduellerPostgresLoggerMessages
{
    private const int EventIdStart = 1200;

    [LoggerMessage(
        EventIdStart + 0,
        LogLevel.Debug,
        "PostgreSQL wake listener started for schema {SchemaName}.")]
    public static partial void PostgresWakeListenerStarted(
        this ILogger logger,
        string schemaName);

    [LoggerMessage(
        EventIdStart + 1,
        LogLevel.Warning,
        "PostgreSQL wake listener for schema {SchemaName} disconnected; retrying in {RetryDelayMs:D} ms.")]
    public static partial void PostgresWakeListenerRetrying(
        this ILogger logger,
        Exception exception,
        string schemaName,
        long retryDelayMs);

    [LoggerMessage(
        EventIdStart + 10,
        LogLevel.Debug,
        "PostgreSQL job-event listener started for schema {SchemaName}.")]
    public static partial void PostgresJobEventListenerStarted(
        this ILogger logger,
        string schemaName);

    [LoggerMessage(
        EventIdStart + 11,
        LogLevel.Warning,
        "PostgreSQL job-event listener for schema {SchemaName} disconnected; retrying in {RetryDelayMs:D} ms.")]
    public static partial void PostgresJobEventListenerRetrying(
        this ILogger logger,
        Exception exception,
        string schemaName,
        long retryDelayMs);

    [LoggerMessage(
        EventIdStart + 12,
        LogLevel.Debug,
        "Ignored PostgreSQL job-event notification with invalid payload for schema {SchemaName}.")]
    public static partial void PostgresJobEventNotificationPayloadInvalid(
        this ILogger logger,
        string schemaName);

    [LoggerMessage(
        EventIdStart + 13,
        LogLevel.Debug,
        "PostgreSQL job event {EventSequence} for job {JobId} was not found after notification.")]
    public static partial void PostgresJobEventNotificationMissing(
        this ILogger logger,
        Guid jobId,
        long eventSequence);

    [LoggerMessage(
        EventIdStart + 14,
        LogLevel.Warning,
        "Failed to publish PostgreSQL job event {EventSequence} for job {JobId}.")]
    public static partial void PostgresJobEventNotificationFailed(
        this ILogger logger,
        Exception exception,
        Guid jobId,
        long eventSequence);

    [LoggerMessage(
        EventIdStart + 15,
        LogLevel.Debug,
        "Dropped PostgreSQL job event {EventSequence} for job {JobId}: {Capacity:D} notifications are already waiting to be published.")]
    public static partial void PostgresJobEventNotificationDropped(
        this ILogger logger,
        Guid jobId,
        long eventSequence,
        int capacity);

    [LoggerMessage(
        EventIdStart + 16,
        LogLevel.Warning,
        "Failed to read {Count:D} notified PostgreSQL job events for schema {SchemaName}; they were not published.")]
    public static partial void PostgresJobEventNotificationBatchFailed(
        this ILogger logger,
        Exception exception,
        string schemaName,
        int count);

    [LoggerMessage(
        EventIdStart + 20,
        LogLevel.Warning,
        "PostgreSQL migration for schema {SchemaName} couldn't get a table lock within {LockTimeoutMs:D} ms (attempt {Attempt:D} of {MaxAttempts:D}); retrying in {RetryDelayMs:D} ms.")]
    public static partial void PostgresMigrationLockRetrying(
        this ILogger logger,
        Exception exception,
        string schemaName,
        long lockTimeoutMs,
        int attempt,
        int maxAttempts,
        long retryDelayMs);

    [LoggerMessage(
        EventIdStart + 30,
        LogLevel.Warning,
        "PostgreSQL metrics cleanup for schema {SchemaName} failed; the next job retention pass retries it.")]
    public static partial void PostgresMetricsCleanupFailed(
        this ILogger logger,
        Exception exception,
        string schemaName);
}
