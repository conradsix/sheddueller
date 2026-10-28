namespace Sheddueller.Postgres.Internal.Operations;

using System.Globalization;

/// <summary>
/// The messages of the lifecycle events this provider writes.
/// </summary>
/// <remarks>
/// Metrics rollups tell lifecycle events apart by these messages (see
/// <see cref="PostgresMetricsRollups.ClassifyJobEvent" />), so every writer uses these constants rather than literals.
/// </remarks>
internal static class PostgresJobEventMessages
{
    public const string Queued = "Queued";

    public const string Completed = "Completed";

    public const string Failed = "Failed";

    public const string Canceled = "Canceled";

    public const string RetryScheduled = "Retry scheduled";

    public const string Released = "Released";

    /// <summary>
    /// The start of the message of a job that failed because a queued job with its idempotency key superseded it.
    /// </summary>
    public const string SupersededFailurePrefix = "Failed; superseded by queued idempotent job ";

    public static string SupersededFailure(Guid duplicateJobId)
      => string.Create(CultureInfo.InvariantCulture, $"{SupersededFailurePrefix}{duplicateJobId:D}");
}
