namespace Sheddueller.Postgres.Internal.Operations;

using Sheddueller.Serialization;
using Sheddueller.Storage;

internal sealed record PostgresClaimedJob(
    Guid JobId,
    int AttemptCount,
    int MaxAttempts,
    RetryBackoffKind? RetryBackoffKind,
    TimeSpan? RetryBaseDelay,
    TimeSpan? RetryMaxDelay,
    string? IdempotencyKey,
    IReadOnlyList<string> GroupKeys,
    DateTimeOffset? CancellationRequestedAtUtc);

internal sealed record PostgresRetryPolicy(
    bool IsConfigured,
    int MaxAttempts,
    RetryBackoffKind? BackoffKind,
    TimeSpan? BaseDelay,
    TimeSpan? MaxDelay);

internal sealed record PostgresScheduleDefinition(
    string ScheduleKey,
    string CronExpression,
    bool IsPaused,
    RecurringOverlapMode OverlapMode,
    int Priority,
    string ServiceType,
    string MethodName,
    IReadOnlyList<string> MethodParameterTypes,
    JobInvocationTargetKind InvocationTargetKind,
    IReadOnlyList<JobMethodParameterBinding>? MethodParameterBindings,
    SerializedJobPayload SerializedArguments,
    IReadOnlyList<string> ConcurrencyGroupKeys,
    IReadOnlyList<JobTag> Tags,
    RetryPolicy? RetryPolicy,
    DateTimeOffset? NextFireAtUtc)
{
    public bool EqualsRequest(UpsertRecurringScheduleRequest request)
      => string.Equals(this.CronExpression, request.CronExpression, StringComparison.Ordinal)
        && this.OverlapMode == request.OverlapMode
        && this.Priority == request.Priority
        && string.Equals(this.ServiceType, request.ServiceType, StringComparison.Ordinal)
        && string.Equals(this.MethodName, request.MethodName, StringComparison.Ordinal)
        && this.MethodParameterTypes.SequenceEqual(request.MethodParameterTypes, StringComparer.Ordinal)
        && this.InvocationTargetKind == request.InvocationTargetKind
        && ParameterBindingsEqual(this.MethodParameterBindings, request.MethodParameterBindings)
        && string.Equals(this.SerializedArguments.ContentType, request.SerializedArguments.ContentType, StringComparison.Ordinal)
        && this.SerializedArguments.Data.SequenceEqual(request.SerializedArguments.Data)
        && this.ConcurrencyGroupKeys.SequenceEqual(request.ConcurrencyGroupKeys, StringComparer.Ordinal)
        && this.Tags.SequenceEqual(request.Tags ?? [])
        && this.RetryPolicy == request.RetryPolicy;

    private static bool ParameterBindingsEqual(
        IReadOnlyList<JobMethodParameterBinding>? left,
        IReadOnlyList<JobMethodParameterBinding>? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        return left.SequenceEqual(right);
    }
}
