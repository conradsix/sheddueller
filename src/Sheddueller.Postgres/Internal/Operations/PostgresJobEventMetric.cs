namespace Sheddueller.Postgres.Internal.Operations;

/// <summary>
/// What a job event counts towards in the metrics rollups.
/// </summary>
internal enum PostgresJobEventMetric
{
    /// <summary>
    /// The event doesn't affect metrics, like a log or progress event.
    /// </summary>
    None,

    Enqueued,

    AttemptStarted,

    AttemptFailed,

    Succeeded,

    Failed,

    Canceled,
}
