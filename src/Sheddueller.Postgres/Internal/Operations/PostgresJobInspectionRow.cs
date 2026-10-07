namespace Sheddueller.Postgres.Internal.Operations;

using Sheddueller.Storage;

internal sealed record PostgresJobInspectionRow(
    Guid JobId,
    JobState State,
    string ServiceType,
    string MethodName,
    int Priority,
    long EnqueueSequence,
    DateTimeOffset EnqueuedAtUtc,
    DateTimeOffset? NotBeforeUtc,
    int AttemptCount,
    int MaxAttempts,
    string? SourceScheduleKey,
    DateTimeOffset? CompletedAtUtc,
    DateTimeOffset? FailedAtUtc,
    DateTimeOffset? CanceledAtUtc,
    DateTimeOffset? ClaimedAtUtc,
    string? ClaimedByNodeId,
    DateTimeOffset? LeaseExpiresAtUtc,
    DateTimeOffset? ScheduledFireAtUtc,
    Guid? RetryCloneSourceJobId,
    DateTimeOffset? CancellationRequestedAtUtc,
    DateTimeOffset? CancellationObservedAtUtc,
    ScheduleOccurrenceKind? ScheduleOccurrenceKind);
