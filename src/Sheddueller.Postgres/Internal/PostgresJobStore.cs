namespace Sheddueller.Postgres.Internal;

using Sheddueller.Postgres.Internal.Operations;
using Sheddueller.Storage;

/// <summary>
/// The PostgreSQL job store: the worker and enqueue operations, and job event appends.
/// </summary>
internal sealed class PostgresJobStore(PostgresOperationContext context) : IJobStore, IJobEventSink
{
    public ValueTask<EnqueueJobResult> EnqueueAsync(
        EnqueueJobRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return EnqueueJobOperation.ExecuteAsync(context, request, cancellationToken);
    }

    public ValueTask<IReadOnlyList<EnqueueJobResult>> EnqueueManyAsync(
        IReadOnlyList<EnqueueJobRequest> requests,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);

        return EnqueueJobOperation.ExecuteManyAsync(context, requests, cancellationToken);
    }

    public ValueTask<ClaimJobResult> TryClaimNextAsync(
        ClaimJobRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return TryClaimNextJobOperation.ExecuteAsync(context, request, cancellationToken);
    }

    public ValueTask<bool> MarkCompletedAsync(
        CompleteJobRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return MarkJobCompletedOperation.ExecuteAsync(context, request, cancellationToken);
    }

    public ValueTask<bool> MarkFailedAsync(
        FailJobRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return MarkJobFailedOperation.ExecuteAsync(context, request, cancellationToken);
    }

    public ValueTask<bool> RenewLeaseAsync(
        RenewLeaseRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return RenewJobLeaseOperation.ExecuteAsync(context, request, cancellationToken);
    }

    public ValueTask<bool> ReleaseJobAsync(
        ReleaseJobRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return ReleaseJobOperation.ExecuteAsync(context, request, cancellationToken);
    }

    public ValueTask<int> RecoverExpiredLeasesAsync(
        RecoverExpiredLeasesRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return RecoverExpiredLeasesOperation.ExecuteAsync(context, cancellationToken);
    }

    public ValueTask<JobCancellationResult> CancelAsync(
        CancelJobRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return CancelJobOperation.ExecuteAsync(context, request, cancellationToken);
    }

    public ValueTask<int> CancelQueuedJobsAsync(
        CancelQueuedJobsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return CancelQueuedJobsOperation.ExecuteAsync(context, request, cancellationToken);
    }

    public ValueTask<DateTimeOffset?> GetCancellationRequestedAtAsync(
        JobCancellationStatusRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return PostgresWorkerOperations.GetCancellationRequestedAtAsync(context, request, cancellationToken);
    }

    public ValueTask<bool> MarkCancellationObservedAsync(
        ObserveJobCancellationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return PostgresWorkerOperations.MarkCancellationObservedAsync(context, request, cancellationToken);
    }

    public ValueTask RecordWorkerNodeHeartbeatAsync(
        WorkerNodeHeartbeatRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return PostgresWorkerOperations.RecordWorkerNodeHeartbeatAsync(context, request, cancellationToken);
    }

    public ValueTask SetConcurrencyLimitAsync(
        SetConcurrencyLimitRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return SetConcurrencyLimitOperation.ExecuteAsync(context, request, cancellationToken);
    }

    public ValueTask SetConcurrencyDefaultLimitAsync(
        SetConcurrencyDefaultLimitRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return SetConcurrencyDefaultLimitOperation.ExecuteAsync(context, request, cancellationToken);
    }

    public ValueTask ClearConcurrencyLimitOverrideAsync(
        ClearConcurrencyLimitOverrideRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return ClearConcurrencyLimitOverrideOperation.ExecuteAsync(context, request, cancellationToken);
    }

    public ValueTask<int?> GetConfiguredConcurrencyLimitAsync(
        string groupKey,
        CancellationToken cancellationToken = default)
      => GetConfiguredConcurrencyLimitOperation.ExecuteAsync(context, groupKey, cancellationToken);

    public ValueTask SetConcurrencyRateLimitAsync(
        SetConcurrencyRateLimitRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return SetConcurrencyRateLimitOperation.ExecuteAsync(context, request, cancellationToken);
    }

    public ValueTask SetConcurrencyDefaultRateLimitAsync(
        SetConcurrencyDefaultRateLimitRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return SetConcurrencyDefaultRateLimitOperation.ExecuteAsync(context, request, cancellationToken);
    }

    public ValueTask ClearConcurrencyDefaultRateLimitAsync(
        ClearConcurrencyDefaultRateLimitRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return ClearConcurrencyDefaultRateLimitOperation.ExecuteAsync(context, request, cancellationToken);
    }

    public ValueTask SetConcurrencyUnlimitedRateLimitAsync(
        SetConcurrencyUnlimitedRateLimitRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return SetConcurrencyUnlimitedRateLimitOperation.ExecuteAsync(context, request, cancellationToken);
    }

    public ValueTask ClearConcurrencyRateLimitOverrideAsync(
        ClearConcurrencyRateLimitOverrideRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return ClearConcurrencyRateLimitOverrideOperation.ExecuteAsync(context, request, cancellationToken);
    }

    public ValueTask<ConcurrencyGroupRateLimitOverride> GetConcurrencyRateLimitOverrideAsync(
        string groupKey,
        CancellationToken cancellationToken = default)
      => PostgresConcurrencyRateLimits.GetOverrideAsync(context, groupKey, cancellationToken);

    public ValueTask<RecurringScheduleUpsertResult> CreateOrUpdateRecurringScheduleAsync(
        UpsertRecurringScheduleRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return CreateOrUpdateRecurringScheduleOperation.ExecuteAsync(context, request, cancellationToken);
    }

    public ValueTask<RecurringScheduleTriggerResult> TriggerRecurringScheduleAsync(
        TriggerRecurringScheduleRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return TriggerRecurringScheduleOperation.ExecuteAsync(context, request, cancellationToken);
    }

    public ValueTask<bool> DeleteRecurringScheduleAsync(
        string scheduleKey,
        CancellationToken cancellationToken = default)
      => DeleteRecurringScheduleOperation.ExecuteAsync(context, scheduleKey, cancellationToken);

    public ValueTask<bool> PauseRecurringScheduleAsync(
        string scheduleKey,
        DateTimeOffset pausedAtUtc,
        CancellationToken cancellationToken = default)
      => PauseRecurringScheduleOperation.ExecuteAsync(context, scheduleKey, cancellationToken);

    public ValueTask<bool> ResumeRecurringScheduleAsync(
        string scheduleKey,
        DateTimeOffset resumedAtUtc,
        CancellationToken cancellationToken = default)
      => ResumeRecurringScheduleOperation.ExecuteAsync(context, scheduleKey, cancellationToken);

    public ValueTask<RecurringScheduleInfo?> GetRecurringScheduleAsync(
        string scheduleKey,
        CancellationToken cancellationToken = default)
      => GetRecurringScheduleOperation.ExecuteAsync(context, scheduleKey, cancellationToken);

    public ValueTask<IReadOnlyList<RecurringScheduleInfo>> ListRecurringSchedulesAsync(
        CancellationToken cancellationToken = default)
      => ListRecurringSchedulesOperation.ExecuteAsync(context, cancellationToken);

    public ValueTask<int> MaterializeDueRecurringSchedulesAsync(
        MaterializeDueRecurringSchedulesRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return MaterializeDueRecurringSchedulesOperation.ExecuteAsync(context, request, cancellationToken);
    }

    public ValueTask<JobEvent> AppendAsync(
        AppendJobEventRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return PostgresJobEvents.AppendAsync(context, request, cancellationToken);
    }
}
