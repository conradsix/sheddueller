namespace Sheddueller.Worker.Tests;

using Sheddueller.Storage;

/// <summary>
/// A job store whose members throw <see cref="NotSupportedException"/> unless a test store overrides them, so a test
/// states only the store behaviour it relies on.
/// </summary>
internal abstract class StubJobStore : IJobStore
{
    public virtual ValueTask<EnqueueJobResult> EnqueueAsync(
        EnqueueJobRequest request,
        CancellationToken cancellationToken = default)
      => throw NotStubbed();

    public virtual ValueTask<IReadOnlyList<EnqueueJobResult>> EnqueueManyAsync(
        IReadOnlyList<EnqueueJobRequest> requests,
        CancellationToken cancellationToken = default)
      => throw NotStubbed();

    public virtual ValueTask<ClaimJobResult> TryClaimNextAsync(
        ClaimJobRequest request,
        CancellationToken cancellationToken = default)
      => throw NotStubbed();

    public virtual ValueTask<bool> MarkCompletedAsync(
        CompleteJobRequest request,
        CancellationToken cancellationToken = default)
      => throw NotStubbed();

    public virtual ValueTask<bool> MarkFailedAsync(
        FailJobRequest request,
        CancellationToken cancellationToken = default)
      => throw NotStubbed();

    public virtual ValueTask<bool> RenewLeaseAsync(
        RenewLeaseRequest request,
        CancellationToken cancellationToken = default)
      => throw NotStubbed();

    public virtual ValueTask<bool> ReleaseJobAsync(
        ReleaseJobRequest request,
        CancellationToken cancellationToken = default)
      => throw NotStubbed();

    public virtual ValueTask<int> RecoverExpiredLeasesAsync(
        RecoverExpiredLeasesRequest request,
        CancellationToken cancellationToken = default)
      => throw NotStubbed();

    public virtual ValueTask<JobCancellationResult> CancelAsync(
        CancelJobRequest request,
        CancellationToken cancellationToken = default)
      => throw NotStubbed();

    public virtual ValueTask<int> CancelQueuedJobsAsync(
        CancelQueuedJobsRequest request,
        CancellationToken cancellationToken = default)
      => throw NotStubbed();

    public virtual ValueTask<DateTimeOffset?> GetCancellationRequestedAtAsync(
        JobCancellationStatusRequest request,
        CancellationToken cancellationToken = default)
      => throw NotStubbed();

    public virtual ValueTask<bool> MarkCancellationObservedAsync(
        ObserveJobCancellationRequest request,
        CancellationToken cancellationToken = default)
      => throw NotStubbed();

    public virtual ValueTask RecordWorkerNodeHeartbeatAsync(
        WorkerNodeHeartbeatRequest request,
        CancellationToken cancellationToken = default)
      => throw NotStubbed();

    public virtual ValueTask SetConcurrencyLimitAsync(
        SetConcurrencyLimitRequest request,
        CancellationToken cancellationToken = default)
      => throw NotStubbed();

    public virtual ValueTask SetConcurrencyDefaultLimitAsync(
        SetConcurrencyDefaultLimitRequest request,
        CancellationToken cancellationToken = default)
      => throw NotStubbed();

    public virtual ValueTask ClearConcurrencyLimitOverrideAsync(
        ClearConcurrencyLimitOverrideRequest request,
        CancellationToken cancellationToken = default)
      => throw NotStubbed();

    public virtual ValueTask<int?> GetConfiguredConcurrencyLimitAsync(
        string groupKey,
        CancellationToken cancellationToken = default)
      => throw NotStubbed();

    public virtual ValueTask<RecurringScheduleUpsertResult> CreateOrUpdateRecurringScheduleAsync(
        UpsertRecurringScheduleRequest request,
        CancellationToken cancellationToken = default)
      => throw NotStubbed();

    public virtual ValueTask<RecurringScheduleTriggerResult> TriggerRecurringScheduleAsync(
        TriggerRecurringScheduleRequest request,
        CancellationToken cancellationToken = default)
      => throw NotStubbed();

    public virtual ValueTask<bool> DeleteRecurringScheduleAsync(
        string scheduleKey,
        CancellationToken cancellationToken = default)
      => throw NotStubbed();

    public virtual ValueTask<bool> PauseRecurringScheduleAsync(
        string scheduleKey,
        DateTimeOffset pausedAtUtc,
        CancellationToken cancellationToken = default)
      => throw NotStubbed();

    public virtual ValueTask<bool> ResumeRecurringScheduleAsync(
        string scheduleKey,
        DateTimeOffset resumedAtUtc,
        CancellationToken cancellationToken = default)
      => throw NotStubbed();

    public virtual ValueTask<RecurringScheduleInfo?> GetRecurringScheduleAsync(
        string scheduleKey,
        CancellationToken cancellationToken = default)
      => throw NotStubbed();

    public virtual ValueTask<IReadOnlyList<RecurringScheduleInfo>> ListRecurringSchedulesAsync(
        CancellationToken cancellationToken = default)
      => throw NotStubbed();

    public virtual ValueTask<int> MaterializeDueRecurringSchedulesAsync(
        MaterializeDueRecurringSchedulesRequest request,
        CancellationToken cancellationToken = default)
      => throw NotStubbed();

    private NotSupportedException NotStubbed()
      => new($"{this.GetType().Name} does not stub this job store member.");
}
